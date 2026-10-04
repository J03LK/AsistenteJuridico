using System.Collections.Concurrent;
using AsistenteJuridico.Application.Common.Indexacion;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Indexacion;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.BackgroundServices;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Persistence.Interceptors;
using AsistenteJuridico.Infrastructure.Services;
using AsistenteJuridico.Infrastructure.Services.AI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AsistenteJuridico.Domain.Tests.Fase8;

[CollectionDefinition(Nombre)]
public sealed class Coleccion84 : ICollectionFixture<Entorno84>
{
    public const string Nombre = "Fase84";
}

/// <summary>
/// Proveedor de embeddings programable para las pruebas de la 8.4 (FASE_8_3_CONTRATO.md §16): vectores deterministas
/// del proveedor simulado, con ganchos para fallos, consumo y bloqueo. Aplica la misma validación de lote que los
/// proveedores reales (máximo absoluto de 64).
/// </summary>
public sealed class ProveedorEmbeddingsProgramable : IEmbeddingProvider
{
    private readonly MockEmbeddingProvider _mock = new();
    private int _llamadas;
    private int _enVuelo;
    private int _maxEnVuelo;

    public string ProviderId { get; set; } = MockEmbeddingProvider.Proveedor;
    public string ModelId { get; set; } = MockEmbeddingProvider.Modelo;
    public int Dimensiones => 1536;
    public int MaxTokensPorEntrada => 8191;
    public int MaxEntradasPorLote { get; set; } = 64;

    /// <summary>Gancho por llamada (número de llamada base 1, textos, token). Devuelve null para el comportamiento normal.</summary>
    public Func<int, IReadOnlyList<string>, CancellationToken, Task<EmbeddingBatchResult?>>? AlLlamar { get; set; }

    /// <summary>Tokens que "informa el proveedor" por llamada; null = no informado. Sin gancho: los del simulado.</summary>
    public Func<int, int?>? Tokens { get; set; }

    public ConcurrentQueue<IReadOnlyList<string>> Lotes { get; } = new();
    public int Llamadas => Volatile.Read(ref _llamadas);
    public int MaximoEnVuelo => Volatile.Read(ref _maxEnVuelo);

    public void Reiniciar()
    {
        ProviderId = MockEmbeddingProvider.Proveedor;
        ModelId = MockEmbeddingProvider.Modelo;
        MaxEntradasPorLote = 64;
        AlLlamar = null;
        Tokens = null;
        Lotes.Clear();
        Interlocked.Exchange(ref _llamadas, 0);
        Interlocked.Exchange(ref _enVuelo, 0);
        Interlocked.Exchange(ref _maxEnVuelo, 0);
    }

    public async Task<EmbeddingBatchResult> EmbedAsync(IReadOnlyList<string> entradas, EmbeddingPurpose proposito, CancellationToken ct)
    {
        LoteEmbeddings.Validar(entradas, MaxEntradasPorLote, MaxTokensPorEntrada);
        var n = Interlocked.Increment(ref _llamadas);
        Lotes.Enqueue(entradas.ToList());
        var enVuelo = Interlocked.Increment(ref _enVuelo);
        int maximo;
        while (enVuelo > (maximo = Volatile.Read(ref _maxEnVuelo)) && Interlocked.CompareExchange(ref _maxEnVuelo, enVuelo, maximo) != maximo)
        {
        }

        try
        {
            if (AlLlamar != null && await AlLlamar(n, entradas, ct) is { } forzado)
            {
                return forzado;
            }

            var r = await _mock.EmbedAsync(entradas, proposito, ct);
            return r with
            {
                ProviderId = ProviderId,
                ModelId = ModelId,
                ModeloDeclarado = ModelId,
                TokensEntrada = Tokens != null ? Tokens(n) : r.TokensEntrada
            };
        }
        finally
        {
            Interlocked.Decrement(ref _enVuelo);
        }
    }
}

/// <summary>Extractor real con la posibilidad de forzar un estado (p. ej. ExtractionFailed) sin tocar archivos.</summary>
public sealed class ControlDeExtraccion
{
    public ExtractionStatus? Forzar { get; set; }
}

internal sealed class ExtractorProgramable(IDocumentTextExtractor real, ControlDeExtraccion control) : IDocumentTextExtractor
{
    public bool IsSupported(string? contentType) => real.IsSupported(contentType);

