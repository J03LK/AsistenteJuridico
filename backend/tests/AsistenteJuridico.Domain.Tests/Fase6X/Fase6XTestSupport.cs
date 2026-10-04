using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Domain.Tests.Fase6;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using AsistenteJuridico.Infrastructure.Services.AI;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AsistenteJuridico.Domain.Tests.Fase6X;

/// <summary>Generadores de archivos reales para las pruebas de extracción (sin binarios en el repositorio).</summary>
internal static class Archivos
{
    public static byte[] Txt(string texto) => Encoding.UTF8.GetBytes(texto);

    public static byte[] Pdf(params string[] paginas)
    {
        var builder = new PdfDocumentBuilder();
        var fuente = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var texto in paginas)
        {
            var pagina = builder.AddPage(PageSize.A4);
            if (texto.Length > 0)
            {
                pagina.AddText(texto, 12, new PdfPoint(25, 700), fuente);
            }
        }

        return builder.Build();
    }

    /// <summary>PDF válido sin capa de texto (como un escaneado).</summary>
    public static byte[] PdfSinTexto() => Pdf(string.Empty);

    /// <summary>Firma %PDF correcta (pasa la validación de subida) pero sin estructura PDF.</summary>
    public static byte[] PdfDanado() => Encoding.ASCII.GetBytes("%PDF-1.4\n%âã\n1 0 obj << /Type /Catalog basura sin cerrar\nstream\u0001\u0002\u0003");

    /// <summary>PDF con diccionario /Encrypt (Standard, R2) cuya contraseña de usuario no es vacía.</summary>
    public static byte[] PdfCifrado()
    {
        var o = new string('A', 32);
        var u = new string('B', 32);
        var objetos = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            $"<< /Filter /Standard /V 1 /R 2 /O ({o}) /U ({u}) /P -4 >>"
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objetos.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objetos[i]}\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objetos.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size {objetos.Length + 1} /Root 1 0 R /Encrypt 4 0 R /ID [<0123456789ABCDEF0123456789ABCDEF> <0123456789ABCDEF0123456789ABCDEF>] >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    public static byte[] Docx(params string[] parrafos)
    {
        using var memoria = new MemoryStream();
        using (var documento = WordprocessingDocument.Create(memoria, WordprocessingDocumentType.Document))
        {
            var principal = documento.AddMainDocumentPart();
            principal.Document = new W.Document(new W.Body(
                parrafos.Select(p => new W.Paragraph(new W.Run(new W.Text(p)))).Cast<OpenXmlElement>()));
        }

        return memoria.ToArray();
    }

    /// <summary>ZIP con [Content_Types].xml y word/ (pasa la validación de subida) pero sin un documento OOXML válido.</summary>
    public static byte[] DocxDanado()
    {
        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (nombre, contenido) in new[]
            {
                ("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
                ("word/document.xml", "<w:document esto no es xml")
            })
            {
                using var escritor = new StreamWriter(zip.CreateEntry(nombre).Open());
                escritor.Write(contenido);
            }
        }

        return memoria.ToArray();
    }

    /// <summary>XLSX con una cadena compartida, una cadena en línea y un número.</summary>
    public static byte[] Xlsx(string compartida, string enLinea, string numero)
    {
        using var memoria = new MemoryStream();
        using (var documento = SpreadsheetDocument.Create(memoria, SpreadsheetDocumentType.Workbook))
        {
            var libro = documento.AddWorkbookPart();
            libro.Workbook = new Workbook();

            var cadenas = libro.AddNewPart<SharedStringTablePart>();
            cadenas.SharedStringTable = new SharedStringTable(new SharedStringItem(new Text(compartida)));

            var hoja = libro.AddNewPart<WorksheetPart>();
            hoja.Worksheet = new Worksheet(new SheetData(new Row(
                new Cell { CellReference = "A1", DataType = CellValues.SharedString, CellValue = new CellValue("0") },
                new Cell { CellReference = "B1", DataType = CellValues.InlineString, InlineString = new InlineString(new Text(enLinea)) },
                new Cell { CellReference = "C1", CellValue = new CellValue(numero) })
            { RowIndex = 1 }));

            libro.Workbook.AppendChild(new Sheets(new Sheet { Id = libro.GetIdOfPart(hoja), SheetId = 1, Name = "Datos" }));
        }

        return memoria.ToArray();
    }

    public static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
}

