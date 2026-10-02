using System;

namespace AsistenteJuridico.Infrastructure.Common;

/// <summary>
/// Utilidades para validación y resolución estricta de identificadores IANA de zonas horarias.
/// </summary>
public static class TimeZoneHelper
{
    public const string DefaultTimeZoneId = "America/Guayaquil";

    /// <summary>
    /// Valida si un identificador corresponde a una zona horaria IANA válida reconocida por el sistema.
    /// </summary>
    public static bool EsZonaIanaValida(string? zonaHorariaId)
    {
        if (string.IsNullOrWhiteSpace(zonaHorariaId))
        {
            return false;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(zonaHorariaId.Trim(), out _);
    }

    /// <summary>
    /// Intenta resolver la zona horaria sin fallback silencioso.
    /// </summary>
    public static bool TryGetTimeZone(string? zonaHorariaId, out TimeZoneInfo timeZone)
    {
        timeZone = null!;
        if (string.IsNullOrWhiteSpace(zonaHorariaId))
        {
            return false;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(zonaHorariaId.Trim(), out timeZone!);
    }
}