    public Task<ExtractionResult> ExtractAsync(Guid documentoId, string? rutaAlmacenamiento, string? contentType, CancellationToken cancellationToken = default) =>
        real.ExtractAsync(documentoId, rutaAlmacenamiento, contentType, cancellationToken);

    public Task<SegmentedExtractionResult> ExtractSegmentsAsync(Guid documentoId, string? rutaAlmacenamiento, string? contentType,
        ExtractionProfile perfil, CancellationToken cancellationToken = default) =>
        control.Forzar is { } estado
            ? Task.FromResult(new SegmentedExtractionResult(documentoId, estado, [], null))
            : real.ExtractSegmentsAsync(documentoId, rutaAlmacenamiento, contentType, perfil, cancellationToken);
}

/// <summary>Interceptor que permite a una prueba hacer fallar un guardado concreto (errores de persistencia).</summary>
public sealed class ControlDePersistencia : SaveChangesInterceptor
{
    /// <summary>Devuelve la excepción que debe lanzar este guardado, o null para dejarlo pasar.</summary>
    public Func<DbContext, Exception?>? AlGuardar { get; set; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (AlGuardar?.Invoke(eventData.Context!) is { } error)
        {
            throw error;
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>Reloj de la aplicación con desfase controlable (enfriamiento del proveedor).</summary>
public sealed class RelojDePrueba : TimeProvider
{
    public TimeSpan Desfase { get; set; }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Desfase;
}

/// <summary>
/// Entorno compartido de la colección de la 8.4: base PostgreSQL temporal (migrada desde cero, con pgvector), host
/// completo con el almacenamiento real en un directorio temporal y los dobles de prueba. Las pruebas de la colección
/// se ejecutan en serie; cada una usa su propio tenant.
/// </summary>
public sealed class Entorno84 : IAsyncLifetime
{
    public BaseDatosTemporal Base { get; } = new();
    public ProveedorEmbeddingsProgramable Proveedor { get; } = new();
    public ControlDeExtraccion Extraccion { get; } = new();
    public ControlDePersistencia Persistencia { get; } = new();
    public RelojDePrueba Reloj { get; } = new();
    internal CapturaDeLogs Logs { get; } = new();
    public string Almacenamiento { get; } = Path.Combine(Path.GetTempPath(), "fase84-" + Guid.NewGuid().ToString("N"));
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    /// <summary>Registro propio de las pruebas: el host usa su propia tubería de logs, que no admite proveedores añadidos.</summary>
    public ILoggerFactory Registro { get; }

    public Entorno84()
    {
        Registro = LoggerFactory.Create(l => l.SetMinimumLevel(LogLevel.Debug).AddProvider(Logs));
    }

    public async Task InitializeAsync()
    {
        await Base.InitializeAsync();
        Directory.CreateDirectory(Almacenamiento);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("FileStorage:BasePath", Almacenamiento);
            b.ConfigureTestServices(servicios =>
            {
                servicios.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
                servicios.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                servicios.AddDbContext<ApplicationDbContext>((sp, opciones) => opciones
                    .UseNpgsql(Base.ConnectionString, npgsql =>
                    {
                        npgsql.UseVector();
                        npgsql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(2), null);
                    })
                    .AddInterceptors(sp.GetRequiredService<AuditableEntityInterceptor>(), Persistencia));

                servicios.RemoveAll<IEmbeddingProvider>();
                servicios.AddScoped<IEmbeddingProvider>(_ => Proveedor);

                servicios.RemoveAll<IDocumentTextExtractor>();
                servicios.AddScoped<IDocumentTextExtractor>(sp =>
                    new ExtractorProgramable(ActivatorUtilities.CreateInstance<DocumentTextExtractor>(sp), Extraccion));

                servicios.AddSingleton<TimeProvider>(Reloj);
            });
        });
        _ = Factory.Services;   // arranca el host (con el worker alojado deshabilitado por TestHostDefaults)
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        Registro.Dispose();
        await Base.DisposeAsync();
        try
        {
            Directory.Delete(Almacenamiento, recursive: true);
        }
        catch
        {
            // Limpieza de mejor esfuerzo.
        }
    }

    public void Reiniciar()
    {
        Proveedor.Reiniciar();
        Extraccion.Forzar = null;
        Persistencia.AlGuardar = null;
        Reloj.Desfase = TimeSpan.Zero;
        Logs.Mensajes.Clear();
    }

