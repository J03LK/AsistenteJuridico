using System.Globalization;
using System.Text;
using AsistenteJuridico.Application.Common.Exceptions;

namespace AsistenteJuridico.Infrastructure.Services;

/// <summary>
/// Fase 7.2 — Sanea el nombre original de un archivo subido para guardarlo como metadato
/// (NombreArchivoOriginal) y usarlo al mostrar o descargar. NUNCA se usa como nombre físico.
/// </summary>
public static class NombreArchivoSanitizer
{
    public const int MaxLength = 255;

    /// <summary>
    /// Toma solo el nombre (sin ruta, cortando por '/' y '\'), elimina caracteres de control (Cc) y de formato (Cf,
    /// incluidos los de control de dirección como U+202E), normaliza a NFC, recorta espacios y puntos finales y
    /// trunca a 255 caracteres conservando la extensión. Si no queda nada, error de validación (400).
    /// </summary>
    public static string Sanitizar(string? nombreOriginal)
    {
        if (string.IsNullOrWhiteSpace(nombreOriginal))
        {
            throw new ValidationException(["El nombre original del archivo es obligatorio."]);
        }

        var soloNombre = nombreOriginal[(nombreOriginal.LastIndexOfAny(['/', '\\']) + 1)..];

        var builder = new StringBuilder(soloNombre.Length);
        foreach (var rune in soloNombre.EnumerateRunes())
        {
            var categoria = Rune.GetUnicodeCategory(rune);
            if (categoria is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                continue;
            }

            builder.Append(rune.ToString());
        }

        var limpio = Recortar(builder.ToString().Normalize(NormalizationForm.FormC));
        if (limpio.Length == 0)
        {
            throw new ValidationException(["El nombre original del archivo no es válido."]);
        }

        if (limpio.Length > MaxLength)
        {
            var extension = Path.GetExtension(limpio);
            if (extension.Length >= MaxLength / 2)
            {
                extension = string.Empty;
            }

            var baseNombre = Recortar(TruncarSinPartirSurrogados(limpio[..^extension.Length], MaxLength - extension.Length));
            limpio = baseNombre + extension;
        }

        return limpio;
    }

    private static string Recortar(string valor) => valor.Trim().TrimEnd('.', ' ').Trim();

    private static string TruncarSinPartirSurrogados(string valor, int longitud)
    {
        if (valor.Length <= longitud)
        {
            return valor;
        }

        var corte = char.IsHighSurrogate(valor[longitud - 1]) ? longitud - 1 : longitud;
        return valor[..corte];
    }
}
