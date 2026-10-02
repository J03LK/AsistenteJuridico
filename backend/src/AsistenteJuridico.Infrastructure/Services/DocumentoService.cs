using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Documentos.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
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

    public DocumentoService(
        ApplicationDbContext context,
        IFileStorageService fileStorageService,
        IExpedienteAccessService expedienteAccessService,
        ICurrentTenantService currentTenantService,
        ICurrentUserService currentUserService,
        IAuditService auditService,
        IValidator<UploadDocumentoDto> uploadValidator)
    {
        _context = context;
        _fileStorageService = fileStorageService;
        _expedienteAccessService = expedienteAccessService;
        _currentTenantService = currentTenantService;
        _currentUserService = currentUserService;
        _auditService = auditService;
        _uploadValidator = uploadValidator;
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

        // Fase 1: Guardado físico seguro (valida magic bytes, genera UUID, computa hash SHA-256)
        var (physicalFileName, relativePath, verifiedContentType, fileSizeBytes, sha256Hash) =
            await _fileStorageService.SaveFileAsync(tenantId, fileStream, originalFileName, declaredContentType, cancellationToken);

        // Fase 2: Inserción en base de datos con compensación transaccional si falla
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
                RutaAlmacenamiento = relativePath,
                ContentType = verifiedContentType,
                TamanioBytes = fileSizeBytes,
                HashSha256 = sha256Hash,
                EstadoIa = EstadoProcesamientoIa.Pendiente,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = _currentUserService.Email
            };

            _context.Documentos.Add(documento);
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Compensación obligatoria: Si la BD falla tras escribir el archivo, eliminar el archivo físico huérfano
            await _fileStorageService.DeleteFileIfExistsAsync(relativePath, cancellationToken);
            throw;
        }

        // Registro de auditoría estricto (NUNCA incluir binario ni base64)
        await _auditService.LogAsync("Documento", documento.Id.ToString(), "UPLOAD", null, new
        {
            documento.ExpedienteId,
            documento.Titulo,
            documento.TipoDocumento,
            documento.TamanioBytes,
            documento.HashSha256,
            documento.ContentType
        }, cancellationToken);

        var expediente = await _context.Expedientes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == dto.ExpedienteId, cancellationToken);

        return new DocumentoDto(
            documento.Id,
            documento.TenantId,
            documento.ExpedienteId,
            expediente?.NumeroExpediente,
            documento.Titulo,
            documento.TipoDocumento,
            documento.RutaAlmacenamiento,
            documento.ContentType,
            documento.TamanioBytes,
            documento.HashSha256,
            documento.EstadoIa,
            documento.EstadoIa.ToString(),
            documento.CreatedAt,
            documento.UpdatedAt,
            documento.Version);
    }

    public async Task<DocumentoDownloadResult> DownloadDocumentoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var documento = await _expedienteAccessService.EnsureCanAccessDocumentoAsync(id, requireWriteAccess: false, cancellationToken);

        var stream = await _fileStorageService.OpenReadFileAsync(documento.RutaAlmacenamiento, cancellationToken);

        var extension = Path.GetExtension(documento.RutaAlmacenamiento);
        var safeFileName = $"{documento.Titulo.Replace(" ", "_")}{extension}";

        return new DocumentoDownloadResult(stream, documento.ContentType, safeFileName);
    }

    public async Task<IReadOnlyList<DocumentoDto>> GetDocumentosByExpedienteAsync(Guid expedienteId, CancellationToken cancellationToken = default)
    {
        await _expedienteAccessService.EnsureCanAccessExpedienteAsync(expedienteId, requireWriteAccess: false, cancellationToken);

        var documentos = await _context.Documentos
            .AsNoTracking()
            .Include(d => d.Expediente)
            .Where(d => d.ExpedienteId == expedienteId && !d.IsDeleted)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DocumentoDto(
                d.Id,
                d.TenantId,
                d.ExpedienteId,
                d.Expediente != null ? d.Expediente.NumeroExpediente : null,
                d.Titulo,
                d.TipoDocumento,
                d.RutaAlmacenamiento,
                d.ContentType,
                d.TamanioBytes,
                d.HashSha256,
                d.EstadoIa,
                d.EstadoIa.ToString(),
                d.CreatedAt,
                d.UpdatedAt,
                d.Version))
            .ToListAsync(cancellationToken);

        return documentos;
    }

    public async Task<DocumentoDto> GetDocumentoByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var documento = await _expedienteAccessService.EnsureCanAccessDocumentoAsync(id, requireWriteAccess: false, cancellationToken);

        return new DocumentoDto(
            documento.Id,
            documento.TenantId,
            documento.ExpedienteId,
            documento.Expediente?.NumeroExpediente,
            documento.Titulo,
            documento.TipoDocumento,
            documento.RutaAlmacenamiento,
            documento.ContentType,
            documento.TamanioBytes,
            documento.HashSha256,
            documento.EstadoIa,
            documento.EstadoIa.ToString(),
            documento.CreatedAt,
            documento.UpdatedAt,
            documento.Version);
    }

    public async Task DeleteDocumentoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var documento = await _expedienteAccessService.EnsureCanAccessDocumentoAsync(id, requireWriteAccess: true, cancellationToken);

        documento.IsDeleted = true;
        documento.DeletedAt = DateTime.UtcNow;
        documento.DeletedBy = _currentUserService.Email;

        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("Documento", documento.Id.ToString(), "DELETE", new
        {
            documento.Titulo,
            documento.RutaAlmacenamiento
        }, null, cancellationToken);
    }
}
