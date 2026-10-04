using AsistenteJuridico.Application.Features.Indexacion;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AsistenteJuridico.Infrastructure.BackgroundServices;

/// <summary>
/// Fase 8.4 — Worker de indexación semántica (FASE_8_4_CONTRATO.md §9). En cada ciclo: tenants habilitados, sembrado,
/// marcado, recuperación de leases, adquisición (FOR UPDATE SKIP LOCKED), proceso y purga.
///
/// Los documentos se procesan en paralelo hasta MaxParalelismo; dentro de un documento los lotes de embeddings son
/// secuenciales, así que nunca hay más de MaxParalelismo peticiones simultáneas al proveedor por instancia.
///
/// Parada limpia (§23): el token de parada llega a cada trabajo, que se libera a Pendiente sin sumar intento. Una
/// caída abrupta se resuelve por lease en la recuperación de cualquier instancia.
/// </summary>
public sealed class IndexacionSemanticaBackgroundService : BackgroundService
{
    private readonly IIndexacionSemanticaService _servicio;
    private readonly IndexacionOptions _options;
    private readonly ILogger<IndexacionSemanticaBackgroundService> _logger;
    private readonly List<Task<ResultadoIndexacion>> _enCurso = [];

    public IndexacionSemanticaBackgroundService(
        IIndexacionSemanticaService servicio,
        IOptions<IndexacionOptions> options,
        ILogger<IndexacionSemanticaBackgroundService> logger)
    {
        _servicio = servicio;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Trabajos adquiridos por esta instancia que aún no han terminado.</summary>
    public int TrabajosEnCurso
    {
        get
        {
            lock (_enCurso)
            {
                return _enCurso.Count(t => !t.IsCompleted);
            }
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled)
        {
            // Si el perfil activo no cabe en documento_indices.Perfil, la aplicación no arranca.
            _ = _servicio.PerfilActivo;
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("[INDEX_WORKER] Indexación semántica deshabilitada por configuración.");
            return;
        }

        var intervalo = TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EjecutarCicloAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError("[INDEX_WORKER_ERROR] Error en el ciclo de indexación semántica. Error: {TipoError}.", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(intervalo, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Parada limpia: cada trabajo en curso recibe el token y se libera por sí mismo.
        await EsperarTrabajosAsync();
    }

    /// <summary>Un ciclo completo. Los trabajos adquiridos siguen procesándose después de que el ciclo devuelva.</summary>
    public async Task<int> EjecutarCicloAsync(CancellationToken stoppingToken)
    {
        var tenants = await _servicio.TenantsHabilitadosAsync(stoppingToken);
        await _servicio.SembrarAsync(tenants, stoppingToken);
        await _servicio.MarcarAsync(tenants, stoppingToken);
        await _servicio.RecuperarLeasesAsync(stoppingToken);

        int huecos;
        lock (_enCurso)
        {
            _enCurso.RemoveAll(t => t.IsCompleted);
            huecos = _options.MaxParalelismo - _enCurso.Count;
        }

        var trabajos = await _servicio.AdquirirAsync(tenants, huecos, stoppingToken);
        lock (_enCurso)
        {
            foreach (var trabajo in trabajos)
            {
                // ProcesarAsync nunca lanza por un fallo del documento: lo clasifica y deja el índice en su estado.
                _enCurso.Add(Task.Run(() => _servicio.ProcesarAsync(trabajo, stoppingToken), CancellationToken.None));
            }
        }

        await _servicio.PurgarAsync(tenants, stoppingToken);
        return trabajos.Count;
    }

    /// <summary>Espera a que terminen los trabajos en curso (en una parada, a que se liberen).</summary>
    public async Task EsperarTrabajosAsync()
    {
        Task<ResultadoIndexacion>[] pendientes;
        lock (_enCurso)
        {
            pendientes = [.. _enCurso];
        }

        try
        {
            await Task.WhenAll(pendientes);
        }
        catch (Exception ex)
        {
            _logger.LogError("[INDEX_WORKER_ERROR] Un trabajo de indexación terminó con una excepción no controlada: {TipoError}.", ex.GetType().Name);
        }
    }
}
