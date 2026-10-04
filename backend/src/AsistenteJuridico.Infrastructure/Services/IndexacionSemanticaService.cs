using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Indexacion;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Application.Features.Indexacion;
using AsistenteJuridico.Domain.Common;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.BackgroundServices;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace AsistenteJuridico.Infrastructure.Services;

/// <summary>
/// Fase 8.4 — Indexación semántica (FASE_8_4_CONTRATO.md v1.1). El registro del trabajo es la fila de
/// documento_indices; no hay colas ni tablas de trabajos.
///
/// - Adquisición con FOR UPDATE SKIP LOCKED; después protege el lease (Procesando + ProcesandoDesde) y el xmin
///   devuelto, que es la prueba de propiedad en CADA escritura del worker.
/// - El proceso (extracción, fragmentación, embeddings) ocurre fuera de transacción y con los vectores en memoria:
///   un índice que no se confirma no deja fragmentos.
/// - Confirmación: fragmentos, retirada de la versión anterior y activación en UNA transacción (§19, §20).
/// - Errores (§13): los de EmbedAsync se clasifican solo por IFalloProveedorEmbeddings.EsTransitorio; cualquier
///   otra excepción suya es un error interno no reintentable. Los errores de persistencia tienen su propia política
///   (estrategia de EF) y, si persisten, no se escribe ningún estado. La parada del host no es un error.
/// - AIUsageLog (§22): solo consumo informado por el proveedor; nunca estimaciones ni un 0 por "no informado".
/// - Logs y auditoría: identificadores, estados, códigos, conteos y duraciones; nunca texto, vectores ni rutas.
/// </summary>
public sealed class IndexacionSemanticaService : IIndexacionSemanticaService
{
    public const string ActorWorker = "worker:indexacion-semantica";
    private const string EntidadDocumento = "Documento";

    /// <summary>Clave del advisory lock del sembrado: espacio propio, distinto de alertas y de la recuperación de la 6.X.</summary>
    private const long ClaveLockSembrado = 0x6658_0840;

    /// <summary>Tipos que se consultan al extractor para saber cuáles admite (única fuente de verdad: IsSupported).</summary>
    private static readonly string[] TiposCandidatos =
    [
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "text/plain",
        "application/msword",
        "application/vnd.ms-excel",
        "image/jpeg",
        "image/png"
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IndexacionOptions _options;
    private readonly ILogger<IndexacionSemanticaService> _logger;
    private readonly TimeProvider _tiempo;
    private readonly string _instancia;
    private readonly Lazy<(string Perfil, string[] Tipos)> _configuracion;
    private long _enfriamientoHastaTicks;

    public IndexacionSemanticaService(
        IServiceScopeFactory scopeFactory,
        IOptions<IndexacionOptions> options,
        ILogger<IndexacionSemanticaService> logger,
        TimeProvider? tiempo = null)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _tiempo = tiempo ?? TimeProvider.System;
        var instancia = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        _instancia = instancia.Length > 100 ? instancia[^100..] : instancia;
        _configuracion = new Lazy<(string, string[])>(CargarConfiguracion);
    }

    public string PerfilActivo => _configuracion.Value.Perfil;

    /// <summary>Identificador de esta instancia del worker (solo diagnóstico; la propiedad la da el xmin).</summary>
    public string Instancia => _instancia;

    /// <summary>True mientras dura el enfriamiento tras un fallo transitorio del proveedor (§12).</summary>
    public bool EnEnfriamiento => _tiempo.GetUtcNow().UtcTicks < Interlocked.Read(ref _enfriamientoHastaTicks);

    private (string, string[]) CargarConfiguracion()
    {
        using var scope = _scopeFactory.CreateScope();
        var perfil = PerfilIndexacion.Componer(scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>());
        if (perfil.Length > PerfilIndexacion.LongitudMaxima)
        {
            throw new InvalidOperationException("El perfil de indexación supera los 200 caracteres admitidos por documento_indices.Perfil.");
        }

        var extractor = scope.ServiceProvider.GetRequiredService<IDocumentTextExtractor>();
        return (perfil, TiposCandidatos.Where(extractor.IsSupported).ToArray());
    }

    // ── Paso 0: tenants habilitados ──────────────────────────────────────

    public async Task<IReadOnlyList<Guid>> TenantsHabilitadosAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var candidatos = await context.Tenants.AsNoTracking()
            .Where(t => t.Activo && t.ConfiguracionJson != null)
            .Select(t => new { t.Id, t.ConfiguracionJson })
            .ToListAsync(cancellationToken);

