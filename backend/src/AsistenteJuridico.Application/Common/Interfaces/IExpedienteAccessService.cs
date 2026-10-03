using AsistenteJuridico.Domain.Entities;

namespace AsistenteJuridico.Application.Common.Interfaces;

/// <summary>
/// Servicio de autorización contextual y jerárquica para expedientes y sus recursos hijos
/// (tareas, audiencias, documentos, causas judiciales vinculadas).
/// Aplica aislamiento multi-tenant, restricción a SuperAdmin y límites de asignación de AbogadoJunior.
/// </summary>
public interface IExpedienteAccessService
{
    Task EnsureCanAccessExpedienteAsync(Guid expedienteId, bool requireWriteAccess = false, CancellationToken cancellationToken = default);
    Task EnsureCanAccessExpedienteAsync(Expediente expediente, bool requireWriteAccess = false, CancellationToken cancellationToken = default);
    Task<bool> CanAccessExpedienteAsync(Guid expedienteId, bool requireWriteAccess = false, CancellationToken cancellationToken = default);

    Task<Tarea> EnsureCanAccessTareaAsync(Guid tareaId, bool requireWriteAccess = false, CancellationToken cancellationToken = default);
    Task<Audiencia> EnsureCanAccessAudienciaAsync(Guid audienciaId, bool requireWriteAccess = false, CancellationToken cancellationToken = default);
    Task<Documento> EnsureCanAccessDocumentoAsync(Guid documentoId, bool requireWriteAccess = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Acceso de lectura al listado de documentos de un expediente: las reglas del expediente más la regla
    /// documental del AsistenteLegal (tarea vigente asignada en ese expediente).
    /// </summary>
    Task EnsureCanAccessDocumentosDeExpedienteAsync(Guid expedienteId, CancellationToken cancellationToken = default);
    Task EnsureCanAccessProcesoAsync(Guid procesoId, CancellationToken cancellationToken = default);
}
