namespace AsistenteJuridico.Application.Features.Indexacion;

/// <summary>
/// Fase 8.4 — Trabajo de indexación adquirido por un worker: la fila de documento_indices en estado Procesando.
/// <see cref="Version"/> es el xmin devuelto por la adquisición: la prueba de propiedad del trabajo.
/// </summary>
public sealed record TrabajoIndexacion(
    Guid IndiceId, Guid TenantId, Guid DocumentoId, Guid ExpedienteId, string Perfil, int Intentos, uint Version);

/// <summary>Resultado de procesar un trabajo (para el worker, los logs y las pruebas).</summary>
public enum ResultadoIndexacion
{
    /// <summary>Índice confirmado: Indexado, con todos sus fragmentos.</summary>
    Indexado,
    /// <summary>Error transitorio: de vuelta a Pendiente con Intentos + 1 y backoff.</summary>
    Reintento,
    /// <summary>Error permanente o intentos agotados: Fallido.</summary>
    Fallido,
    /// <summary>Presupuesto preventivo agotado: Pendiente hasta el día siguiente, sin sumar intento.</summary>
    Aplazado,
    /// <summary>Parada limpia del host: Procesando → Pendiente, sin sumar intento.</summary>
    Liberado,
    /// <summary>El worker perdió el trabajo (xmin distinto): no escribió nada.</summary>
    Abandonado,
    /// <summary>Documento o expediente borrado, o tenant desactivado: PurgaPendiente.</summary>
    Purga,
    /// <summary>El documento ya no pertenece al expediente del índice: Obsoleto.</summary>
    Obsoleto,
    /// <summary>Error persistente de la base: no se escribió ningún estado; lo resolverá la recuperación por lease.</summary>
    SinEstado
}

/// <summary>
/// Fase 8.4 — Pasos de la indexación semántica (FASE_8_4_CONTRATO.md §9). Cada paso corre en sus propias
/// transacciones cortas. El worker los encadena en cada ciclo; las pruebas los ejecutan de forma explícita.
/// No expone búsqueda ni endpoints: solo construye, activa y purga índices.
/// </summary>
public interface IIndexacionSemanticaService
{
    /// <summary>Perfil activo: firma del proveedor de embeddings más las versiones de la 8.2.</summary>
    string PerfilActivo { get; }

    /// <summary>Paso 0: tenants activos con la indexación semántica habilitada (evaluado en la aplicación).</summary>
    Task<IReadOnlyList<Guid>> TenantsHabilitadosAsync(CancellationToken cancellationToken = default);

    /// <summary>Paso 1: crea los índices Pendiente que falten. Devuelve cuántos sembró.</summary>
    Task<int> SembrarAsync(IReadOnlyCollection<Guid> tenantsHabilitados, CancellationToken cancellationToken = default);

    /// <summary>Paso 2: PurgaPendiente, Obsoleto de perfiles inactivos y reactivación por límite de fragmentos.</summary>
    Task<int> MarcarAsync(IReadOnlyCollection<Guid> tenantsHabilitados, CancellationToken cancellationToken = default);

    /// <summary>Paso 3: recupera los índices Procesando con el lease vencido. Devuelve cuántos recuperó.</summary>
    Task<int> RecuperarLeasesAsync(CancellationToken cancellationToken = default);

    /// <summary>Paso 4: adquiere hasta <paramref name="maximo"/> trabajos con FOR UPDATE SKIP LOCKED.</summary>
    Task<IReadOnlyList<TrabajoIndexacion>> AdquirirAsync(
        IReadOnlyCollection<Guid> tenantsHabilitados, int maximo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Paso 5: procesa un trabajo adquirido. Nunca lanza por un fallo del documento: lo clasifica y deja el índice
    /// en el estado que corresponda. <paramref name="parada"/> es el token de parada del host.
    /// </summary>
    Task<ResultadoIndexacion> ProcesarAsync(TrabajoIndexacion trabajo, CancellationToken parada = default);

    /// <summary>Paso 6: purga hasta el lote configurado de índices Obsoleto o PurgaPendiente.</summary>
    Task<int> PurgarAsync(IReadOnlyCollection<Guid> tenantsHabilitados, CancellationToken cancellationToken = default);
}
