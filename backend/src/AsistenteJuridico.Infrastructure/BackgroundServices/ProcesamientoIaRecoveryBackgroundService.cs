using System.IO.Hashing;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AsistenteJuridico.Infrastructure.BackgroundServices;

/// <summary>
/// Fase 6.X (X1) — Recupera documentos atascados en Procesando cuyo lease venció: Procesando -> Fallido, con
/// auditoría AI_PROCESSING_RECOVERED en el mismo SaveChanges. Solo este worker recupera; no hay endpoint.
///
/// Sin duplicados entre instancias: un scope por tenant con SetTenantId (necesario para la auditoría), advisory lock
/// transaccional propio por tenant y UPDATE con WHERE xmin por documento (una fila se recupera una sola vez).
///
/// Inicio del lease: IaProcesandoDesde. El fallback COALESCE(IaProcesandoDesde, UpdatedAt, CreatedAt) solo actúa en
/// los documentos históricos que ya estaban en Procesando con IaProcesandoDesde = NULL antes de la migración; toda
/// transición nueva a Procesando fija IaProcesandoDesde.
/// </summary>
public class ProcesamientoIaRecoveryBackgroundService : BackgroundService
{
    public const string MotivoLeaseVencido = "LEASE_EXPIRED";
    public const string UsuarioSistema = "system:ia-recovery";

    /// <summary>Semilla propia: el espacio de claves no coincide con el del worker de alertas (semilla 0).</summary>
    private const long SemillaLock = 0x6658_0001;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProcesamientoIaRecoveryOptions _options;
    private readonly ILogger<ProcesamientoIaRecoveryBackgroundService> _logger;

    public ProcesamientoIaRecoveryBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<ProcesamientoIaRecoveryOptions> options,
        ILogger<ProcesamientoIaRecoveryBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("[IA_RECOVERY] Recuperación de Procesando deshabilitada por configuración.");
            return;
        }

        var intervalo = TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EjecutarCicloAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError("[IA_RECOVERY_ERROR] Error en el ciclo de recuperación de Procesando. Error: {TipoError}.", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(intervalo, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Un ciclo completo: tenants con documentos vencidos y recuperación por tenant. Devuelve el total recuperado.</summary>
    public async Task<int> EjecutarCicloAsync(DateTime ahoraUtc, CancellationToken cancellationToken = default)
    {
        var limite = LimiteDelLease(ahoraUtc);
        List<Guid> tenants;
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // Consulta global: IgnoreQueryFilters con predicado explícito; solo se leen TenantId.
            tenants = await context.Documentos
                .IgnoreQueryFilters()
                .Where(d => d.EstadoIa == EstadoProcesamientoIa.Procesando
                    && (d.IaProcesandoDesde ?? d.UpdatedAt ?? d.CreatedAt) < limite)
                .Select(d => d.TenantId)
                .Distinct()
                .ToListAsync(cancellationToken);
        }

        var total = 0;
        foreach (var tenantId in tenants)
        {
            try
            {
                total += await RecuperarTenantAsync(tenantId, ahoraUtc, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("[IA_RECOVERY_ERROR] Error recuperando documentos del tenant {TenantId}. Error: {TipoError}.", tenantId, ex.GetType().Name);
            }
        }

        return total;
    }

    /// <summary>
    /// Recupera los documentos vencidos de un tenant. Si otra instancia tiene el lock del tenant, no hace nada (0).
    /// </summary>
    public async Task<int> RecuperarTenantAsync(Guid tenantId, DateTime ahoraUtc, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentTenantService>().SetTenantId(tenantId);
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var auditoria = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var limite = LimiteDelLease(ahoraUtc);
        var claveLock = ObtenerClaveLock(tenantId);

        var estrategia = context.Database.CreateExecutionStrategy();
        return await estrategia.ExecuteAsync(async ct =>
        {
            context.ChangeTracker.Clear();
            await using var transaccion = await context.Database.BeginTransactionAsync(ct);

            var bloqueado = await context.Database
                .SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock({0}) AS \"Value\"", claveLock)
                .SingleAsync(ct);
            if (!bloqueado)
            {
                _logger.LogDebug("[IA_RECOVERY] Tenant {TenantId} ocupado por otra instancia. Se omite.", tenantId);
                await transaccion.RollbackAsync(ct);
                return 0;
            }

            var vencidos = await context.Documentos
                .IgnoreQueryFilters()
                .Where(d => d.TenantId == tenantId
                    && d.EstadoIa == EstadoProcesamientoIa.Procesando
                    && (d.IaProcesandoDesde ?? d.UpdatedAt ?? d.CreatedAt) < limite)
                .OrderBy(d => d.Id)
                .Take(Math.Max(1, _options.BatchSize))
                .ToListAsync(ct);

            var recuperados = 0;
            foreach (var documento in vencidos)
            {
                // Tras un conflicto anterior el contexto se limpió: se vuelve a adjuntar con sus valores leídos (xmin incluido).
                if (context.Entry(documento).State == EntityState.Detached)
                {
                    context.Attach(documento);
                }

                var procesandoDesde = documento.IaProcesandoDesde ?? documento.UpdatedAt ?? documento.CreatedAt;

                documento.EstadoIa = EstadoProcesamientoIa.Fallido;
                documento.IaProcesandoDesde = null;
                documento.UpdatedAt = DateTime.UtcNow;
                documento.UpdatedBy = UsuarioSistema;

                await auditoria.LogInTransactionAsync("Documento", documento.Id.ToString(), "AI_PROCESSING_RECOVERED", null, new
                {
                    documento.ExpedienteId,
                    ProcesandoDesde = procesandoDesde,
                    Motivo = MotivoLeaseVencido
                }, ct);

                try
                {
                    // UPDATE ... WHERE xmin = leído. Si la operación terminó o la recuperó otro, 0 filas: se omite.
                    // El savepoint automático de EF revierte también la auditoría de este documento.
                    await context.SaveChangesAsync(ct);
                    recuperados++;
                    _logger.LogWarning(
                        "[IA_RECOVERY] Documento {DocumentoId} (tenant {TenantId}) pasó de Procesando a Fallido por {Motivo}.",
                        documento.Id, tenantId, MotivoLeaseVencido);
                }
                catch (DbUpdateConcurrencyException)
                {
                    context.ChangeTracker.Clear();
                    _logger.LogInformation(
                        "[IA_RECOVERY] El documento {DocumentoId} cambió durante la recuperación; no se sobrescribe.", documento.Id);
                }
            }

            await transaccion.CommitAsync(ct);
            return recuperados;
        }, cancellationToken);
    }

    private DateTime LimiteDelLease(DateTime ahoraUtc) => ahoraUtc.AddSeconds(-_options.LeaseSeconds);

    /// <summary>Clave del advisory lock transaccional de recuperación de un tenant (espacio propio, semilla distinta de alertas).</summary>
    public static long ObtenerClaveLock(Guid tenantId) =>
        unchecked((long)XxHash64.HashToUInt64(tenantId.ToByteArray(), SemillaLock));
}
