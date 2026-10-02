using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Infrastructure.Common;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AsistenteJuridico.Infrastructure.BackgroundServices;

/// <summary>
/// Worker en segundo plano para la evaluación y generación autónoma de alertas procesales.
/// Ejecuta un ciclo periódico cada 15 minutos sin interacción web ni dependencias HTTP.
/// Coordina la ejecución distribuida mediante PostgreSQL Advisory Locks deterministas de 64 bits.
/// </summary>
public class AlertasBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AlertasBackgroundService> _logger;
    private readonly TimeSpan _periodo = TimeSpan.FromMinutes(15);

    public AlertasBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<AlertasBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ALERTAS_WORKER] Iniciando servicio en segundo plano de alertas procesales (Período: {Minutos} min)...", _periodo.TotalMinutes);

        // Ciclo inicial al arrancar el host
        await EjecutarCicloAlertasAsync(stoppingToken);

        using var timer = new PeriodicTimer(_periodo);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await EjecutarCicloAlertasAsync(stoppingToken);
        }

        _logger.LogInformation("[ALERTAS_WORKER] Servicio en segundo plano de alertas detenido.");
    }

    public async Task EjecutarCicloAlertasAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("[ALERTAS_WORKER] Iniciando ciclo periódico de evaluación de alertas procesales...");

            using var initScope = _scopeFactory.CreateScope();
            var initContext = initScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var activeTenants = await initContext.Tenants
                .AsNoTracking()
                .Where(t => t.Activo)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            _logger.LogInformation("[ALERTAS_WORKER] Se identificaron {TotalTenants} tenants activos para procesar.", activeTenants.Count);

            foreach (var tenantId in activeTenants)
            {
                if (cancellationToken.IsCancellationRequested) break;

                await ProcesarTenantConLockAsync(tenantId, cancellationToken);
            }

            _logger.LogInformation("[ALERTAS_WORKER] Ciclo periódico de alertas finalizado exitosamente.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cierre controlado
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ALERTAS_WORKER_ERROR] Error no controlado durante el ciclo de alertas.");
        }
    }

    public async Task ProcesarTenantConLockAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var tenantService = scope.ServiceProvider.GetRequiredService<ICurrentTenantService>();
        tenantService.SetTenantId(tenantId);

        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var alertasService = scope.ServiceProvider.GetRequiredService<IAlertasService>();

        long lockKey = AdvisoryLockHelper.ObtenerTenantLockKey(tenantId);

        // La estrategia de reintentos de Npgsql no admite transacciones abiertas fuera de ella: la unidad completa
        // (transacción, advisory lock, reglas y commit) se ejecuta dentro de la estrategia y, ante un fallo transitorio,
        // se repite entera desde cero.
        var strategy = context.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async ct =>
            {
                // Un reintento no debe arrastrar entidades del intento fallido, cuya transacción ya se revirtió.
                context.ChangeTracker.Clear();

                await using var tx = await context.Database.BeginTransactionAsync(ct);

                // Adquisición atómica de Advisory Lock a nivel de transacción
                bool adquirido = await context.Database
                    .SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock({0}) AS \"Value\"", lockKey)
                    .SingleAsync(ct);

                if (!adquirido)
                {
                    _logger.LogDebug("[ADVISORY_LOCK] Tenant {TenantId} ocupado por otra instancia. Omitiendo ciclo.", tenantId);
                    await tx.RollbackAsync(ct);
                    return;
                }

                _logger.LogInformation("[ALERTAS_WORKER] Advisory Lock adquirido para Tenant {TenantId}. Procesando alertas...", tenantId);
                await alertasService.ProcesarReglasAlertasTenantAsync(tenantId, ct);

                await tx.CommitAsync(ct);
                _logger.LogInformation("[ALERTAS_WORKER] Procesamiento completado para Tenant {TenantId}. Lock liberado automáticamente.", tenantId);
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // La transacción ya se revirtió al salir de su bloque; el siguiente ciclo reintentará el tenant.
            _logger.LogError(ex, "[ALERTAS_WORKER_ERROR] Error procesando alertas para Tenant {TenantId}.", tenantId);
        }
    }
}
