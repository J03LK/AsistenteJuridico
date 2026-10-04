using System.Globalization;
using System.Text;

namespace AsistenteJuridico.Application.Common.Indexacion;

/// <summary>
/// Fase 8.2 — Normalización <c>norm-v1</c> (contrato 8.2 §7). Función pura, determinista, idempotente e independiente
/// de la cultura. Pasos, en este orden:
/// 1. NFC.
/// 2. \r\n → \n; después \r aislado, U+0085 (NEL), U+2028 y U+2029 → \n.
/// 3. Se eliminan los caracteres de la categoría Cc salvo \n y \t.
/// 4. Toda secuencia de dos o más caracteres de la categoría Zs se sustituye por un U+0020 (un Zs aislado no cambia).
/// No cambia mayúsculas, tildes, números ni puntuación; no colapsa \n ni \t; no elimina Cf; no recorta; no anonimiza.
/// El texto UTF-16 mal formado (sustituto aislado) no se repara ni se sustituye: se rechaza con ArgumentException. La
/// extracción segmentada ya lo clasifica como InvalidContent, así que desde un documento nunca llega aquí.
/// </summary>
public static class TextoNormalizador
{
    public const string Version = "norm-v1";

    private const char Nel = (char)0x0085;
    private const char SeparadorLinea = (char)0x2028;
    private const char SeparadorParrafo = (char)0x2029;

    public static string Normalizar(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);
        if (texto.Length == 0)
        {
            return texto;
        }

        if (!EsUtf16BienFormado(texto))
        {
            throw new ArgumentException("Texto UTF-16 mal formado: contiene un sustituto aislado.", nameof(texto));
        }

        var nfc = texto.Normalize(NormalizationForm.FormC);

        // Pasos 2 y 3.
        var lineas = new StringBuilder(nfc.Length);
        for (var i = 0; i < nfc.Length; i++)
        {
            var c = nfc[i];
            if (c == '\r')
            {
                lineas.Append('\n');
                if (i + 1 < nfc.Length && nfc[i + 1] == '\n')
                {
                    i++;
                }

                continue;
            }

            if (c is Nel or SeparadorLinea or SeparadorParrafo)
            {
                lineas.Append('\n');
                continue;
            }

            if (c != '\n' && c != '\t' && char.GetUnicodeCategory(c) == UnicodeCategory.Control)
            {
                continue;
            }

            lineas.Append(c);
        }

        // Paso 4 (los Zs son todos del plano básico: basta con recorrer unidades UTF-16).
        var salida = new StringBuilder(lineas.Length);
        var i2 = 0;
        while (i2 < lineas.Length)
        {
            if (char.GetUnicodeCategory(lineas[i2]) != UnicodeCategory.SpaceSeparator)
            {
                salida.Append(lineas[i2++]);
                continue;
            }

            var fin = i2;
            while (fin < lineas.Length && char.GetUnicodeCategory(lineas[fin]) == UnicodeCategory.SpaceSeparator)
            {
                fin++;
            }

            if (fin - i2 >= 2)
            {
                salida.Append(' ');
            }
            else
            {
                salida.Append(lineas[i2]);
            }

            i2 = fin;
        }

        return salida.ToString();
    }

    /// <summary>
    /// Indica si el texto es UTF-16 bien formado: todo sustituto alto va seguido de uno bajo y ningún sustituto bajo
    /// aparece sin su alto. Un texto mal formado es contenido inválido: nunca se repara.
    /// </summary>
    public static bool EsUtf16BienFormado(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);
        for (var i = 0; i < texto.Length; i++)
        {
            var c = texto[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= texto.Length || !char.IsLowSurrogate(texto[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }
}