    /// <summary>Instancia del servicio (un "worker") con las opciones indicadas; no pasa por la validación de arranque.</summary>
    public IndexacionSemanticaService Servicio(Action<IndexacionOptions>? configurar = null)
    {
        var opciones = new IndexacionOptions { MaxDocumentosPorTenant = 100, MaxParalelismo = 8 };
        configurar?.Invoke(opciones);
        return new IndexacionSemanticaService(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(opciones),
            Registro.CreateLogger<IndexacionSemanticaService>(), Reloj);
    }

    public ApplicationDbContext Contexto() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(Base.ConnectionString, n => n.UseVector()).Options);
}

/// <summary>Datos de una prueba: un tenant con la indexación habilitada, un expediente y documentos con archivo real.</summary>
public sealed class Escenario84(Entorno84 entorno)
{
    public const string Habilitado = "{\"ia\":{\"indexacionSemantica\":true}}";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid UsuarioId { get; } = Guid.NewGuid();
    public Guid ExpedienteId { get; } = Guid.NewGuid();
    public Guid OtroExpedienteId { get; } = Guid.NewGuid();
    public IReadOnlyCollection<Guid> Tenants => [TenantId];

    public async Task<Escenario84> CrearAsync(bool habilitado = true)
    {
        await using var db = entorno.Contexto();
        var email = $"{UsuarioId:N}@fase84.test";
        db.Tenants.Add(new Tenant
        {
            Id = TenantId, Nombre = "Estudio Fase 8.4", IdentificadorUrl = "fase84-" + TenantId.ToString("N"), Activo = true,
            ConfiguracionJson = habilitado ? Habilitado : null
        });
        db.Usuarios.Add(new Usuario
        {
            Id = UsuarioId, TenantId = TenantId, UserName = email, Email = email, NombreCompleto = "Senior 8.4",
            Rol = Roles.AbogadoSenior, Activo = true
        });
        var cliente = new Cliente
        {
            Id = Guid.NewGuid(), TenantId = TenantId, TipoIdentificacion = TipoIdentificacion.Ruc,
            Identificacion = Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), NombreRazonSocial = "Cliente 8.4", Activo = true
        };
        db.Add(cliente);
        foreach (var id in new[] { ExpedienteId, OtroExpedienteId })
        {
            db.Add(new Expediente
            {
                Id = id, TenantId = TenantId, ClienteId = cliente.Id, NumeroExpediente = "EXP-84-" + id.ToString("N")[..8],
                Titulo = "Caso 8.4", Materia = "Civil", Estado = EstadoExpediente.Abierto, AbogadoResponsableId = UsuarioId
            });
        }

