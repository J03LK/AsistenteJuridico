using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AsistenteJuridico.Application.Common.Interfaces;

/// <summary>
/// Contrato del contexto de base de datos para la capa de aplicación.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<Usuario> Usuarios { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<Cliente> Clientes { get; }
    DbSet<Expediente> Expedientes { get; }
    DbSet<ProcesoJudicial> ProcesosJudiciales { get; }
    DbSet<ExpedienteProcesoJudicial> ExpedienteProcesosJudiciales { get; }
    DbSet<Documento> Documentos { get; }
    DbSet<Tarea> Tareas { get; }
    DbSet<Audiencia> Audiencias { get; }
    DbSet<TenantSecuencia> TenantSecuencias { get; }
    DbSet<HistorialAuditoria> HistorialAuditorias { get; }
    DbSet<AlertaProcesal> AlertasProcesales { get; }
    DbSet<AIConversation> AIConversations { get; }
    DbSet<AIMessage> AIMessages { get; }
    DbSet<AIUsageLog> AIUsageLogs { get; }
    DbSet<DocumentoIndice> DocumentoIndices { get; }
    DbSet<DocumentoFragmento> DocumentoFragmentos { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
