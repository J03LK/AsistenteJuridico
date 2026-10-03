using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Documentos.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using System.Text.RegularExpressions;
using AsistenteJuridico.Application.Common.DTOs;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AppValidationException = AsistenteJuridico.Application.Common.Exceptions.ValidationException;

namespace AsistenteJuridico.Infrastructure.Services;

public class DocumentoService : IDocumentoService
{
    private readonly ApplicationDbContext _context;
    private readonly IFileStorageService _fileStorageService;
    private readonly IExpedienteAccessService _expedienteAccessService;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly ICurrentUserService _currentUserService;
    private readonly IAuditService _auditService;
    private readonly IValidator<UploadDocumentoDto> _uploadValidator;
    private readonly IValidator<UpdateDocumentoDto> _updateValidator;
    private readonly IValidator<DocumentoFilterRequest> _filterValidator;
    private readonly ILogger<DocumentoService> _logger;

    public DocumentoService(
        ApplicationDbContext context,
        IFileStorageService fileStorageService,
        IExpedienteAccessService expedienteAccessService,
        ICurrentTenantService currentTenantService,
        ICurrentUserService currentUserService,
        IAuditService auditService,
        IValidator<UploadDocumentoDto> uploadValidator,
        IValidator<UpdateDocumentoDto> updateValidator,
        IValidator<DocumentoFilterRequest> filterValidator,
        ILogger<DocumentoService>? logger = null)
    {
        _context = context;
        _fileStorageService = fileStorageService;
        _expedienteAccessService = expedienteAccessService;
        _currentTenantService = currentTenantService;
        _currentUserService = currentUserService;
        _auditService = auditService;
        _uploadValidator = uploadValidator;
        _updateValidator = updateValidator;
        _filterValidator = filterValidator;
        _logger = logger ?? NullLogger<DocumentoService>.Instance;
    }

    private void EnsureTenantAndNotSuperAdmin()
    {
        if (_currentUserService.Role == Roles.SuperAdmin)
        {
            throw new ForbiddenException("Los administradores globales (SuperAdmin) no tienen acceso a documentos jurídicos.");
        }

        if (!_currentTenantService.TenantId.HasValue)
        {
            throw new ForbiddenException("Contexto de Tenant no especificado.");
        }
    }

    public async Task<DocumentoDto> UploadDocumentoAsync(
        UploadDocumentoDto dto,
        Stream fileStream,
        string originalFileName,
        string declaredContentType,
        long? declaredLength = null,
        CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();

        var validationResult = await _uploadValidator.ValidateAsync(dto, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new AppValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        // Autorización jerárquica: Debe tener acceso de escritura sobre el expediente padre
        await _expedienteAccessService.EnsureCanAccessExpedienteAsync(dto.ExpedienteId, requireWriteAccess: true, cancellationToken);

        var tenantId = _currentTenantService.TenantId!.Value;

        // Fase 1 (7.2): guardado físico seguro. Valida nombre, extensión (415), MIME (415), tamaño (413) y contenido
        // (400), calcula el SHA-256 sobre un temporal y lo mueve a {tenant}/{expediente}/{guid}{ext}.
        var archivo = await _fileStorageService.SaveDocumentoAsync(
            tenantId, dto.ExpedienteId, fileStream, originalFileName, declaredContentType, declaredLength, cancellationToken);

        // Fase 2: Inserción en base de datos, con la auditoría en el MISMO SaveChanges, y compensación si falla
        Documento documento;
        try
        {
            documento = new Documento
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ExpedienteId = dto.ExpedienteId,
                Titulo = dto.Titulo.Trim(),
                TipoDocumento = dto.TipoDocumento.Trim(),
                Descripcion = string.IsNullOrWhiteSpace(dto.Descripcion) ? null : dto.Descripcion.Trim(),
                NombreArchivoOriginal = archivo.NombreArchivoOriginal,
                RutaAlmacenamiento = archivo.RelativePath,
                ContentType = archivo.ContentType,
                TamanioBytes = archivo.SizeBytes,
                HashSha256 = archivo.Sha256Hash,
                EstadoIa = EstadoProcesamientoIa.Pendiente,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = _currentUserService.Email
            };

            AsegurarInvariantesDeDocumentoNuevo(documento);

            _context.Documentos.Add(documento);

            // Fase 7.4: auditoría transaccional. Sin ruta física, sin contenido. sha256Contenido es exclusivamente el
            // SHA-256 calculado durante el streaming de la subida (invariante comprobada arriba).
            await _auditService.LogInTransactionAsync("Documento", documento.Id.ToString(), "UPLOAD", null, new
            {
                documento.ExpedienteId,
                documento.Titulo,
                documento.TipoDocumento,
                documento.Descripcion,
                documento.NombreArchivoOriginal,
                documento.ContentType,
                documento.TamanioBytes,
                Sha256Contenido = documento.HashSha256
            }, cancellationToken);

            await _context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Compensación obligatoria: si la BD (documento o auditoría) falla tras mover el archivo, eliminar el
            // archivo físico huérfano. CancellationToken.None: la compensación debe ejecutarse aunque la petición se
            // haya cancelado.
            await _fileStorageService.DeleteFileIfExistsAsync(archivo.RelativePath, CancellationToken.None);
            throw;
        }

        var expediente = await _context.Expedientes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == dto.ExpedienteId, cancellationToken);