/// <summary>Almacenamiento real (FileStorageService) sobre un directorio temporal propio.</summary>
internal sealed class AlmacenamientoTemporal : IDisposable
{
    public string Directorio { get; } = Path.Combine(Path.GetTempPath(), "fase6x-" + Guid.NewGuid().ToString("N"));
    public FileStorageService Servicio { get; }

    public AlmacenamientoTemporal()
    {
        Directory.CreateDirectory(Directorio);
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:BasePath"] = Directorio })
            .Build();
        Servicio = new FileStorageService(configuracion, NullLogger<FileStorageService>.Instance);
    }

    /// <summary>Guarda por la ruta de subida real (con su validación) y devuelve el resultado almacenado.</summary>
    public Task<StoredDocumentoFile> SubirAsync(Guid tenantId, Guid expedienteId, byte[] contenido, string nombre, string mime) =>
        Servicio.SaveDocumentoAsync(tenantId, expedienteId, new MemoryStream(contenido), nombre, mime, contenido.Length);

    /// <summary>Escribe un archivo directamente (daño "en reposo", sin pasar por la validación de subida).</summary>
    public string EscribirCrudo(Guid tenantId, Guid expedienteId, byte[] contenido, string extension)
    {
        var relativa = $"{tenantId:N}/{expedienteId:N}/{Guid.NewGuid():N}{extension}";
        var completa = Path.Combine(Directorio, relativa);
        Directory.CreateDirectory(Path.GetDirectoryName(completa)!);
        File.WriteAllBytes(completa, contenido);
        return relativa;
    }

    /// <summary>Symlink de directorio o, en Windows sin privilegios, junction (ambos son puntos de reanálisis).</summary>
    public static void CrearEnlaceDeDirectorio(string enlace, string destino)
    {
        try
        {
            Directory.CreateSymbolicLink(enlace, destino);
            return;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
        }

        var proceso = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{enlace}\" \"{destino}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        proceso.WaitForExit();
        Assert.True(proceso.ExitCode == 0 && Directory.Exists(enlace), "No se pudo crear el enlace: " + proceso.StandardError.ReadToEnd());
    }

    public void Dispose()
    {
        try
        {
            // Primero los enlaces (sin seguirlos), después el resto.
            foreach (var dir in Directory.EnumerateDirectories(Directorio, "*", SearchOption.AllDirectories)
                         .Where(d => new DirectoryInfo(d).LinkTarget != null || new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))
                         .ToList())
            {
                Directory.Delete(dir);
            }

            Directory.Delete(Directorio, recursive: true);
        }
        catch
        {
            // Limpieza de mejor esfuerzo.
        }
    }
}

/// <summary>Proveedor con respuesta fija y un gancho que se ejecuta al recibir la llamada.</summary>
internal sealed class ProveedorFijo : IAIProvider
{
    public const int TokensEntrada = 123;
    public const int TokensSalida = 45;

    public Func<CancellationToken, Task>? AlLlamar { get; set; }
    public Exception? Lanzar { get; set; }
    public string Respuesta { get; set; } = "{\"hechos\":[\"propuesta\"]}";
    public List<AIChatCompletionRequest> Peticiones { get; } = [];

    public string ProviderId => "proveedor-6x";

    public AIProviderCapabilities Capabilities => new(
        MaxContextTokens: 200000,
        MaxOutputTokens: 4096,
        SupportsStreaming: true,
        SupportsSystemPrompt: true,
        SupportedModels: ["modelo-6x"],
        SupportsStructuredOutput: true);

    public int EstimateTokens(string text) => Math.Max(1, text.Length / 4);

    public async Task<AIChatCompletionResponse> CompleteChatAsync(AIChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        Peticiones.Add(request);
        if (AlLlamar != null)
        {
            await AlLlamar(cancellationToken);
        }

        if (Lanzar != null)
        {
            throw Lanzar;
        }

        return new AIChatCompletionResponse(Respuesta, TokensEntrada, TokensSalida, TokensEntrada + TokensSalida, AIFinishReason.Stop, "modelo-6x", ProviderId);
    }

