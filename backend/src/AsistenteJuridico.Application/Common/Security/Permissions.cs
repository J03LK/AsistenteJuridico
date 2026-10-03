namespace AsistenteJuridico.Application.Common.Security;

/// <summary>
/// Catálogo de permisos granulares para autorización basada en políticas (PBAC).
/// </summary>
public static class Permissions
{
    // Expedientes
    public const string ExpedientesRead = "Expedientes.Read";
    public const string ExpedientesCreate = "Expedientes.Create";
    public const string ExpedientesUpdate = "Expedientes.Update";
    public const string ExpedientesDelete = "Expedientes.Delete";
    public const string ExpedientesAssign = "Expedientes.Assign";
    public const string ExpedientesCloseForce = "Expedientes.CloseForce";

    // Clientes
    public const string ClientesRead = "Clientes.Read";
    public const string ClientesCreate = "Clientes.Create";
    public const string ClientesUpdate = "Clientes.Update";
    public const string ClientesDelete = "Clientes.Delete";

    // Tareas
    public const string TareasRead = "Tareas.Read";
    public const string TareasManage = "Tareas.Manage";

    // Documentos
    public const string DocumentosRead = "Documentos.Read";
    public const string DocumentosUpload = "Documentos.Upload";
    public const string DocumentosUpdate = "Documentos.Update";
    public const string DocumentosDelete = "Documentos.Delete";

    // Procesos SATJE
    public const string ProcesosRead = "Procesos.Read";
    public const string ProcesosLink = "Procesos.Link";

    // Audiencias y Agenda
    public const string AudienciasManage = "Audiencias.Manage";

    // Usuarios del Estudio
    public const string UsuariosRead = "Usuarios.Read";
    public const string UsuariosManage = "Usuarios.Manage";

    // Tenant y Auditoría
    public const string TenantManage = "Tenant.Manage";
    public const string AuditRead = "Audit.Read";

    // Dashboard, Agenda y Alertas (Fase 5)
    public const string DashboardRead = "Dashboard.Read";
    public const string AgendaRead = "Agenda.Read";
    public const string AlertasRead = "Alertas.Read";
    public const string AlertasManage = "Alertas.Manage";

    // Inteligencia Artificial (Fase 6)
    public const string AIChat = "AI.Chat";
    public const string AISummarize = "AI.Summarize";
    public const string AIExtract = "AI.Extract";
    public const string AIDraft = "AI.Draft";
    public const string AIUsageRead = "AI.UsageRead";

    /// <summary>
    /// Retorna los permisos asignados por defecto a un rol del estudio jurídico.
    /// </summary>
    public static IReadOnlyList<string> GetPermissionsForRole(string role)
    {
        return role switch
        {
            Roles.SuperAdmin =>
            [
                UsuariosRead,
                UsuariosManage,
                TenantManage,
                AuditRead
            ],
            Roles.AdminEstudio =>
            [
                ExpedientesRead,
                ExpedientesCreate,
                ExpedientesUpdate,
                ExpedientesDelete,
                ExpedientesAssign,
                ExpedientesCloseForce,
                ClientesRead,
                ClientesCreate,
                ClientesUpdate,
                ClientesDelete,
                TareasRead,
                TareasManage,
                DocumentosRead,
                DocumentosUpload,
                DocumentosUpdate,
                DocumentosDelete,
                ProcesosRead,
                ProcesosLink,
                AudienciasManage,
                UsuariosRead,
                UsuariosManage,
                AuditRead,
                TenantManage,
                DashboardRead,
                AgendaRead,
                AlertasRead,
                AlertasManage,
                AIChat,
                AISummarize,
                AIExtract,
                AIDraft,
                AIUsageRead
            ],
            Roles.AbogadoSenior =>
            [
                ExpedientesRead,
                ExpedientesCreate,
                ExpedientesUpdate,
                ExpedientesAssign,
                ExpedientesCloseForce,
                ClientesRead,
                ClientesCreate,
                ClientesUpdate,
                TareasRead,
                TareasManage,
                DocumentosRead,
                DocumentosUpload,
                DocumentosUpdate,
                DocumentosDelete,
                ProcesosRead,
                ProcesosLink,
                AudienciasManage,
                DashboardRead,
                AgendaRead,
                AlertasRead,
                AlertasManage,
                AIChat,
                AISummarize,
                AIExtract,
                AIDraft,
                AIUsageRead
            ],
            Roles.AbogadoJunior =>
            [
                ExpedientesRead,
                ExpedientesUpdate,
                ClientesRead,
                ClientesCreate,
                TareasRead,
                TareasManage,
                DocumentosRead,
                DocumentosUpload,
                DocumentosUpdate,
                ProcesosRead,
                AudienciasManage,
                DashboardRead,
                AgendaRead,
                AlertasRead,
                AlertasManage,
                AIChat,
                AISummarize,
                AIExtract,
                AIDraft
            ],
            Roles.AsistenteLegal =>
            [
                ExpedientesRead,
                ClientesRead,
                TareasRead,
                TareasManage,
                // Fase 7 (D2): sin Documentos.Upload; solo lee documentos con tarea vigente en el expediente.
                DocumentosRead,
                ProcesosRead,
                AudienciasManage,
                DashboardRead,
                AgendaRead,
                AlertasRead,
                AlertasManage,
                AIChat,
                AISummarize,
                AIExtract,
                AIDraft
            ],
            _ => []
        };
    }
}
