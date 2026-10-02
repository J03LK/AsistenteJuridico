using System;
using AsistenteJuridico.Domain.Common;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Mensaje intercambiado dentro de una conversación de IA.
/// IMPORTANTE: El System Prompt interno NUNCA se persiste en esta entidad; solo se registra SystemPromptVersion.
/// </summary>
public class AIMessage : BaseEntity, IMultiTenant
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid ConversationId { get; set; }
    public AIConversation Conversation { get; set; } = null!;

    public AIRolMensaje Rol { get; set; }
    public string Contenido { get; set; } = string.Empty;

    /// <summary>
    /// Identificador o versión de la directiva de sistema utilizada (sin almacenar el prompt completo).
    /// </summary>
    public string SystemPromptVersion { get; set; } = "legal-assistant-v1";

    public int? TokensEntrada { get; set; }
    public int? TokensSalida { get; set; }
    public int? DuracionMs { get; set; }

    public string? ModelId { get; set; }
    public string? ProviderId { get; set; }
    public string? FinishReason { get; set; }

    /// <summary>
    /// Indica si la respuesta se basó en contexto autorizado del expediente (true) o si es consulta doctrinaria general (false).
    /// </summary>
    public bool ContextoAutorizado { get; set; } = false;

    /// <summary>
    /// Descargo de responsabilidad legal renderizado y persistido para transparencia deontológica.
    /// </summary>
    public string Disclaimer { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