        await db.SaveChangesAsync();
        return this;
    }

    /// <summary>Documento con archivo real guardado por FileStorageService (hash incluido).</summary>
    public async Task<Guid> DocumentoAsync(byte[] contenido, string nombre, string mime, bool conHash = true)
    {
        StoredDocumentoFile archivo;
        using (var scope = entorno.Factory.Services.CreateScope())
        {
            archivo = await scope.ServiceProvider.GetRequiredService<IFileStorageService>()
                .SaveDocumentoAsync(TenantId, ExpedienteId, new MemoryStream(contenido), nombre, mime, contenido.Length);
        }

        return await InsertarAsync(archivo.RelativePath, archivo.ContentType, conHash ? archivo.Sha256Hash : null, contenido.Length);
    }

    public Task<Guid> DocumentoTxtAsync(string texto, bool conHash = true) =>
        DocumentoAsync(System.Text.Encoding.UTF8.GetBytes(texto), "documento.txt", "text/plain", conHash);

    /// <summary>Documento con ruta y ContentType arbitrarios (archivo inexistente, ruta insegura, formato no soportado).</summary>
    public Task<Guid> DocumentoCrudoAsync(string ruta, string contentType) => InsertarAsync(ruta, contentType, null, 10);

    private async Task<Guid> InsertarAsync(string ruta, string contentType, string? hash, long tamanio)
    {
        await using var db = entorno.Contexto();
        var id = Guid.NewGuid();
        db.Documentos.Add(new Documento
        {
            Id = id, TenantId = TenantId, ExpedienteId = ExpedienteId, Titulo = "TITULO-SECRETO-84 " + id.ToString("N")[..6], TipoDocumento = "Escrito",
            NombreArchivoOriginal = "archivo", RutaAlmacenamiento = ruta, ContentType = contentType, TamanioBytes = tamanio, HashSha256 = hash
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Fila Pendiente creada directamente (lo que hará la reindexación manual de la 8.5).</summary>
    public async Task<Guid> IndicePendienteAsync(Guid documentoId, string perfil)
    {
        await using var db = entorno.Contexto();
        var indice = new DocumentoIndice
        {
            Id = Guid.NewGuid(), TenantId = TenantId, DocumentoId = documentoId, ExpedienteId = ExpedienteId, Perfil = perfil,
            Estado = EstadoIndexacion.Pendiente
        };
        db.DocumentoIndices.Add(indice);
        await db.SaveChangesAsync();
        return indice.Id;
    }

    public async Task<List<DocumentoIndice>> IndicesAsync(Guid documentoId)
    {
        await using var db = entorno.Contexto();
        return await db.DocumentoIndices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.DocumentoId == documentoId).OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).ToListAsync();
    }

    public async Task<DocumentoIndice> IndiceAsync(Guid documentoId) => Assert.Single(await IndicesAsync(documentoId));

    public async Task<List<DocumentoFragmento>> FragmentosAsync(Guid indiceId)
    {
        await using var db = entorno.Contexto();
        return await db.DocumentoFragmentos.IgnoreQueryFilters().AsNoTracking().Where(f => f.IndiceId == indiceId).OrderBy(f => f.Orden).ToListAsync();
    }

    public async Task<int> ContarFragmentosAsync(Guid indiceId)
    {
        await using var db = entorno.Contexto();
        return await db.DocumentoFragmentos.IgnoreQueryFilters().CountAsync(f => f.IndiceId == indiceId);
    }

    public async Task<List<AIUsageLog>> UsosAsync()
    {
        await using var db = entorno.Contexto();
        return await db.AIUsageLogs.IgnoreQueryFilters().AsNoTracking().Where(u => u.TenantId == TenantId).OrderBy(u => u.CreatedAt).ToListAsync();
    }

    public async Task<List<HistorialAuditoria>> AuditoriasAsync(Guid documentoId, string accion)
    {
        await using var db = entorno.Contexto();
        return await db.HistorialAuditorias.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.TenantId == TenantId && a.EntidadId == documentoId.ToString() && a.Accion == accion).OrderBy(a => a.Fecha).ToListAsync();
    }

    public async Task SqlAsync(string sql, params object[] parametros)
    {
        await using var db = entorno.Contexto();
        await db.Database.ExecuteSqlRawAsync(sql, parametros);
    }

    public Task BorrarDocumentoAsync(Guid documentoId) =>
        SqlAsync("UPDATE documentos SET \"IsDeleted\" = true, \"DeletedAt\" = now() WHERE \"Id\" = {0}", documentoId);

    public Task DesactivarTenantAsync() =>
        SqlAsync("UPDATE tenants SET \"ConfiguracionJson\" = '{{\"ia\":{{\"indexacionSemantica\":false}}}}' WHERE \"Id\" = {0}", TenantId);

    /// <summary>Hace elegible de inmediato un índice aplazado por backoff (el reloj de la base no se puede adelantar).</summary>
    public Task HacerElegibleAsync(Guid indiceId) =>
        SqlAsync("UPDATE documento_indices SET \"ProximoIntentoEn\" = now() - interval '1 second' WHERE \"Id\" = {0}", indiceId);

    /// <summary>Siembra y adquiere el único trabajo pendiente del tenant.</summary>
    public async Task<TrabajoIndexacion> AdquirirUnoAsync(IIndexacionSemanticaService servicio)
    {
        await servicio.SembrarAsync(Tenants);
        return Assert.Single(await servicio.AdquirirAsync(Tenants, 1));
    }

    /// <summary>Texto TXT que la fragmentación chunk-v1 divide exactamente en <paramref name="n"/> fragmentos.</summary>
    public static string TextoDeFragmentos(int n, string marca = "Parrafo")
    {
        string Construir(int parrafos) => string.Join("\n\n", Enumerable.Range(1, parrafos)
            .Select(i => $"{marca} {i:D4} " + new string((char)('a' + i % 26), 1380)));

        int Contar(string texto) => Fragmentador.Fragmentar([Fase82Fragmentos.Txt(texto)], int.MaxValue).Fragmentos.Count;

        if (n == 1)
        {
            return $"{marca} unico del documento de prueba.";
        }

        var k = n;
        var candidato = Construir(k);
        for (var vueltas = 0; vueltas < 50 && Contar(candidato) != n; vueltas++)
        {
            k += Contar(candidato) < n ? 1 : -1;
            candidato = Construir(k);
        }

        Assert.Equal(n, Contar(candidato));
        return candidato;
    }
}