    public async IAsyncEnumerable<AIChatCompletionChunk> StreamChatAsync(
        AIChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Peticiones.Add(request);
        await Task.Yield();
        yield return new AIChatCompletionChunk("respuesta", AIFinishReason.Stop);
    }
}

/// <summary>Almacenamiento que falla si alguien intenta abrir un archivo (invariante D1 del chat).</summary>
internal sealed class AlmacenamientoProhibido : IFileStorageService
{
    public Task DeleteFileIfExistsAsync(string? relativeFilePath, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<Stream> OpenReadFileAsync(string relativeFilePath, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("El chat no debe abrir archivos de documentos.");

    public Task<StoredDocumentoFile> SaveDocumentoAsync(Guid tenantId, Guid expedienteId, Stream fileStream, string originalFileName,
        string? declaredContentType, long? declaredLength, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No se esperaba una subida.");
}

/// <summary>
/// Stream seekable que tarda 2 ms en cada lectura y las cuenta:
/// hace que el análisis de un PDF real supere un timeout corto y permite comprobar si sigue leyendo después.
/// </summary>
internal sealed class StreamLento(byte[] contenido) : MemoryStream(contenido, writable: false)
{
    private int _lecturas;

    public int Lecturas => Volatile.Read(ref _lecturas);

    // Toda lectura pasa por aquí. MemoryStream.Read(Span) llama al Read(byte[]) virtual en las subclases, así que
    // Read(Span) se implementa con un búfer propio para no entrar en recursión.
    public override int Read(byte[] buffer, int offset, int count)
    {
        Interlocked.Increment(ref _lecturas);
        Thread.Sleep(2);
        return base.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        var temporal = new byte[buffer.Length];
        var leidos = Read(temporal, 0, temporal.Length);
        temporal.AsSpan(0, leidos).CopyTo(buffer);
        return leidos;
    }

    public override int ReadByte()
    {
        Interlocked.Increment(ref _lecturas);
        Thread.Sleep(2);
        return base.ReadByte();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));
}

/// <summary>Almacenamiento que entrega siempre el mismo <see cref="StreamLento"/>.</summary>
internal sealed class AlmacenamientoLento(StreamLento stream) : IFileStorageService
{
    public Task DeleteFileIfExistsAsync(string? relativeFilePath, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<Stream> OpenReadFileAsync(string relativeFilePath, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(stream);

    public Task<StoredDocumentoFile> SaveDocumentoAsync(Guid tenantId, Guid expedienteId, Stream fileStream, string originalFileName,
        string? declaredContentType, long? declaredLength, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>Extractor que delega en otro y anota si había una transacción abierta durante cada extracción (S-M2).</summary>
internal sealed class ExtractorObservado(IDocumentTextExtractor interno, ApplicationDbContext contexto) : IDocumentTextExtractor
{
    public List<bool> TransaccionAbierta { get; } = [];
    public int Llamadas { get; private set; }

    public bool IsSupported(string? contentType) => interno.IsSupported(contentType);

    public Task<ExtractionResult> ExtractAsync(Guid documentoId, string? rutaAlmacenamiento, string? contentType, CancellationToken cancellationToken = default)
    {
        Llamadas++;
        TransaccionAbierta.Add(contexto.Database.CurrentTransaction != null);
        return interno.ExtractAsync(documentoId, rutaAlmacenamiento, contentType, cancellationToken);
    }
}

/// <summary>Logger que guarda los mensajes formateados.</summary>
internal sealed class LoggerEnLista<T> : ILogger<T>
{
    public List<string> Mensajes { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Mensajes.Add(formatter(state, exception));
}

/// <summary>Interceptor que hace fallar el guardado del registro de uso independiente (solo AIUsageLog, sin documentos).</summary>
internal sealed class FallarRegistroDeUsoIndependiente : SaveChangesInterceptor
{
    public bool Armado { get; set; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var entradas = eventData.Context!.ChangeTracker.Entries().ToList();
        if (Armado
            && entradas.Any(e => e.Entity is AIUsageLog && e.State == EntityState.Added)
            && !entradas.Any(e => e.Entity is Documento))
        {
            throw new InvalidOperationException("Fallo simulado del registro de uso independiente.");
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>
/// Escenario sobre PostgreSQL real: tenants, usuarios, expedientes y documentos con archivos reales.
/// </summary>
internal sealed class Escenario6X : IDisposable
{
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid OtroTenantId { get; } = Guid.NewGuid();
    public Guid SeniorId { get; } = Guid.NewGuid();
    public Guid JuniorId { get; } = Guid.NewGuid();
    public Guid AsistenteId { get; } = Guid.NewGuid();
    public Guid OtroAsistenteId { get; } = Guid.NewGuid();
    public Guid SeniorOtroTenantId { get; } = Guid.NewGuid();
    public Guid ExpedienteId { get; } = Guid.NewGuid();
    public Guid OtroExpedienteId { get; } = Guid.NewGuid();
    public Guid ExpedienteOtroTenantId { get; } = Guid.NewGuid();

    public AlmacenamientoTemporal Almacenamiento { get; } = new();

    public ApplicationDbContext Contexto(Guid? tenantId = null, params IInterceptor[] interceptores) => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(TestConfiguration.PostgresConnectionString)
            .AddInterceptors(interceptores)
            .Options,
        new TestTenantService { TenantId = tenantId ?? TenantId });

    public async Task SembrarAsync()
    {
        await using var context = Contexto();
        foreach (var (id, slug) in new[] { (TenantId, "fase6x-"), (OtroTenantId, "fase6x-otro-") })
        {
            context.Tenants.Add(new Tenant
            {
                Id = id, Nombre = "Estudio Fase 6.X", IdentificadorUrl = slug + Guid.NewGuid().ToString("N"),
                ZonaHorariaId = "America/Guayaquil", Activo = true
            });
        }

        foreach (var (id, tenant, rol) in new[]
        {
            (SeniorId, TenantId, Roles.AbogadoSenior), (JuniorId, TenantId, Roles.AbogadoJunior),
            (AsistenteId, TenantId, Roles.AsistenteLegal), (OtroAsistenteId, TenantId, Roles.AsistenteLegal),
            (SeniorOtroTenantId, OtroTenantId, Roles.AbogadoSenior)
        })
        {
            var email = $"{id:N}@fase6x.test";
            context.Usuarios.Add(new Usuario
            {
                Id = id, TenantId = tenant, UserName = email, Email = email, NormalizedEmail = email.ToUpperInvariant(),
                NormalizedUserName = email.ToUpperInvariant(), NombreCompleto = "Usuario " + rol, Rol = rol, Activo = true
            });
        }

        var cliente = NuevoCliente(TenantId);
        var clienteOtro = NuevoCliente(OtroTenantId);
        context.AddRange(cliente, clienteOtro,
            NuevoExpediente(ExpedienteId, TenantId, cliente.Id),
            NuevoExpediente(OtroExpedienteId, TenantId, cliente.Id),
            NuevoExpediente(ExpedienteOtroTenantId, OtroTenantId, clienteOtro.Id, SeniorOtroTenantId));
        await context.SaveChangesAsync();
    }

    private static Cliente NuevoCliente(Guid tenantId) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, TipoIdentificacion = TipoIdentificacion.Ruc,
        Identificacion = Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(),
        NombreRazonSocial = "Cliente Fase 6.X", Activo = true
    };

    private Expediente NuevoExpediente(Guid id, Guid tenantId, Guid clienteId, Guid? responsable = null) => new()
    {
        Id = id, TenantId = tenantId, ClienteId = clienteId, NumeroExpediente = "EXP-6X-" + Guid.NewGuid().ToString("N")[..8],
        Titulo = "Caso Fase 6.X", Materia = "Civil", Estado = EstadoExpediente.Abierto, AbogadoResponsableId = responsable ?? SeniorId
    };

    /// <summary>Documento con archivo real subido por la ruta de subida (validación incluida).</summary>
    public async Task<Guid> DocumentoAsync(
        byte[] contenido, string nombre, string mime, Guid? expedienteId = null, Guid? tenantId = null,
        EstadoProcesamientoIa estado = EstadoProcesamientoIa.Pendiente, string? metadatos = null, DateTime? creado = null)
    {
        var tenant = tenantId ?? TenantId;
        var expediente = expedienteId ?? ExpedienteId;
        var archivo = await Almacenamiento.SubirAsync(tenant, expediente, contenido, nombre, mime);
        return await InsertarAsync(tenant, expediente, archivo.RelativePath, archivo.ContentType, archivo.SizeBytes, estado, metadatos, creado, archivo.NombreArchivoOriginal);
    }

    /// <summary>Documento con ruta y ContentType arbitrarios (archivo dañado en reposo, inexistente o ruta insegura).</summary>
    public Task<Guid> DocumentoCrudoAsync(
        string ruta, string contentType, EstadoProcesamientoIa estado = EstadoProcesamientoIa.Pendiente,
        string? metadatos = null, DateTime? creado = null, Guid? expedienteId = null) =>
        InsertarAsync(TenantId, expedienteId ?? ExpedienteId, ruta, contentType, 10, estado, metadatos, creado, "archivo");

    private async Task<Guid> InsertarAsync(
        Guid tenant, Guid expediente, string ruta, string contentType, long tamanio, EstadoProcesamientoIa estado,
        string? metadatos, DateTime? creado, string? nombreOriginal)
    {
        await using var context = Contexto(tenant);
        var id = Guid.NewGuid();
        context.Documentos.Add(new Documento
        {
            Id = id, TenantId = tenant, ExpedienteId = expediente, Titulo = "Documento " + id.ToString("N")[..6],
            TipoDocumento = "Escrito", NombreArchivoOriginal = nombreOriginal, RutaAlmacenamiento = ruta, ContentType = contentType,
            TamanioBytes = tamanio, EstadoIa = estado, MetadatosJson = metadatos, CreatedAt = creado ?? DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        return id;
    }

    public async Task TareaAsync(Guid asignadoA, EstadoTarea estado, Guid? expedienteId = null, Guid? tenantId = null)
    {
        var tenant = tenantId ?? TenantId;
        await using var context = Contexto(tenant);
        context.Tareas.Add(new Tarea
        {
            Id = Guid.NewGuid(), TenantId = tenant, ExpedienteId = expedienteId ?? ExpedienteId, AsignadoAUsuarioId = asignadoA,
            Titulo = "Tarea " + estado, Estado = estado, Prioridad = Prioridad.Media, FechaVencimiento = DateTime.UtcNow.AddDays(3)
        });
        await context.SaveChangesAsync();
    }

    public AIService Servicio(
        ApplicationDbContext context, Guid usuarioId, string rol, IAIProvider proveedor,
        IDocumentTextExtractor? extractor = null, IFileStorageService? almacenamiento = null, Guid? tenantId = null,
        ILogger<AIService>? logger = null)
    {
        var tenant = tenantId ?? TenantId;
        var tenantService = new TestTenantService { TenantId = tenant };
        var userService = new TestUserService { UserId = usuarioId, TenantId = tenant, Role = rol, Email = $"{usuarioId:N}@fase6x.test" };
        var storage = almacenamiento ?? Almacenamiento.Servicio;
        return new AIService(
            context, proveedor, tenantService, userService, new ExpedienteAccessService(context, userService, tenantService),
            storage, logger ?? NullLogger<AIService>.Instance, extractor ?? new DocumentTextExtractor(storage));
    }

    public async Task<Documento> LeerDocumentoAsync(Guid id)
    {
        await using var context = Contexto();
        return await context.Documentos.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == id);
    }

    public async Task<List<HistorialAuditoria>> AuditoriasAsync(Guid documentoId, string accion)
    {
        await using var context = Contexto();
        return await context.HistorialAuditorias.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.EntidadId == documentoId.ToString() && a.Accion == accion).ToListAsync();
    }

    public async Task<int> ConversacionesAsync(Guid usuarioId)
    {
        await using var context = Contexto();
        return await context.AIConversations.IgnoreQueryFilters().CountAsync(c => c.UsuarioId == usuarioId);
    }

    public async Task<List<AIUsageLog>> UsosAsync(Guid usuarioId)
    {
        await using var context = Contexto();
        return await context.AIUsageLogs.IgnoreQueryFilters().AsNoTracking().Where(u => u.UsuarioId == usuarioId).ToListAsync();
    }

    public async Task EliminarExpedienteAsync(Guid expedienteId)
    {
        await using var context = Contexto();
        var expediente = await context.Expedientes.IgnoreQueryFilters().SingleAsync(e => e.Id == expedienteId);
        expediente.IsDeleted = true;
        expediente.DeletedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    public void Dispose() => Almacenamiento.Dispose();
}
