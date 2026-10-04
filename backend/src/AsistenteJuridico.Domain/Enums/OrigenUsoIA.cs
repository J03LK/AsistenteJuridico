namespace AsistenteJuridico.Domain.Enums;

/// <summary>
/// Fase 8 — Actor que originó un consumo de IA (contrato §14.1). Una operación iniciada por una petición HTTP
/// autenticada siempre es <see cref="Usuario"/> y lleva el usuario; Worker y Sistema llevan su identificador en
/// <c>ActorSistema</c>. Lo garantiza un CHECK en <c>ai_usage_logs</c>.
/// </summary>
public enum OrigenUsoIA : short
{
    Usuario = 1,
    Worker = 2,
    Sistema = 3
}
