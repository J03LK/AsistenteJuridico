using System;
using AsistenteJuridico.Domain.Common;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Registro inmutable de consumo y métricas de invocaciones de IA.
/// Protegido a nivel de base de datos con triggers PostgreSQL que rechazan UPDATE y DELETE.
/// </summary>
public class AIUsageLog : BaseEntity, IMultiTenant
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid UsuarioId { get; set; }
    public Usuario Usuario { get; set; } = null!;

    /// <summary>
    /// Identificador de REFERENCIA HISTÓRICA a la conversación.
    /// Para preservar la estricta inmutabilidad del log (los triggers de PostgreSQL bloquean UPDATE y DELETE con RAISE EXCEPTION),
    /// este campo no actúa como una FK referencial restrictiva/mutante tras la purga física de AIConversation: permanece inalterado
    /// como trazabilidad histórica sin otorgar acceso ni usarse para autorización ni sufrir modificaciones (sin UPDATE).
    /// </summary>
    public Guid? ConversationId { get; set; }
    public AIConversation? Conversation { get; set; }

    public AICasoUso CasoUso { get; set; }

    public string ProviderId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;

    public int TokensEntrada { get; set; }
    public int TokensSalida { get; set; }
    public int TotalTokens { get; set; }
    public int DuracionMs { get; set; }

    public decimal? CostoEstimadoUsd { get; set; }
    public bool Exitoso { get; set; } = true;
    public string? CodigoError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