        return ToDto(documento, expediente?.NumeroExpediente);
    }

    /// <summary>
    /// Fase 7.3 — Descarga endurecida: autoriza ANTES de abrir el archivo (tenant del JWT, documento y expediente
    /// activos, reglas de rol) y abre solo la ruta guardada en la base de datos, con las defensas de la 7.2
    /// (ruta insegura o enlace -> 403). Archivo inexistente -> 404 DOCUMENT_FILE_NOT_FOUND. Sin recálculo de SHA-256.
    /// </summary>
    public async Task<DocumentoDownloadResult> DownloadDocumentoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var documento = await EnsureCanAccessDocumentoAsync(id, requireWriteAccess: false, cancellationToken);

        Stream stream;
        try
        {
            stream = await _fileStorageService.OpenReadFileAsync(documento.RutaAlmacenamiento, cancellationToken);
        }
        catch (NotFoundException ex) when (ex.ErrorCode == null)
        {
            // La autorización ya pasó: este 404 solo puede venir del almacenamiento. Nunca se registra la ruta física.
            _logger.LogError(
                "[DOCUMENT_FILE_NOT_FOUND] El archivo físico del documento {DocumentoId} (tenant {TenantId}) no existe en el almacenamiento.",
                documento.Id, documento.TenantId);
            throw new NotFoundException("El archivo del documento no está disponible.") { ErrorCode = DocumentoErrorCodes.FileNotFound };
        }

        // Fase 7.4: solo se audita una descarga que se va a servir (el archivo se abrió). Sin ruta, sin hash, sin
        // contenido. Auditoría independiente: si falla, la descarga continúa (AuditService deja
        // [AUDITORIA_NO_REGISTRADA]; esta captura cubre además cualquier excepción que escapara de LogAsync).
        try
        {
            await _auditService.LogAsync("Documento", documento.Id.ToString(), "DOWNLOAD", null, new
            {
                documento.ExpedienteId,
                documento.TamanioBytes
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "[AUDITORIA_NO_REGISTRADA] No se pudo guardar la auditoría DOWNLOAD del documento {DocumentoId} (tenant {TenantId}). Error: {TipoError}.",
                documento.Id, documento.TenantId, ex.GetType().Name);
        }

        return new DocumentoDownloadResult(stream, documento.ContentType, NombreDeDescarga(documento));
    }

    public async Task<PagedResult<DocumentoDto>> GetDocumentosPagedAsync(DocumentoFilterRequest filtro, CancellationToken cancellationToken = default)
    {
        var validacion = await _filterValidator.ValidateAsync(filtro, cancellationToken);
        if (!validacion.IsValid)
        {
            throw new AppValidationException(validacion.Errors.Select(e => e.ErrorMessage));
        }

        var consulta = await ConstruirConsultaListadoAsync(filtro, cancellationToken);

        var total = await consulta.CountAsync(cancellationToken);
        var items = await consulta
            .Skip((filtro.PageNumber - 1) * filtro.PageSize)
            .Take(filtro.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<DocumentoDto>(items, total, filtro.PageNumber, filtro.PageSize);
    }

    /// <summary>
    /// Ruta obsoleta (alias): la MISMA consulta que el listado oficial (autorización, tenant, orden y DTO), sin
    /// filtros adicionales y sin paginar, para conservar su forma de respuesta.
    /// </summary>
    public async Task<IReadOnlyList<DocumentoDto>> GetDocumentosByExpedienteAsync(Guid expedienteId, CancellationToken cancellationToken = default)
    {
        var consulta = await ConstruirConsultaListadoAsync(new DocumentoFilterRequest { ExpedienteId = expedienteId }, cancellationToken);
        return await consulta.ToListAsync(cancellationToken);
    }

    public async Task<DocumentoDto> GetDocumentoByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var documento = await EnsureCanAccessDocumentoAsync(id, requireWriteAccess: false, cancellationToken);

        return ToDto(documento, documento.Expediente.NumeroExpediente);
    }

    /// <summary>
    /// Fase 7.3 — PUT de metadatos. Solo asigna Titulo, TipoDocumento y Descripcion (más UpdatedAt/UpdatedBy, que fija
    /// el sistema). Orden: autorización -> validación -> Procesando (409) -> versión (409) -> UPDATE con WHERE xmin.
    /// </summary>
    public async Task<DocumentoDto> UpdateDocumentoAsync(Guid id, UpdateDocumentoDto dto, CancellationToken cancellationToken = default)
    {
        var documento = await EnsureCanAccessDocumentoAsync(id, requireWriteAccess: true, cancellationToken);

        var validacion = await _updateValidator.ValidateAsync(dto, cancellationToken);
        if (!validacion.IsValid)
        {
            throw new AppValidationException(validacion.Errors.Select(e => e.ErrorMessage));
        }

        EnsureNoProcesando(documento);
        var version = dto.Version!.Value;
        EnsureVersionVigente(documento, version);

        var titulo = dto.Titulo!.Trim();
        var tipoDocumento = dto.TipoDocumento!.Trim();
        var descripcion = string.IsNullOrWhiteSpace(dto.Descripcion) ? null : dto.Descripcion.Trim();

        if (titulo == documento.Titulo && tipoDocumento == documento.TipoDocumento && descripcion == documento.Descripcion)
        {
            // Sin cambios: no se escribe nada y la versión no cambia.
            return ToDto(documento, documento.Expediente.NumeroExpediente);
        }

        // Fase 7.4: la auditoría contiene EXACTAMENTE los campos que cambian (claves camelCase explícitas; un null en
        // descripcion se registra porque representa el cambio).
        var anteriores = new Dictionary<string, object?>();
        var nuevos = new Dictionary<string, object?>();
        RegistrarCambio("titulo", documento.Titulo, titulo, anteriores, nuevos);
        RegistrarCambio("tipoDocumento", documento.TipoDocumento, tipoDocumento, anteriores, nuevos);
        RegistrarCambio("descripcion", documento.Descripcion, descripcion, anteriores, nuevos);

        documento.Titulo = titulo;
        documento.TipoDocumento = tipoDocumento;
        documento.Descripcion = descripcion;
        documento.UpdatedAt = DateTime.UtcNow;
        documento.UpdatedBy = _currentUserService.Email;

        // Mismo SaveChanges que el UPDATE con WHERE xmin: conflicto o error -> ni cambio ni auditoría.
        await _auditService.LogInTransactionAsync("Documento", documento.Id.ToString(), "UPDATE", anteriores, nuevos, cancellationToken);
        await GuardarConVersionAsync(documento, version, cancellationToken);

        return ToDto(documento, documento.Expediente.NumeroExpediente);
    }

    /// <summary>
    /// Fase 7.3 — Borrado lógico con versión obligatoria. El archivo físico y RutaAlmacenamiento se conservan.
    /// Fase 7.4 — Auditoría DELETE transaccional, SIN RutaAlmacenamiento.
    /// </summary>
    public async Task DeleteDocumentoAsync(Guid id, uint? version, CancellationToken cancellationToken = default)
    {
        var documento = await EnsureCanAccessDocumentoAsync(id, requireWriteAccess: true, cancellationToken);

        // Después de la autorización (D73-6): un 404/403 tiene prioridad sobre el 400.
        // Contrato v1.1: ausente -> 400; 0 -> 400; positiva obsoleta -> 409.
        if (version is null or 0)
        {
            throw new AppValidationException([version is null
                ? "La versión del documento es obligatoria."
                : "La versión del documento no es válida."]);
        }

        EnsureNoProcesando(documento);
        EnsureVersionVigente(documento, version.Value);

        documento.IsDeleted = true;
        documento.DeletedAt = DateTime.UtcNow;
        documento.DeletedBy = _currentUserService.Email;

        // Fase 7.4: en el mismo SaveChanges que el borrado con WHERE xmin. Las propiedades nulas se omiten
        // (AuditService no escribe nulos): un documento antiguo sin hash NO lleva la clave sha256Contenido.
        await _auditService.LogInTransactionAsync("Documento", documento.Id.ToString(), "DELETE", new
        {
            documento.ExpedienteId,
            documento.Titulo,
            documento.NombreArchivoOriginal,
            Sha256Contenido = Sha256ContenidoAuditable(documento.HashSha256)
        }, null, cancellationToken);

        await GuardarConVersionAsync(documento, version.Value, cancellationToken);
    }

    /// <summary>
    /// Formato de la huella documental: exactamente 64 hexadecimales en minúsculas (igual que la CHECK
    /// CK_documentos_HashSha256_formato). \z en lugar de $ para no aceptar un salto de línea final.
    /// </summary>
    private static readonly Regex FormatoSha256 = new("^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Invariantes de todo documento nuevo (Fase 7.4): hash SHA-256 presente y válido, calculado durante el streaming
    /// de la subida, y tamaño mayor que 0. Si no se cumplen no se crea el documento (error interno, sin datos).
    /// </summary>
    private static void AsegurarInvariantesDeDocumentoNuevo(Documento documento)
    {
        if (documento.HashSha256 is null || !FormatoSha256.IsMatch(documento.HashSha256))
        {
            throw new InvalidOperationException("Invariante de documento nuevo incumplida: el SHA-256 del contenido no es válido.");
        }

        if (documento.TamanioBytes <= 0)
        {
            throw new InvalidOperationException("Invariante de documento nuevo incumplida: el tamaño del contenido debe ser mayor que 0.");
        }
    }

    /// <summary>
    /// sha256Contenido solo admite el SHA-256 documental válido; cualquier otro valor (o null) se omite del evento.
    /// </summary>
    private static string? Sha256ContenidoAuditable(string? hash) =>
        hash is not null && FormatoSha256.IsMatch(hash) ? hash : null;

    private static void RegistrarCambio(string clave, string? anterior, string? nuevo,
        Dictionary<string, object?> anteriores, Dictionary<string, object?> nuevos)
    {
        if (!string.Equals(anterior, nuevo, StringComparison.Ordinal))
        {
            anteriores[clave] = anterior;
            nuevos[clave] = nuevo;
        }
    }

    /// <summary>
    /// Consulta única del listado (oficial y alias): autorización documental del expediente (D-1), tenant explícito
    /// del contexto, filtros, orden total determinista CreatedAt DESC, Id DESC y proyección al DTO sin ruta física.
    /// </summary>
    private async Task<IQueryable<DocumentoDto>> ConstruirConsultaListadoAsync(DocumentoFilterRequest filtro, CancellationToken cancellationToken)
    {
        var expedienteId = filtro.ExpedienteId!.Value;

        // Fase 7 (D-1): misma regla documental que el detalle, incluida la del AsistenteLegal.
        try
        {
            await _expedienteAccessService.EnsureCanAccessDocumentosDeExpedienteAsync(expedienteId, cancellationToken);
        }
        catch (ForbiddenException ex) when (ex.ErrorCode == null)
        {
            // El 404 de un expediente inexistente o eliminado se deja sin código: no es un documento inexistente.
            throw new ForbiddenException(ex.Message) { ErrorCode = DocumentoErrorCodes.AccessDenied };
        }

        var tenantId = _currentTenantService.TenantId!.Value;
        var consulta = _context.Documentos
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.ExpedienteId == expedienteId && !d.IsDeleted);

        if (!string.IsNullOrWhiteSpace(filtro.TipoDocumento))
        {
            var tipo = filtro.TipoDocumento.Trim();
            consulta = consulta.Where(d => d.TipoDocumento == tipo);
        }

        if (filtro.FechaDesde.HasValue)
        {
            var desde = ComoUtc(filtro.FechaDesde.Value);
            consulta = consulta.Where(d => d.CreatedAt >= desde);
        }

        if (filtro.FechaHasta.HasValue)
        {
            var hasta = ComoUtc(filtro.FechaHasta.Value);
            consulta = consulta.Where(d => d.CreatedAt <= hasta);
        }

        if (!string.IsNullOrWhiteSpace(filtro.SearchTerm))
        {
            var busqueda = filtro.SearchTerm.Trim().ToLower();
            consulta = consulta.Where(d => d.Titulo.ToLower().Contains(busqueda)
                || (d.NombreArchivoOriginal != null && d.NombreArchivoOriginal.ToLower().Contains(busqueda)));
        }

        return consulta
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .Select(d => new DocumentoDto(
                d.Id,
                d.TenantId,
                d.ExpedienteId,
                d.Expediente.NumeroExpediente,
                d.Titulo,
                d.TipoDocumento,
                d.Descripcion,
                d.NombreArchivoOriginal,
                d.ContentType,
                d.TamanioBytes,
                d.HashSha256,
                d.EstadoIa,
                d.EstadoIa.ToString(),
                d.CreatedAt,
                d.CreatedBy,
                d.UpdatedAt,
                d.Version));
    }

    /// <summary>
    /// D7: mientras la IA procesa el documento (Fase 6) no se edita ni se borra. Solo se LEE EstadoIa.
    /// </summary>
    private static void EnsureNoProcesando(Documento documento)
    {
        if (documento.EstadoIa == EstadoProcesamientoIa.Procesando)
        {
            throw new ConflictException("El documento se está procesando con IA y no puede modificarse ni eliminarse hasta que termine.")
            {
                ErrorCode = DocumentoErrorCodes.Processing
            };
        }
    }

    /// <summary>Comprobación previa: si la versión cargada ya difiere de la del cliente, 409 sin escribir.</summary>
    private static void EnsureVersionVigente(Documento documento, uint version)
    {
        if (documento.Version != version)
        {
            throw ConflictoDeVersion();
        }
    }

    /// <summary>
    /// Guarda con la versión del cliente como valor original de xmin: PostgreSQL ejecuta
    /// UPDATE ... WHERE "Id" = @id AND "xmin" = @version. Si otro proceso confirmó un cambio entre la carga y el
    /// guardado, afecta 0 filas -> DbUpdateConcurrencyException -> 409. Nunca hay sobrescritura silenciosa.
    /// </summary>
    private async Task GuardarConVersionAsync(Documento documento, uint version, CancellationToken cancellationToken)
    {
        _context.Entry(documento).Property(d => d.Version).OriginalValue = version;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ConflictoDeVersion();
        }
    }

    private static ConflictException ConflictoDeVersion() =>
        new("El documento fue modificado por otra operación. Recárguelo y vuelva a intentarlo.")
        {
            ErrorCode = DocumentoErrorCodes.ConcurrencyConflict
        };

    private static DateTime ComoUtc(DateTime fecha) => fecha.Kind switch
    {
        DateTimeKind.Utc => fecha,
        DateTimeKind.Local => fecha.ToUniversalTime(),
        _ => DateTime.SpecifyKind(fecha, DateTimeKind.Utc)
    };

    /// <summary>
    /// Nombre de descarga: NombreArchivoOriginal (ya saneado en la subida). Filas antiguas sin él: Titulo saneado
    /// (separadores de ruta -> '_') más la extensión física. Nunca la ruta.
    /// </summary>
    private static string NombreDeDescarga(Documento documento)
    {
        if (!string.IsNullOrWhiteSpace(documento.NombreArchivoOriginal))
        {
            return documento.NombreArchivoOriginal;
        }

        var extension = Path.GetExtension(documento.RutaAlmacenamiento);
        var titulo = documento.Titulo.Replace('/', '_').Replace('\\', '_');
        try
        {
            return NombreArchivoSanitizer.Sanitizar(titulo + extension);
        }
        catch (AppValidationException)
        {
            return "documento" + extension;
        }
    }

    /// <summary>
    /// Autoriza el acceso a un documento y traduce los 404/403 del servicio de acceso compartido a los códigos
    /// DOCUMENT_* (Fase 7). El mensaje y el código HTTP no cambian; el servicio de acceso y la IA siguen igual.
    /// </summary>
    private async Task<Documento> EnsureCanAccessDocumentoAsync(Guid id, bool requireWriteAccess, CancellationToken cancellationToken)
    {
        try
        {
            return await _expedienteAccessService.EnsureCanAccessDocumentoAsync(id, requireWriteAccess, cancellationToken);
        }
        catch (NotFoundException ex) when (ex.ErrorCode == null)
        {
            throw new NotFoundException(ex.Message) { ErrorCode = DocumentoErrorCodes.NotFound };
        }
        catch (ForbiddenException ex) when (ex.ErrorCode == null)
        {
            throw new ForbiddenException(ex.Message) { ErrorCode = DocumentoErrorCodes.AccessDenied };
        }
    }

    /// <summary>Fase 7: el DTO nunca incluye RutaAlmacenamiento.</summary>
    private static DocumentoDto ToDto(Documento documento, string? expedienteNumero) => new(
        documento.Id,
        documento.TenantId,
        documento.ExpedienteId,
        expedienteNumero,
        documento.Titulo,
        documento.TipoDocumento,
        documento.Descripcion,
        documento.NombreArchivoOriginal,
        documento.ContentType,
        documento.TamanioBytes,
        documento.HashSha256,
        documento.EstadoIa,
        documento.EstadoIa.ToString(),
        documento.CreatedAt,
        documento.CreatedBy,
        documento.UpdatedAt,
        documento.Version);
}
