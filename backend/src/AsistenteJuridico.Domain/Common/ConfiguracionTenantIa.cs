using System.Text.Json;

namespace AsistenteJuridico.Domain.Common;

/// <summary>
/// Fase 8 (contrato DA8-13) — Lectura de la activación de la indexación semántica guardada en
/// <c>Tenant.ConfiguracionJson</c>, en la clave <c>ia.indexacionSemantica</c>:
/// <code>{ "ia": { "indexacionSemantica": true } }</code>
/// Desactivada por defecto: sin configuración, con JSON inválido o con un valor que no sea el booleano true, la
/// indexación NO está activa (la activación debe ser explícita).
/// </summary>
public static class ConfiguracionTenantIa
{
    public const string Seccion = "ia";
    public const string ClaveIndexacionSemantica = "indexacionSemantica";

    public static bool IndexacionSemanticaHabilitada(string? configuracionJson)
    {
        if (string.IsNullOrWhiteSpace(configuracionJson))
        {
            return false;
        }

        try
        {
            using var documento = JsonDocument.Parse(configuracionJson);
            return documento.RootElement.ValueKind == JsonValueKind.Object
                && documento.RootElement.TryGetProperty(Seccion, out var ia)
                && ia.ValueKind == JsonValueKind.Object
                && ia.TryGetProperty(ClaveIndexacionSemantica, out var valor)
                && valor.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