        // Se evalúa en la aplicación: ConfiguracionJson es texto y puede no ser JSON válido.
        return candidatos.Where(t => ConfiguracionTenantIa.IndexacionSemanticaHabilitada(t.ConfiguracionJson)).Select(t => t.Id).ToList();
    }

    // ── Paso 1: sembrado ─────────────────────────────────────────────────

    public async Task<int> SembrarAsync(IReadOnlyCollection<Guid> tenantsHabilitados, CancellationToken cancellationToken = default)
    {
        if (tenantsHabilitados.Count == 0)
        {
            return 0;
        }

        var (perfil, tipos) = _configuracion.Value;
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var sembrados = await context.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            await using var transaccion = await context.Database.BeginTransactionAsync(ct);

            // Una sola instancia siembra en cada ciclo.
            var bloqueado = await context.Database
                .SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock({0}) AS \"Value\"", ClaveLockSembrado)
                .SingleAsync(ct);
            if (!bloqueado)
            {
                await transaccion.RollbackAsync(ct);
                return 0;
            }

            // Documentos activos, de expediente activo, con formato soportado y sin fila viva del perfil activo
            // (Pendiente, Procesando, Indexado o Fallido). ON CONFLICT cubre la carrera con el único parcial EnCurso.
            var filas = await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO documento_indices
                    ("Id", "TenantId", "DocumentoId", "ExpedienteId", "Perfil", "Dimensiones", "Estado", "HashContenido",
                     "Intentos", "Fragmentos", "TokensTotales", "CreatedAt")
                SELECT gen_random_uuid(), d."TenantId", d."Id", d."ExpedienteId", @perfil, @dimensiones, 0, d."HashSha256", 0, 0, 0, now()
                FROM documentos d
                JOIN expedientes e ON e."TenantId" = d."TenantId" AND e."Id" = d."ExpedienteId"
                WHERE d."TenantId" = ANY(@tenants)
                  AND d."IsDeleted" = false AND e."IsDeleted" = false
                  AND lower(btrim(split_part(d."ContentType", ';', 1))) = ANY(@tipos)
                  AND NOT EXISTS (
                      SELECT 1 FROM documento_indices i
                      WHERE i."DocumentoId" = d."Id" AND i."Perfil" = @perfil AND i."Estado" IN (0, 1, 2, 3))
                ORDER BY d."CreatedAt", d."Id"
                LIMIT @lote
                ON CONFLICT DO NOTHING
                """,
                [
                    new NpgsqlParameter("perfil", perfil),
                    new NpgsqlParameter("dimensiones", DocumentoIndice.DimensionesPerfilInicial),
                    Uuids("tenants", tenantsHabilitados),
                    new NpgsqlParameter("tipos", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = tipos },
                    new NpgsqlParameter("lote", _options.LoteSembrado)
                ], ct);

            await transaccion.CommitAsync(ct);
            return filas;
        }, cancellationToken);

        if (sembrados > 0)
        {
            _logger.LogDebug("[INDEX_SEEDED] {Sembrados} índice(s) sembrado(s) para el perfil activo.", sembrados);
        }

        return sembrados;
    }

    // ── Paso 2: marcado ──────────────────────────────────────────────────

    public async Task<int> MarcarAsync(IReadOnlyCollection<Guid> tenantsHabilitados, CancellationToken cancellationToken = default)
    {
        var (perfil, _) = _configuracion.Value;
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var tenants = Uuids("tenants", tenantsHabilitados);

        // PurgaPendiente: documento o expediente borrado, o tenant sin la indexación habilitada.
        var purga = await context.Database.ExecuteSqlRawAsync(
            """
            UPDATE documento_indices i
            SET "Estado" = 5, "ProcesandoDesde" = NULL, "ProcesadoPor" = NULL, "UpdatedAt" = now()
            WHERE i."Estado" IN (0, 1, 2, 3)
              AND (NOT (i."TenantId" = ANY(@tenants))
                   OR EXISTS (SELECT 1 FROM documentos d
                              WHERE d."TenantId" = i."TenantId" AND d."Id" = i."DocumentoId" AND d."IsDeleted")
                   OR EXISTS (SELECT 1 FROM expedientes e
                              WHERE e."TenantId" = i."TenantId" AND e."Id" = i."ExpedienteId" AND e."IsDeleted"))
            """, [tenants], cancellationToken);

        // Obsoleto: filas de un perfil inactivo que nunca sirvieron. Una fila Indexado de otro perfil NO se toca:
        // solo se retira en la transacción que activa su reemplazo (§7, §20).
        var obsoletos = await context.Database.ExecuteSqlRawAsync(
            """
            UPDATE documento_indices
            SET "Estado" = 4, "UpdatedAt" = now()
            WHERE "Estado" IN (0, 3) AND "Perfil" <> @perfil AND "TenantId" = ANY(@tenants)
            """, [new NpgsqlParameter("perfil", perfil), Uuids("tenants", tenantsHabilitados)], cancellationToken);

        // Reactivación (rector §7.1): el documento ya cabe en el límite de fragmentos vigente.
        var reactivados = await context.Database.ExecuteSqlRawAsync(
            """
            UPDATE documento_indices
            SET "Estado" = 0, "Intentos" = 0, "CodigoError" = NULL, "ProximoIntentoEn" = NULL, "UpdatedAt" = now()
            WHERE "Estado" = 3 AND "Perfil" = @perfil AND "TenantId" = ANY(@tenants)
              AND "CodigoError" = @codigo AND "FragmentosCalculados" IS NOT NULL AND "FragmentosCalculados" <= @limite
            """,
            [
                new NpgsqlParameter("perfil", perfil),
                Uuids("tenants", tenantsHabilitados),
                new NpgsqlParameter("codigo", CodigosIndexacion.IndiceDemasiadoGrande),
                new NpgsqlParameter("limite", _options.MaxFragmentosPorDocumento)
            ], cancellationToken);

        return purga + obsoletos + reactivados;
    }

    // ── Paso 3: recuperación de leases vencidos ──────────────────────────

    public async Task<int> RecuperarLeasesAsync(CancellationToken cancellationToken = default)
    {
        List<FilaTenant> vencidos;
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            vencidos = await context.Database.SqlQueryRaw<FilaTenant>(
                """
                SELECT "Id", "TenantId" FROM documento_indices
                WHERE "Estado" = 1 AND "ProcesandoDesde" < now() - make_interval(secs => {0})
                ORDER BY "ProcesandoDesde"
                LIMIT 200
                """, (double)_options.LeaseSeconds).ToListAsync(cancellationToken);
        }

        var recuperados = 0;
        foreach (var fila in vencidos)
        {
            try
            {
                if (await RecuperarUnoAsync(fila.Id, fila.TenantId, cancellationToken))
                {
                    recuperados++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("[INDEX_RECOVERY_ERROR] Error al recuperar el índice {IndiceId}. Error: {TipoError}.", fila.Id, ex.GetType().Name);
            }
        }

        return recuperados;
    }

    private async Task<bool> RecuperarUnoAsync(Guid indiceId, Guid tenantId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentTenantService>().SetTenantId(tenantId);
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var auditoria = scope.ServiceProvider.GetRequiredService<IAuditService>();

        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            context.ChangeTracker.Clear();
            await using var transaccion = await context.Database.BeginTransactionAsync(ct);

            // La condición del lease se evalúa con el reloj de la base; otra instancia que ya lo esté recuperando se salta.
            var indice = (await context.DocumentoIndices
                .FromSqlRaw(
                    """
                    SELECT i.*, i.xmin FROM documento_indices i
                    WHERE i."Id" = {0} AND i."Estado" = 1 AND i."ProcesandoDesde" < now() - make_interval(secs => {1})
                    FOR UPDATE SKIP LOCKED
                    """, indiceId, (double)_options.LeaseSeconds)
                .IgnoreQueryFilters()
                .ToListAsync(ct)).SingleOrDefault();
            if (indice is null)
            {
                await transaccion.RollbackAsync(ct);
                return false;
            }

            var ahora = await AhoraDeLaBaseAsync(context, ct);
            var intentos = indice.Intentos + 1;
            var agotado = intentos >= _options.MaxIntentos;

            indice.Intentos = intentos;
            indice.ProcesandoDesde = null;
            indice.ProcesadoPor = null;
            indice.CodigoError = CodigosIndexacion.LeaseVencido;
            indice.UpdatedAt = ahora;
            if (agotado)
            {
                indice.Estado = EstadoIndexacion.Fallido;
                indice.ProximoIntentoEn = null;
                await auditoria.LogInTransactionAsync(EntidadDocumento, indice.DocumentoId.ToString(), "DOCUMENT_INDEX_FAILED", null, new
                {
                    actor = ActorWorker,
                    indiceId = indice.Id,
                    expedienteId = indice.ExpedienteId,
                    perfil = indice.Perfil,
                    codigoError = CodigosIndexacion.LeaseVencido,
                    motivo = MotivosIndexacion.LeaseVencido,
                    intentos
                }, ct);
            }
            else
            {
                indice.Estado = EstadoIndexacion.Pendiente;
                indice.ProximoIntentoEn = ahora + Backoff(intentos);
                await auditoria.LogInTransactionAsync(EntidadDocumento, indice.DocumentoId.ToString(), "DOCUMENT_INDEX_RECOVERED", null, new
                {
                    actor = ActorWorker,
                    indiceId = indice.Id,
                    expedienteId = indice.ExpedienteId,
                    perfil = indice.Perfil,
                    motivo = MotivosIndexacion.LeaseVencido,
                    intentos
                }, ct);
            }

            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaccion.RollbackAsync(ct);
                return false;
            }

            await transaccion.CommitAsync(ct);
            _logger.LogWarning(
                "[INDEX_LEASE_EXPIRED] Índice {IndiceId} (documento {DocumentoId}, tenant {TenantId}): lease vencido; intentos {Intentos}; estado {Estado}.",
                indice.Id, indice.DocumentoId, tenantId, intentos, indice.Estado);
            return true;
        }, cancellationToken);
    }

    // ── Paso 4: adquisición ──────────────────────────────────────────────

    public async Task<IReadOnlyList<TrabajoIndexacion>> AdquirirAsync(
        IReadOnlyCollection<Guid> tenantsHabilitados, int maximo, CancellationToken cancellationToken = default)
    {
        if (maximo <= 0 || tenantsHabilitados.Count == 0 || EnEnfriamiento)
        {
            return [];
        }

        var (perfil, _) = _configuracion.Value;
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Una sentencia = una transacción corta. FOR UPDATE SKIP LOCKED solo reparte la adquisición; al confirmar se
        // libera el bloqueo y pasa a proteger el lease. El xmin devuelto es la prueba de propiedad del trabajo.
        var filas = await context.Database.SqlQueryRaw<FilaAdquirida>(
            """
            UPDATE documento_indices
            SET "Estado" = 1, "ProcesandoDesde" = now(), "ProcesadoPor" = @instancia, "UpdatedAt" = now()
            WHERE "Id" IN (
                SELECT i."Id" FROM documento_indices i
                WHERE i."Estado" = 0 AND i."Id" IN (
                    -- Candidatos: los más antiguos de cada tenant, sin superar su tope contando los que ya procesa.
                    SELECT c."Id" FROM (
                        SELECT x."Id", x."ProximoIntentoEn", x."CreatedAt",
                               row_number() OVER (PARTITION BY x."TenantId"
                                                  ORDER BY x."ProximoIntentoEn" NULLS FIRST, x."CreatedAt", x."Id") AS posicion,
                               (SELECT count(*) FROM documento_indices p
                                WHERE p."TenantId" = x."TenantId" AND p."Estado" = 1) AS en_curso
                        FROM documento_indices x
                        WHERE x."Estado" = 0 AND x."Perfil" = @perfil
                          AND (x."ProximoIntentoEn" IS NULL OR x."ProximoIntentoEn" <= now())
                          AND x."TenantId" = ANY(@tenants)) c
                    WHERE c.posicion + c.en_curso <= @maxPorTenant
                    ORDER BY c."ProximoIntentoEn" NULLS FIRST, c."CreatedAt", c."Id"
                    LIMIT @maximo)
                FOR UPDATE SKIP LOCKED)
            RETURNING "Id", "TenantId", "DocumentoId", "ExpedienteId", "Perfil", "Intentos", xmin::text::bigint AS "Version"
            """,
            new NpgsqlParameter("instancia", _instancia),
            new NpgsqlParameter("perfil", perfil),
            Uuids("tenants", tenantsHabilitados),
            new NpgsqlParameter("maxPorTenant", _options.MaxDocumentosPorTenant),
            new NpgsqlParameter("maximo", maximo)).ToListAsync(cancellationToken);

        foreach (var fila in filas)
        {
            _logger.LogInformation(
                "[INDEX_CLAIMED] Índice {IndiceId} (documento {DocumentoId}, tenant {TenantId}) adquirido por {Instancia}; intento {Intento}.",
                fila.Id, fila.DocumentoId, fila.TenantId, _instancia, fila.Intentos + 1);
        }

        return filas.Select(f => new TrabajoIndexacion(f.Id, f.TenantId, f.DocumentoId, f.ExpedienteId, f.Perfil, f.Intentos, (uint)f.Version)).ToList();
    }

    // ── Paso 5: proceso de un documento ──────────────────────────────────

    public async Task<ResultadoIndexacion> ProcesarAsync(TrabajoIndexacion trabajo, CancellationToken parada = default)
    {
        var ejecucion = new Ejecucion(trabajo);
        using var scope = _scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentTenantService>().SetTenantId(trabajo.TenantId);
        var servicios = scope.ServiceProvider;
        var context = servicios.GetRequiredService<ApplicationDbContext>();

        ResultadoIndexacion resultado;
        try
        {
            resultado = await ProcesarCoreAsync(servicios, context, ejecucion, parada);
        }
        catch (OperationCanceledException) when (parada.IsCancellationRequested)
        {
            // Parada limpia del host (§23): no es un error, no suma intento y no genera reintento.
            resultado = await LiberarAsync(servicios, ejecucion);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Otro actor tomó la fila (recuperación, marcado): se abandona sin escribir nada.
            resultado = ResultadoIndexacion.Abandonado;
            _logger.LogWarning("[INDEX_ACTIVATION_DISCARDED] Índice {IndiceId} (ejecución {EjecucionId}): el trabajo ya no es de este worker (xmin).",
                trabajo.IndiceId, ejecucion.Id);
        }
        catch (Exception ex) when (EsErrorDePersistencia(ex))
        {
            // Error persistente de la base tras los reintentos de EF (§13.1): no se escribe ningún estado; el lease
            // vencerá y la recuperación contará el intento.
            resultado = ResultadoIndexacion.SinEstado;
            _logger.LogError("[INDEX_PERSISTENCE_ERROR] Índice {IndiceId} (ejecución {EjecucionId}): error de persistencia {TipoError}; sin escritura de estado.",
                trabajo.IndiceId, ejecucion.Id, ex.GetType().Name);
        }
        catch (Exception ex)
        {
            // Excepción no prevista del proceso: error interno no reintentable (§13.1).
            _logger.LogError("[INDEX_INTERNAL_ERROR] Índice {IndiceId} (ejecución {EjecucionId}): error interno {TipoError}.",
                trabajo.IndiceId, ejecucion.Id, ex.GetType().Name);
            resultado = await FallarSeguroAsync(servicios, ejecucion, CodigosIndexacion.ErrorInterno, MotivosIndexacion.Interno);
        }

        // Consumo real de un intento que no terminó en Indexado: en un contexto independiente (§22.2).
        if (resultado != ResultadoIndexacion.Indexado)
        {
            await RegistrarConsumoIndependienteAsync(ejecucion, resultado);
        }

        return resultado;
    }

    private async Task<ResultadoIndexacion> ProcesarCoreAsync(
        IServiceProvider servicios, ApplicationDbContext context, Ejecucion ejecucion, CancellationToken parada)
    {
        var trabajo = ejecucion.Trabajo;
        var cronometro = Stopwatch.StartNew();
        _logger.LogInformation("[INDEX_BUILD] Índice {IndiceId} (documento {DocumentoId}, tenant {TenantId}, ejecución {EjecucionId}): construcción iniciada; intento {Intento}.",
            trabajo.IndiceId, trabajo.DocumentoId, trabajo.TenantId, ejecucion.Id, ejecucion.Intento);

        // 1. Documento, expediente y tenant.
        var origen = await LeerOrigenAsync(context, trabajo, parada);
        if (origen.Situacion != SituacionOrigen.Vigente)
        {
            return await DescartarAsync(servicios, ejecucion, origen.Situacion);
        }

        // 2. Extracción (perfil de indexación de la 8.2) y primer latido.
        var extractor = servicios.GetRequiredService<IDocumentTextExtractor>();
        var extraccion = await extractor.ExtractSegmentsAsync(
            trabajo.DocumentoId, origen.Ruta, origen.ContentType, ExtractionProfile.Indexacion, parada);
        if (extraccion.Status != ExtractionStatus.Success)
        {
            return await ResolverExtraccionAsync(servicios, ejecucion, extraccion.Status);
        }

        if (!await LatirAsync(servicios, context, ejecucion, parada))
        {
            return ejecucion.ResultadoDelLatido;
        }

        // 3. Hash esperado (§14): un hash distinto es un evento de seguridad, sin reencolar.
        if (origen.HashEsperado != null && !string.Equals(origen.HashEsperado, extraccion.HashSha256Archivo, StringComparison.Ordinal))
        {
            return await FallarPorHashAsync(servicios, ejecucion);
        }

        ejecucion.HashContenido = origen.HashEsperado ?? extraccion.HashSha256Archivo;

        // 4. Fragmentación y límite de fragmentos, antes de cualquier llamada al proveedor.
        var fragmentacion = Fragmentador.Fragmentar(extraccion.Segmentos, _options.MaxFragmentosPorDocumento, parada);
        switch (fragmentacion.Estado)
        {
            case EstadoFragmentacion.SinTexto:
                return await FallarAsync(servicios, ejecucion, CodigosIndexacion.TextoVacio, null);
            case EstadoFragmentacion.LimiteSuperado:
                return await FallarAsync(servicios, ejecucion, CodigosIndexacion.IndiceDemasiadoGrande, MotivosIndexacion.LimiteDeFragmentos,
                    fragmentosCalculados: fragmentacion.FragmentosCalculados, limiteAplicado: fragmentacion.LimiteAplicado);
        }

        var fragmentos = fragmentacion.Fragmentos;
        // Estimación LOCAL del tamaño del texto (no es consumo del proveedor): va a TokensTotales y al presupuesto.
        var tokensEstimados = fragmentos.Sum(f => (long)f.TokensEstimados);

        // 5. Presupuesto preventivo estimado (§26.2).
        if (await PresupuestoAgotadoAsync(context, trabajo.TenantId, tokensEstimados, parada))
        {
            return await AplazarAsync(servicios, context, ejecucion, tokensEstimados, parada);
        }

        // 6. Embeddings por lotes secuenciales, con un latido tras cada lote.
        var proveedor = servicios.GetRequiredService<IEmbeddingProvider>();
        if (!string.Equals(PerfilIndexacion.Componer(proveedor), trabajo.Perfil, StringComparison.Ordinal))
        {
            // El proveedor cambió bajo el worker: no se generan vectores de otro perfil.
            return await FallarAsync(servicios, ejecucion, CodigosIndexacion.ErrorInterno, MotivosIndexacion.Interno);
        }

        var tamanoLote = LoteEmbeddings.LimiteEfectivo(proveedor.MaxEntradasPorLote);
        ejecucion.LotesTotales = (fragmentos.Count + tamanoLote - 1) / tamanoLote;
        ejecucion.ProviderId = proveedor.ProviderId;
        ejecucion.ModelId = proveedor.ModelId;
        var vectores = new List<float[]>(fragmentos.Count);

        for (var inicio = 0; inicio < fragmentos.Count; inicio += tamanoLote)
        {
            parada.ThrowIfCancellationRequested();
            var textos = fragmentos.Skip(inicio).Take(tamanoLote).Select(f => f.Texto).ToList();
            var numeroLote = inicio / tamanoLote + 1;
            var cronometroLote = Stopwatch.StartNew();

            EmbeddingBatchResult lote;
            try
            {
                // Solo el texto de los fragmentos de ESTE documento; ningún identificador (8.3).
                lote = await proveedor.EmbedAsync(textos, EmbeddingPurpose.Documento, parada);
            }
            catch (OperationCanceledException) when (parada.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IFalloProveedorEmbeddings fallo)
            {
                ejecucion.DuracionProveedorMs += cronometroLote.ElapsedMilliseconds;
                _logger.LogWarning(
                    "[INDEX_BATCH_FAILED] Índice {IndiceId} (ejecución {EjecucionId}): lote {Lote}/{Lotes} fallido. Motivo {Motivo}; transitorio {Transitorio}; estado HTTP {Estado}; intentos del proveedor {IntentosProveedor}.",
                    trabajo.IndiceId, ejecucion.Id, numeroLote, ejecucion.LotesTotales, fallo.Motivo, fallo.EsTransitorio, fallo.CodigoEstadoHttp, fallo.Intentos);

                var codigo = fallo.Motivo == MotivoFalloEmbedding.Timeout ? CodigosIndexacion.ProveedorTimeout : CodigosIndexacion.ProveedorError;
                if (!fallo.EsTransitorio)
                {
                    return await FallarAsync(servicios, ejecucion, codigo, fallo.Motivo.ToString());
                }

                ActivarEnfriamiento();
                return await ReintentarAsync(servicios, context, ejecucion, codigo, fallo.Motivo.ToString(), fallo.EsperaSugerida, parada);
            }
            catch (Exception ex)
            {
                // Excepción de EmbedAsync que NO implementa IFalloProveedorEmbeddings: error interno no reintentable.
                // No se convierte en excepción de proveedor ni se trata como transitoria (K-1).
                ejecucion.DuracionProveedorMs += cronometroLote.ElapsedMilliseconds;
                _logger.LogError("[INDEX_INTERNAL_ERROR] Índice {IndiceId} (ejecución {EjecucionId}): excepción no tipada del proveedor de embeddings: {TipoError}.",
                    trabajo.IndiceId, ejecucion.Id, ex.GetType().Name);
                return await FallarAsync(servicios, ejecucion, CodigosIndexacion.ErrorInterno, MotivosIndexacion.Interno);
            }

            ejecucion.DuracionProveedorMs += cronometroLote.ElapsedMilliseconds;

            // Validación del lote antes de aceptarlo (§17). Una incoherencia aquí es interna: el proveedor ya valida.
            if (lote.Vectores.Count != textos.Count
                || lote.Dimensiones != DocumentoIndice.DimensionesPerfilInicial
                || lote.Vectores.Any(v => v is null || v.Length != DocumentoIndice.DimensionesPerfilInicial)
                || !string.Equals(lote.ModelId, proveedor.ModelId, StringComparison.Ordinal)
                || !string.Equals(lote.ProviderId, proveedor.ProviderId, StringComparison.Ordinal))
            {
                return await FallarAsync(servicios, ejecucion, CodigosIndexacion.ErrorInterno, MotivosIndexacion.Interno);
            }

            vectores.AddRange(lote.Vectores);
            ejecucion.RegistrarLote(lote.TokensEntrada);
            _logger.LogDebug(
                "[INDEX_BATCH] Índice {IndiceId} (ejecución {EjecucionId}): lote {Lote}/{Lotes} con {Textos} texto(s) en {DuracionMs} ms; tokens {Tokens}.",
                trabajo.IndiceId, ejecucion.Id, numeroLote, ejecucion.LotesTotales, textos.Count, cronometroLote.ElapsedMilliseconds,
                lote.TokensEntrada?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no informado");

            if (!await LatirAsync(servicios, context, ejecucion, parada))
            {
                return ejecucion.ResultadoDelLatido;
            }
        }

        // 7. Conjunto completo y coherente (§18) y confirmación atómica (§19).
        if (vectores.Count != fragmentos.Count)
        {
            return await FallarAsync(servicios, ejecucion, CodigosIndexacion.ErrorInterno, MotivosIndexacion.Interno);
        }

        var confirmacion = await ConfirmarAsync(servicios, context, ejecucion, fragmentos, vectores, tokensEstimados, parada);
        if (confirmacion == ResultadoIndexacion.Indexado)
        {
            _logger.LogInformation(
                "[INDEX_BUILD] Índice {IndiceId} (ejecución {EjecucionId}): construcción completada con {Fragmentos} fragmento(s) en {DuracionMs} ms.",
                trabajo.IndiceId, ejecucion.Id, fragmentos.Count, cronometro.ElapsedMilliseconds);
        }

        return confirmacion;
    }

    // ── Latido (§10) ─────────────────────────────────────────────────────

    /// <summary>
    /// Renueva el lease con el reloj de la base exigiendo el xmin propio y comprueba que el tenant, el documento y el
    /// expediente siguen vigentes. False: el worker debe detenerse (ejecucion.ResultadoDelLatido dice por qué).
    /// </summary>
    private async Task<bool> LatirAsync(IServiceProvider servicios, ApplicationDbContext context, Ejecucion ejecucion, CancellationToken parada)
    {
        var trabajo = ejecucion.Trabajo;
        var nuevas = await context.Database.SqlQueryRaw<long>(
            """
            UPDATE documento_indices SET "ProcesandoDesde" = now(), "UpdatedAt" = now()
            WHERE "Id" = {0} AND xmin::text::bigint = {1} AND "Estado" = 1
            RETURNING xmin::text::bigint AS "Value"
            """, trabajo.IndiceId, (long)ejecucion.Version).ToListAsync(parada);
        if (nuevas.Count == 0)
        {
            // Otro actor recuperó, marcó o purgó la fila: se abandona sin escribir nada.
            ejecucion.ResultadoDelLatido = ResultadoIndexacion.Abandonado;
            _logger.LogWarning("[INDEX_ACTIVATION_DISCARDED] Índice {IndiceId} (ejecución {EjecucionId}): el latido no encontró el trabajo (xmin); se abandona.",
                trabajo.IndiceId, ejecucion.Id);
            return false;
        }

        ejecucion.Version = (uint)nuevas[0];

        var origen = await LeerOrigenAsync(context, trabajo, parada);
        if (origen.Situacion != SituacionOrigen.Vigente)
        {
            ejecucion.ResultadoDelLatido = await DescartarAsync(servicios, ejecucion, origen.Situacion);
            return false;
        }

        return true;
    }

    // ── Confirmación atómica (§19, §20) ──────────────────────────────────

    private async Task<ResultadoIndexacion> ConfirmarAsync(
        IServiceProvider servicios, ApplicationDbContext context, Ejecucion ejecucion,
        IReadOnlyList<FragmentoPreparado> fragmentos, List<float[]> vectores, long tokensEstimados, CancellationToken parada)
    {
        var trabajo = ejecucion.Trabajo;
        var auditoria = servicios.GetRequiredService<IAuditService>();
        var versionInicial = ejecucion.Version;
        Guid? reemplazado = null;
        Guid? usoId = null;

        var desenlace = await context.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            context.ChangeTracker.Clear();
            reemplazado = null;
            usoId = null;
            await using var transaccion = await context.Database.BeginTransactionAsync(ct);

            // 1. Propiedad: bloquea la fila propia antes de escribir nada más. 0 filas = el trabajo ya no es nuestro.
            var propias = await context.Database.SqlQueryRaw<long>(
                """
                UPDATE documento_indices SET "UpdatedAt" = now()
                WHERE "Id" = {0} AND xmin::text::bigint = {1} AND "Estado" = 1
                RETURNING xmin::text::bigint AS "Value"
                """, trabajo.IndiceId, (long)versionInicial).ToListAsync(ct);
            if (propias.Count == 0)
            {
                await transaccion.RollbackAsync(ct);
                return (Desenlace.Perdido, versionInicial);
            }

            // 2. Revalidación con FOR SHARE: un borrado lógico concurrente espera a esta transacción o ya se ve.
            var situacion = await RevalidarConBloqueoAsync(context, ejecucion, ct);
            if (situacion != SituacionOrigen.Vigente)
            {
                await transaccion.RollbackAsync(ct);
                return (situacion switch
                {
                    SituacionOrigen.HashDistinto => Desenlace.HashDistinto,
                    SituacionOrigen.OtroExpediente => Desenlace.OtroExpediente,
                    _ => Desenlace.Purga
                }, versionInicial);
            }

            var ahora = await AhoraDeLaBaseAsync(context, ct);

            // 4. Retirada de la versión vigente anterior del mismo documento. Va ANTES de la activación: el único
            // parcial Vigente se comprueba en cada sentencia. Solo ocurre aquí, dentro de esta transacción (§20).
            var retirados = await context.Database.SqlQueryRaw<Guid>(
                """
                UPDATE documento_indices SET "Estado" = 4, "UpdatedAt" = now()
                WHERE "TenantId" = {0} AND "DocumentoId" = {1} AND "Estado" = 2 AND "Id" <> {2}
                RETURNING "Id" AS "Value"
                """, trabajo.TenantId, trabajo.DocumentoId, trabajo.IndiceId).ToListAsync(ct);
            reemplazado = retirados.Count > 0 ? retirados[0] : null;

            // 3. Fragmentos: los identificadores se copian del trabajo (la fila del índice), nunca de fuera.
            for (var i = 0; i < fragmentos.Count; i++)
            {
                var f = fragmentos[i];
                context.DocumentoFragmentos.Add(new DocumentoFragmento
                {
                    Id = Guid.NewGuid(),
                    TenantId = trabajo.TenantId,
                    DocumentoId = trabajo.DocumentoId,
                    ExpedienteId = trabajo.ExpedienteId,
                    IndiceId = trabajo.IndiceId,
                    Orden = f.Orden,
                    Texto = f.Texto,
                    Ubicacion = JsonSerializer.Serialize(new
                    {
                        tipo = f.Ubicacion.Tipo,
                        desde = f.Ubicacion.Desde,
                        hasta = f.Ubicacion.Hasta,
                        etiqueta = f.Ubicacion.Etiqueta
                    }),
                    RutaSeccion = f.RutaSeccion,
                    CaracterInicio = f.CaracterInicio,
                    CaracterFin = f.CaracterFin,
                    TokensEstimados = f.TokensEstimados,
                    HashFragmento = f.HashFragmento,
                    Embedding = vectores[i],   // tal cual lo devolvió el proveedor
                    CreatedAt = ahora
                });
            }

            // 5. Activación de la fila propia, exigiendo el xmin tomado en el paso 1.
            var indice = Propio(context, trabajo, (uint)propias[0]);
            indice.Estado = EstadoIndexacion.Indexado;
            indice.IndexadoEn = ahora;
            indice.Fragmentos = fragmentos.Count;
            indice.TokensTotales = (int)Math.Min(tokensEstimados, int.MaxValue);
            indice.HashContenido = ejecucion.HashContenido;
            indice.CodigoError = null;
            indice.FragmentosCalculados = null;
            indice.LimiteAplicado = null;
            indice.ProcesandoDesde = null;
            indice.ProcesadoPor = null;
            indice.ProximoIntentoEn = null;
            indice.UpdatedAt = ahora;
            Escribir(context, indice, nameof(DocumentoIndice.Estado), nameof(DocumentoIndice.IndexadoEn), nameof(DocumentoIndice.Fragmentos),
                nameof(DocumentoIndice.TokensTotales), nameof(DocumentoIndice.HashContenido), nameof(DocumentoIndice.CodigoError),
                nameof(DocumentoIndice.FragmentosCalculados), nameof(DocumentoIndice.LimiteAplicado), nameof(DocumentoIndice.ProcesandoDesde),
                nameof(DocumentoIndice.ProcesadoPor), nameof(DocumentoIndice.ProximoIntentoEn), nameof(DocumentoIndice.UpdatedAt));

            // 6. Consumo real (solo si todos los lotes lo informaron) y auditoría, en la misma transacción.
            var uso = ejecucion.CrearUso(exitoso: true, codigoError: null, ahora);
            if (uso != null)
            {
                context.AIUsageLogs.Add(uso);
                usoId = uso.Id;
            }

            await auditoria.LogInTransactionAsync(EntidadDocumento, trabajo.DocumentoId.ToString(), "DOCUMENT_INDEXED", null, new
            {
                actor = ActorWorker,
                indiceId = trabajo.IndiceId,
                ejecucionId = ejecucion.Id,
                expedienteId = trabajo.ExpedienteId,
                perfil = trabajo.Perfil,
                fragmentos = fragmentos.Count,
                tokensTotales = tokensEstimados,                // estimación local del tamaño; no es consumo
                tokensInformados = ejecucion.TokensInformados,  // consumo real informado, o null
                lotesCompletados = ejecucion.LotesCompletados,
                lotesTotales = ejecucion.LotesTotales,
                intento = ejecucion.Intento,
                aiUsageLogId = usoId,
                indiceReemplazadoId = reemplazado
            }, ct);

            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaccion.RollbackAsync(ct);
                return (Desenlace.Perdido, versionInicial);
            }

            await transaccion.CommitAsync(ct);
            return (Desenlace.Activado, indice.Version);
        }, parada);

        switch (desenlace.Item1)
        {
            case Desenlace.Activado:
                ejecucion.Version = desenlace.Item2;
                ejecucion.ConsumoRegistrado = true;
                _logger.LogInformation(
                    "[INDEX_ACTIVATED] Índice {IndiceId} (documento {DocumentoId}, tenant {TenantId}, ejecución {EjecucionId}) activado con {Fragmentos} fragmento(s); índice reemplazado {Reemplazado}.",
                    trabajo.IndiceId, trabajo.DocumentoId, trabajo.TenantId, ejecucion.Id, fragmentos.Count, reemplazado);
                if (reemplazado != null)
                {
                    _logger.LogInformation("[INDEX_SUPERSEDED] Índice {IndiceRetirado} retirado por {IndiceNuevo}.", reemplazado, trabajo.IndiceId);
                }

                return ResultadoIndexacion.Indexado;

            case Desenlace.Perdido:
                _logger.LogWarning("[INDEX_ACTIVATION_DISCARDED] Índice {IndiceId} (ejecución {EjecucionId}): activación descartada (xmin).",
                    trabajo.IndiceId, ejecucion.Id);
                return ResultadoIndexacion.Abandonado;

            case Desenlace.HashDistinto:
                return await FallarPorHashAsync(servicios, ejecucion);

            case Desenlace.OtroExpediente:
                return await DescartarAsync(servicios, ejecucion, SituacionOrigen.OtroExpediente);

            default:
                return await DescartarAsync(servicios, ejecucion, SituacionOrigen.Eliminado);
        }
    }

    private static async Task<SituacionOrigen> RevalidarConBloqueoAsync(ApplicationDbContext context, Ejecucion ejecucion, CancellationToken ct)
    {
        var trabajo = ejecucion.Trabajo;
        var documento = (await context.Database.SqlQueryRaw<FilaDocumento>(
            """
            SELECT d."IsDeleted", d."ExpedienteId", d."HashSha256" FROM documentos d
            WHERE d."TenantId" = {0} AND d."Id" = {1}
            FOR SHARE
            """, trabajo.TenantId, trabajo.DocumentoId).ToListAsync(ct)).SingleOrDefault();
        if (documento is null || documento.IsDeleted)
        {
            return SituacionOrigen.Eliminado;
        }

        var expedientes = await context.Database.SqlQueryRaw<bool>(
            """
            SELECT e."IsDeleted" AS "Value" FROM expedientes e
            WHERE e."TenantId" = {0} AND e."Id" = {1}
            FOR SHARE
            """, trabajo.TenantId, documento.ExpedienteId).ToListAsync(ct);
        if (expedientes.Count == 0 || expedientes[0])
        {
            return SituacionOrigen.Eliminado;
        }

        if (!await TenantHabilitadoAsync(context, trabajo.TenantId, ct))
        {
            return SituacionOrigen.Eliminado;
        }

        if (documento.ExpedienteId != trabajo.ExpedienteId)
        {
            return SituacionOrigen.OtroExpediente;
        }

        return documento.HashSha256 != null && !string.Equals(documento.HashSha256, ejecucion.HashContenido, StringComparison.Ordinal)
            ? SituacionOrigen.HashDistinto
            : SituacionOrigen.Vigente;
    }

    // ── Transiciones del índice propio (siempre exigiendo el xmin) ───────

    private async Task<ResultadoIndexacion> ResolverExtraccionAsync(IServiceProvider servicios, Ejecucion ejecucion, ExtractionStatus estado)
    {
        var codigo = CodigosIndexacion.DeExtraccion(estado)!;
        if (estado == ExtractionStatus.ExtractionFailed)
        {
            // Único estado transitorio de la extracción (8.2): reintento del trabajo.
            return await ReintentarAsync(servicios, servicios.GetRequiredService<ApplicationDbContext>(), ejecucion, codigo, null, null, CancellationToken.None);
        }

        var motivo = estado switch
        {
            ExtractionStatus.Forbidden => MotivosIndexacion.AlmacenamientoRechazado,
            ExtractionStatus.ContextExceeded => MotivosIndexacion.ContextoExcedido,
            _ => null
        };
        return await FallarAsync(servicios, ejecucion, codigo, motivo);
    }

    private async Task<ResultadoIndexacion> FallarPorHashAsync(IServiceProvider servicios, Ejecucion ejecucion)
    {
        // Evento de seguridad (§14): el archivo no coincide con el hash esperado. Sin reencolar ni Obsoleto.
        _logger.LogError("[INDEX_HASH_MISMATCH] Índice {IndiceId} (documento {DocumentoId}, tenant {TenantId}, ejecución {EjecucionId}): el hash del archivo no coincide con el esperado.",
            ejecucion.Trabajo.IndiceId, ejecucion.Trabajo.DocumentoId, ejecucion.Trabajo.TenantId, ejecucion.Id);
        return await FallarAsync(servicios, ejecucion, CodigosIndexacion.TextoInvalido, MotivosIndexacion.HashDistinto);
    }

    /// <summary>Procesando → Fallido, con auditoría DOCUMENT_INDEX_FAILED en la misma transacción.</summary>
    private async Task<ResultadoIndexacion> FallarAsync(
        IServiceProvider servicios, Ejecucion ejecucion, string codigo, string? motivo,
        int? fragmentosCalculados = null, int? limiteAplicado = null, int? intentos = null)
    {
        var trabajo = ejecucion.Trabajo;
        var context = servicios.GetRequiredService<ApplicationDbContext>();
        var auditoria = servicios.GetRequiredService<IAuditService>();
        var intentosFinales = intentos ?? trabajo.Intentos;
        ejecucion.CodigoError = codigo;

        await context.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            context.ChangeTracker.Clear();
            var indice = Propio(context, trabajo, ejecucion.Version);
            indice.Estado = EstadoIndexacion.Fallido;
            indice.CodigoError = codigo;
            indice.Intentos = intentosFinales;
            indice.FragmentosCalculados = fragmentosCalculados;
            indice.LimiteAplicado = limiteAplicado;
            indice.ProcesandoDesde = null;
            indice.ProcesadoPor = null;
            indice.ProximoIntentoEn = null;
            indice.UpdatedAt = DateTime.UtcNow;
            Escribir(context, indice, nameof(DocumentoIndice.Estado), nameof(DocumentoIndice.CodigoError), nameof(DocumentoIndice.Intentos),
                nameof(DocumentoIndice.FragmentosCalculados), nameof(DocumentoIndice.LimiteAplicado), nameof(DocumentoIndice.ProcesandoDesde),
                nameof(DocumentoIndice.ProcesadoPor), nameof(DocumentoIndice.ProximoIntentoEn), nameof(DocumentoIndice.UpdatedAt));

            await auditoria.LogInTransactionAsync(EntidadDocumento, trabajo.DocumentoId.ToString(), "DOCUMENT_INDEX_FAILED", null, new
            {
                actor = ActorWorker,
                indiceId = trabajo.IndiceId,
                ejecucionId = ejecucion.Id,
                expedienteId = trabajo.ExpedienteId,
                perfil = trabajo.Perfil,
                codigoError = codigo,
                motivo,
                intentos = intentosFinales,
                intento = ejecucion.Intento,
                lotesCompletados = ejecucion.LotesCompletados,
                lotesTotales = ejecucion.LotesTotales,
                tokensInformados = ejecucion.TokensInformados,
                aiUsageLogId = ejecucion.UsoPrevistoId,
                fragmentosCalculados,
                limiteAplicado
            }, ct);

            await context.SaveChangesAsync(ct);   // UPDATE … WHERE xmin = el propio; 0 filas → DbUpdateConcurrencyException
            ejecucion.Version = indice.Version;
        }, CancellationToken.None);

        _logger.LogWarning(
            "[INDEX_FAILED] Índice {IndiceId} (documento {DocumentoId}, tenant {TenantId}, ejecución {EjecucionId}): Fallido con {Codigo}; motivo {Motivo}; intentos {Intentos}.",
            trabajo.IndiceId, trabajo.DocumentoId, trabajo.TenantId, ejecucion.Id, codigo, motivo, intentosFinales);
        return ResultadoIndexacion.Fallido;
    }

    /// <summary>Fallo desde el manejador general: si tampoco se puede escribir el estado, lo resolverá el lease.</summary>
    private async Task<ResultadoIndexacion> FallarSeguroAsync(IServiceProvider servicios, Ejecucion ejecucion, string codigo, string motivo)
    {
        try
        {
            return await FallarAsync(servicios, ejecucion, codigo, motivo);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ResultadoIndexacion.Abandonado;
        }
        catch (Exception ex)
        {
            _logger.LogError("[INDEX_PERSISTENCE_ERROR] Índice {IndiceId}: no se pudo registrar el fallo interno. Error: {TipoError}.",
                ejecucion.Trabajo.IndiceId, ex.GetType().Name);
            return ResultadoIndexacion.SinEstado;
        }
    }

    /// <summary>Error transitorio del intento: Procesando → Pendiente con Intentos + 1 y backoff, o Fallido al agotar (§12).</summary>
    private async Task<ResultadoIndexacion> ReintentarAsync(
        IServiceProvider servicios, ApplicationDbContext context, Ejecucion ejecucion, string codigo, string? motivo,
        TimeSpan? esperaSugerida, CancellationToken parada)
    {
        var trabajo = ejecucion.Trabajo;
        var intentos = trabajo.Intentos + 1;
        ejecucion.CodigoError = codigo;
        if (intentos >= _options.MaxIntentos)
        {
            return await FallarAsync(servicios, ejecucion, codigo, MotivosIndexacion.ReintentosAgotados, intentos: intentos);
        }

        var espera = Backoff(intentos);
        if (esperaSugerida is { } sugerida && sugerida > espera)
        {
            espera = sugerida > IndexacionOptions.BackoffMaximo ? IndexacionOptions.BackoffMaximo : sugerida;
        }

        DateTime proximo = default;
        await context.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            context.ChangeTracker.Clear();
            proximo = await AhoraDeLaBaseAsync(context, ct) + espera;
            var indice = Propio(context, trabajo, ejecucion.Version);
            indice.Estado = EstadoIndexacion.Pendiente;
            indice.Intentos = intentos;
            indice.ProximoIntentoEn = proximo;
            indice.CodigoError = codigo;   // código del último fallo; se limpia al confirmar (§22.4)
            indice.ProcesandoDesde = null;
            indice.ProcesadoPor = null;
            indice.UpdatedAt = DateTime.UtcNow;
            Escribir(context, indice, nameof(DocumentoIndice.Estado), nameof(DocumentoIndice.Intentos), nameof(DocumentoIndice.ProximoIntentoEn),
                nameof(DocumentoIndice.CodigoError), nameof(DocumentoIndice.ProcesandoDesde), nameof(DocumentoIndice.ProcesadoPor),
                nameof(DocumentoIndice.UpdatedAt));
            await context.SaveChangesAsync(ct);
            ejecucion.Version = indice.Version;
        }, CancellationToken.None);

        _logger.LogWarning(
            "[INDEX_RETRY] Índice {IndiceId} (documento {DocumentoId}, tenant {TenantId}, ejecución {EjecucionId}): reintento programado. Intento {Intento}; código {Codigo}; motivo {Motivo}; próximo intento {Proximo:o}; lotes completados {Lotes}; aiUsageLogId {UsoId}.",
            trabajo.IndiceId, trabajo.DocumentoId, trabajo.TenantId, ejecucion.Id, intentos, codigo, motivo, proximo, ejecucion.LotesCompletados, ejecucion.UsoPrevistoId);
        return ResultadoIndexacion.Reintento;
    }

    /// <summary>Presupuesto preventivo agotado: Pendiente hasta el día UTC siguiente, sin sumar intento (§26.2).</summary>
    private async Task<ResultadoIndexacion> AplazarAsync(
        IServiceProvider servicios, ApplicationDbContext context, Ejecucion ejecucion, long tokensEstimados, CancellationToken parada)
    {
        var trabajo = ejecucion.Trabajo;
        DateTime proximo = default;
        await context.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            context.ChangeTracker.Clear();
            var ahora = await AhoraDeLaBaseAsync(context, ct);
            proximo = ahora.Date.AddDays(1).AddSeconds(Random.Shared.Next(0, 600));
            var indice = Propio(context, trabajo, ejecucion.Version);
            indice.Estado = EstadoIndexacion.Pendiente;
            indice.ProximoIntentoEn = DateTime.SpecifyKind(proximo, DateTimeKind.Utc);
            indice.ProcesandoDesde = null;
            indice.ProcesadoPor = null;
            indice.UpdatedAt = DateTime.UtcNow;
            Escribir(context, indice, nameof(DocumentoIndice.Estado), nameof(DocumentoIndice.ProximoIntentoEn),
                nameof(DocumentoIndice.ProcesandoDesde), nameof(DocumentoIndice.ProcesadoPor), nameof(DocumentoIndice.UpdatedAt));
            await context.SaveChangesAsync(ct);
            ejecucion.Version = indice.Version;
        }, CancellationToken.None);

        _logger.LogInformation(
            "[INDEX_BUDGET_DEFERRED] Índice {IndiceId} (tenant {TenantId}, ejecución {EjecucionId}): presupuesto preventivo estimado agotado. Estimación del documento {TokensEstimados}; próximo intento {Proximo:o}.",
            trabajo.IndiceId, trabajo.TenantId, ejecucion.Id, tokensEstimados, proximo);
        return ResultadoIndexacion.Aplazado;
    }

    /// <summary>Documento o expediente borrado, tenant desactivado (PurgaPendiente) u otro expediente (Obsoleto).</summary>
    private async Task<ResultadoIndexacion> DescartarAsync(IServiceProvider servicios, Ejecucion ejecucion, SituacionOrigen situacion)
    {
        var trabajo = ejecucion.Trabajo;
        var context = servicios.GetRequiredService<ApplicationDbContext>();
        var estado = situacion == SituacionOrigen.OtroExpediente ? EstadoIndexacion.Obsoleto : EstadoIndexacion.PurgaPendiente;

        await context.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            context.ChangeTracker.Clear();
            var indice = Propio(context, trabajo, ejecucion.Version);
            indice.Estado = estado;
            indice.ProcesandoDesde = null;
            indice.ProcesadoPor = null;
            indice.UpdatedAt = DateTime.UtcNow;
            Escribir(context, indice, nameof(DocumentoIndice.Estado), nameof(DocumentoIndice.ProcesandoDesde),
                nameof(DocumentoIndice.ProcesadoPor), nameof(DocumentoIndice.UpdatedAt));
            await context.SaveChangesAsync(ct);
            ejecucion.Version = indice.Version;
        }, CancellationToken.None);

        _logger.LogWarning("[INDEX_ACTIVATION_DISCARDED] Índice {IndiceId} (ejecución {EjecucionId}): descartado; causa {Causa}; estado {Estado}.",
            trabajo.IndiceId, ejecucion.Id, situacion, estado);
        return estado == EstadoIndexacion.Obsoleto ? ResultadoIndexacion.Obsoleto : ResultadoIndexacion.Purga;
    }

    /// <summary>
    /// Parada limpia (§23): Procesando → Pendiente de inmediato, sin sumar intento y sin tocar ProximoIntentoEn.
    /// Usa un token propio con plazo corto; si no llega a completarse, el caso degrada a la recuperación por lease.
    /// </summary>
    private async Task<ResultadoIndexacion> LiberarAsync(IServiceProvider servicios, Ejecucion ejecucion)
    {
        var trabajo = ejecucion.Trabajo;
        var context = servicios.GetRequiredService<ApplicationDbContext>();
        try
        {
            using var plazo = new CancellationTokenSource(IndexacionOptions.PlazoLiberacion);
            context.ChangeTracker.Clear();
            var indice = Propio(context, trabajo, ejecucion.Version);
            indice.Estado = EstadoIndexacion.Pendiente;
            indice.ProcesandoDesde = null;
            indice.ProcesadoPor = null;
            indice.UpdatedAt = DateTime.UtcNow;
            // Ni Intentos ni ProximoIntentoEn se tocan: la parada limpia no consume un reintento.
            Escribir(context, indice, nameof(DocumentoIndice.Estado), nameof(DocumentoIndice.ProcesandoDesde),
                nameof(DocumentoIndice.ProcesadoPor), nameof(DocumentoIndice.UpdatedAt));
            await context.SaveChangesAsync(plazo.Token);
            ejecucion.Version = indice.Version;

            _logger.LogInformation(
                "[INDEX_RELEASED] Índice {IndiceId} (documento {DocumentoId}, tenant {TenantId}, ejecución {EjecucionId}) liberado por parada del host; lotes completados {Lotes}; intentos sin cambio.",
                trabajo.IndiceId, trabajo.DocumentoId, trabajo.TenantId, ejecucion.Id, ejecucion.LotesCompletados);
            return ResultadoIndexacion.Liberado;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ResultadoIndexacion.Abandonado;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[INDEX_RELEASE_FAILED] Índice {IndiceId}: no se pudo liberar en la parada ({TipoError}); lo recuperará el lease.",
                trabajo.IndiceId, ex.GetType().Name);
            return ResultadoIndexacion.SinEstado;
        }
    }

    // ── Paso 6: purga (§24) ──────────────────────────────────────────────

    public async Task<int> PurgarAsync(IReadOnlyCollection<Guid> tenantsHabilitados, CancellationToken cancellationToken = default)
    {
        List<FilaTenant> candidatos;
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            candidatos = await context.Database.SqlQueryRaw<FilaTenant>(
                """
                SELECT "Id", "TenantId" FROM documento_indices
                WHERE "Estado" IN (4, 5)
                ORDER BY "UpdatedAt" NULLS FIRST, "CreatedAt", "Id"
                LIMIT {0}
                """, _options.LotePurga).ToListAsync(cancellationToken);
        }

        var habilitados = tenantsHabilitados.ToHashSet();
        var purgados = 0;
        foreach (var fila in candidatos)
        {
            try
            {
                if (await PurgarUnoAsync(fila.Id, fila.TenantId, habilitados.Contains(fila.TenantId), cancellationToken))
                {
                    purgados++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("[INDEX_PURGE_ERROR] Error al purgar el índice {IndiceId}. Error: {TipoError}.", fila.Id, ex.GetType().Name);
            }
        }

        return purgados;
    }

    private async Task<bool> PurgarUnoAsync(Guid indiceId, Guid tenantId, bool tenantHabilitado, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentTenantService>().SetTenantId(tenantId);
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var auditoria = scope.ServiceProvider.GetRequiredService<IAuditService>();

        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            context.ChangeTracker.Clear();
            await using var transaccion = await context.Database.BeginTransactionAsync(ct);
            var indice = (await context.DocumentoIndices
                .FromSqlRaw(
                    """
                    SELECT i.*, i.xmin FROM documento_indices i
                    WHERE i."Id" = {0} AND i."Estado" IN (4, 5)
                    FOR UPDATE SKIP LOCKED
                    """, indiceId)
                .IgnoreQueryFilters()
                .ToListAsync(ct)).SingleOrDefault();
            if (indice is null)
            {
                await transaccion.RollbackAsync(ct);
                return false;
            }

            var motivo = indice.Estado == EstadoIndexacion.Obsoleto
                ? MotivosIndexacion.PerfilObsoleto
                : tenantHabilitado ? MotivosIndexacion.DocumentoEliminado : MotivosIndexacion.TenantDesactivado;

            // Borrado físico: los fragmentos se van en cascada (FK de la 8.1).
            context.DocumentoIndices.Remove(indice);
            await auditoria.LogInTransactionAsync(EntidadDocumento, indice.DocumentoId.ToString(), "DOCUMENT_INDEX_PURGED", null, new
            {
                actor = ActorWorker,
                indiceId = indice.Id,
                expedienteId = indice.ExpedienteId,
                perfil = indice.Perfil,
                motivo
            }, ct);

            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaccion.RollbackAsync(ct);
                return false;
            }

            await transaccion.CommitAsync(ct);
            return true;
        }, cancellationToken);
    }

    // ── AIUsageLog en contexto independiente (§22.2) ─────────────────────

    private async Task RegistrarConsumoIndependienteAsync(Ejecucion ejecucion, ResultadoIndexacion resultado)
    {
        if (ejecucion.ConsumoRegistrado)
        {
            return;
        }

        // Parada limpia: intento interrumpido, no un error (CodigoError NULL). En el resto, el código del intento.
        var codigo = resultado == ResultadoIndexacion.Liberado ? null : ejecucion.CodigoError;
        var uso = ejecucion.CrearUso(exitoso: false, codigo, DateTime.UtcNow);
        if (uso is null)
        {
            return;   // sin lotes completados, o algún lote sin consumo informado: no se inventa nada (§22.3)
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<ICurrentTenantService>().SetTenantId(ejecucion.Trabajo.TenantId);
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            context.AIUsageLogs.Add(uso);
            await context.SaveChangesAsync(CancellationToken.None);
            ejecucion.ConsumoRegistrado = true;
        }
        catch (Exception ex)
        {
            _logger.LogError("[INDEX_USAGE_NOT_RECORDED] No se pudo registrar el consumo del intento {EjecucionId} (índice {IndiceId}). Error: {TipoError}.",
                ejecucion.Id, ejecucion.Trabajo.IndiceId, ex.GetType().Name);
        }
    }

    // ── Utilidades ───────────────────────────────────────────────────────

    /// <summary>Entidad propia adjunta con el xmin del worker: SaveChanges emite UPDATE … WHERE "Id" = … AND xmin = ….</summary>
    private static DocumentoIndice Propio(ApplicationDbContext context, TrabajoIndexacion trabajo, uint version)
    {
        var indice = new DocumentoIndice
        {
            Id = trabajo.IndiceId,
            TenantId = trabajo.TenantId,
            DocumentoId = trabajo.DocumentoId,
            ExpedienteId = trabajo.ExpedienteId,
            Perfil = trabajo.Perfil,
            Estado = EstadoIndexacion.Procesando,
            Version = version
        };
        context.Attach(indice);
        return indice;
    }

    /// <summary>
    /// Marca como modificadas exactamente las propiedades que la transición escribe. La entidad adjunta parte de
    /// valores por defecto, así que EF no detectaría, por ejemplo, poner a NULL un lease que en la base tiene valor.
    /// </summary>
    private static void Escribir(ApplicationDbContext context, DocumentoIndice indice, params string[] propiedades)
    {
        var entrada = context.Entry(indice);
        foreach (var propiedad in propiedades)
        {
            entrada.Property(propiedad).IsModified = true;
        }
    }

    private async Task<Origen> LeerOrigenAsync(ApplicationDbContext context, TrabajoIndexacion trabajo, CancellationToken ct)
    {
        // Consulta explícita por tenant y documento (el scope ya tiene SetTenantId del tenant del índice).
        var documento = await context.Documentos.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == trabajo.TenantId && d.Id == trabajo.DocumentoId)
            .Select(d => new { d.IsDeleted, d.ExpedienteId, d.HashSha256, d.RutaAlmacenamiento, d.ContentType })
            .SingleOrDefaultAsync(ct);
        if (documento is null || documento.IsDeleted)
        {
            return new Origen(SituacionOrigen.Eliminado, null, null, null);
        }

        var expedienteBorrado = await context.Expedientes.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == trabajo.TenantId && e.Id == documento.ExpedienteId)
            .Select(e => (bool?)e.IsDeleted)
            .SingleOrDefaultAsync(ct);
        if (expedienteBorrado != false || !await TenantHabilitadoAsync(context, trabajo.TenantId, ct))
        {
            return new Origen(SituacionOrigen.Eliminado, null, null, null);
        }

        return documento.ExpedienteId != trabajo.ExpedienteId
            ? new Origen(SituacionOrigen.OtroExpediente, null, null, null)
            : new Origen(SituacionOrigen.Vigente, documento.RutaAlmacenamiento, documento.ContentType, documento.HashSha256);
    }

    private static async Task<bool> TenantHabilitadoAsync(ApplicationDbContext context, Guid tenantId, CancellationToken ct)
    {
        var tenant = await context.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Activo, t.ConfiguracionJson })
            .SingleOrDefaultAsync(ct);
        return tenant is { Activo: true } && ConfiguracionTenantIa.IndexacionSemanticaHabilitada(tenant.ConfiguracionJson);
    }

    /// <summary>
    /// Presupuesto PREVENTIVO ESTIMADO (§26.2): suma de TokensTotales (estimación local, no consumo) de los índices del
    /// tenant activados en el día UTC en curso, más la estimación del documento. No interviene AIUsageLog.
    /// </summary>
    private async Task<bool> PresupuestoAgotadoAsync(ApplicationDbContext context, Guid tenantId, long tokensEstimados, CancellationToken ct)
    {
        var usados = await context.Database.SqlQueryRaw<long>(
            """
            SELECT COALESCE(SUM("TokensTotales"), 0)::bigint AS "Value" FROM documento_indices
            WHERE "TenantId" = {0} AND "IndexadoEn" >= date_trunc('day', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'
            """, tenantId).SingleAsync(ct);
        return usados + tokensEstimados > _options.MaxTokensDiariosPorTenant;
    }

    private static async Task<DateTime> AhoraDeLaBaseAsync(ApplicationDbContext context, CancellationToken ct) =>
        DateTime.SpecifyKind(await context.Database.SqlQueryRaw<DateTime>("SELECT now() AS \"Value\"").SingleAsync(ct), DateTimeKind.Utc);

    /// <summary>Backoff del trabajo: min(2^intentos × 1 min, 6 h) con jitter de ±20 %.</summary>
    public static TimeSpan Backoff(int intentos, double? aleatorio = null)
    {
        var exponente = Math.Min(Math.Max(intentos, 0), 20);
        var segundos = Math.Min(Math.Pow(2, exponente) * IndexacionOptions.BackoffBase.TotalSeconds, IndexacionOptions.BackoffMaximo.TotalSeconds);
        var factor = 1 + ((aleatorio ?? Random.Shared.NextDouble()) * 2 - 1) * IndexacionOptions.BackoffJitter;
        return TimeSpan.FromSeconds(Math.Min(segundos * factor, IndexacionOptions.BackoffMaximo.TotalSeconds));
    }

    private void ActivarEnfriamiento() =>
        Interlocked.Exchange(ref _enfriamientoHastaTicks, (_tiempo.GetUtcNow() + IndexacionOptions.Enfriamiento).UtcTicks);

    private static NpgsqlParameter Uuids(string nombre, IReadOnlyCollection<Guid> valores) =>
        new(nombre, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = valores.ToArray() };

    /// <summary>Errores de la base de datos (también los que quedan tras agotar la estrategia de reintentos de EF).</summary>
    private static bool EsErrorDePersistencia(Exception ex)
    {
        for (var actual = ex; actual != null; actual = actual.InnerException)
        {
            if (actual is DbException or DbUpdateException or RetryLimitExceededException)
            {
                return true;
            }
        }

        return false;
    }

    private enum SituacionOrigen { Vigente, Eliminado, OtroExpediente, HashDistinto }

    private enum Desenlace { Activado, Perdido, Purga, OtroExpediente, HashDistinto }

    private sealed record Origen(SituacionOrigen Situacion, string? Ruta, string? ContentType, string? HashEsperado);

    private sealed class FilaTenant
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
    }

    private sealed class FilaAdquirida
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid DocumentoId { get; set; }
        public Guid ExpedienteId { get; set; }
        public string Perfil { get; set; } = string.Empty;
        public int Intentos { get; set; }
        public long Version { get; set; }
    }

    private sealed class FilaDocumento
    {
        public bool IsDeleted { get; set; }
        public Guid ExpedienteId { get; set; }
        public string? HashSha256 { get; set; }
    }

    /// <summary>Estado en memoria de un intento: xmin vigente, lotes y consumo real informado.</summary>
    private sealed class Ejecucion(TrabajoIndexacion trabajo)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public TrabajoIndexacion Trabajo { get; } = trabajo;
        public uint Version { get; set; } = trabajo.Version;
        public int Intento => Trabajo.Intentos + 1;
        public string? HashContenido { get; set; }
        public int LotesTotales { get; set; }
        public int LotesCompletados { get; private set; }
        public int LotesSinConsumoInformado { get; private set; }
        public long DuracionProveedorMs { get; set; }
        public string? ProviderId { get; set; }
        public string? ModelId { get; set; }
        public string? CodigoError { get; set; }
        public bool ConsumoRegistrado { get; set; }
        public ResultadoIndexacion ResultadoDelLatido { get; set; } = ResultadoIndexacion.Abandonado;

        private long _tokensInformados;
        private readonly Guid _usoId = Guid.NewGuid();

        /// <summary>Consumo real: suma de lo informado por el proveedor; null si algún lote no lo informó o no hubo lotes.</summary>
        public long? TokensInformados => LotesCompletados > 0 && LotesSinConsumoInformado == 0 ? _tokensInformados : null;

        /// <summary>Id de la fila de AIUsageLog que se escribirá para este intento, o null si no corresponde escribirla.</summary>
        public Guid? UsoPrevistoId => TokensInformados is null ? null : _usoId;

        public void RegistrarLote(int? tokensEntrada)
        {
            LotesCompletados++;
            if (tokensEntrada is { } tokens)
            {
                _tokensInformados += tokens;
            }
            else
            {
                LotesSinConsumoInformado++;
            }
        }

        /// <summary>
        /// Fila de AIUsageLog del intento (§22.2), o null: sin lotes completados no hay consumo, y si algún lote no
        /// informó sus tokens no se escribe un 0 ni una estimación (§22.3).
        /// </summary>
        public AIUsageLog? CrearUso(bool exitoso, string? codigoError, DateTime ahora)
        {
            if (TokensInformados is not { } tokens || ProviderId is null || ModelId is null)
            {
                return null;
            }

            var reales = (int)Math.Min(tokens, int.MaxValue);
            return new AIUsageLog
            {
                Id = _usoId,
                TenantId = Trabajo.TenantId,
                UsuarioId = null,
                Origen = OrigenUsoIA.Worker,
                ActorSistema = ActorWorker,
                CasoUso = AICasoUso.IndexacionSemantica,
                ProviderId = ProviderId,
                ModelId = ModelId,
                TokensEntrada = reales,     // solo consumo informado por el proveedor
                TokensSalida = 0,           // un embedding no produce tokens de salida
                TotalTokens = reales,
                DuracionMs = (int)Math.Min(DuracionProveedorMs, int.MaxValue),
                CostoEstimadoUsd = null,    // sin precio oficial verificado (DP-4)
                Exitoso = exitoso,
                CodigoError = codigoError,
                CreatedAt = ahora
            };
        }
    }
}
