using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AsistenteJuridico.Application.Common.Interfaces.AI;

namespace AsistenteJuridico.Application.Common.Indexacion;

/// <summary>
/// Fase 8.2 — Fragmento listo para las fases siguientes (contrato 8.2 §9). Sus campos corresponden uno a uno con
/// documento_fragmentos (8.1), salvo los identificadores y el embedding, que no son responsabilidad de la 8.2.
/// </summary>
public sealed record FragmentoPreparado(
    int Orden, string Texto, UbicacionSegmento Ubicacion, string? RutaSeccion,
    int CaracterInicio, int CaracterFin, int TokensEstimados, string HashFragmento);

public enum EstadoFragmentacion
{
    Fragmentado,
    SinTexto,
    LimiteSuperado
}

/// <summary>
/// Fragmentado: Fragmentos ≥ 1 y FragmentosCalculados = Fragmentos.Count. SinTexto: sin fragmentos ni conteos.
/// LimiteSuperado: sin fragmentos (todo o nada), con FragmentosCalculados y LimiteAplicado.
/// </summary>
public sealed record ResultadoFragmentacion(
    EstadoFragmentacion Estado, IReadOnlyList<FragmentoPreparado> Fragmentos, int? FragmentosCalculados, int? LimiteAplicado);

/// <summary>
/// Fase 8.2 — Fragmentación <c>chunk-v1</c> (contrato 8.2 §8). Función pura y determinista: normaliza cada segmento
/// con <see cref="TextoNormalizador"/>, construye el texto del documento T y lo divide con la prioridad
/// segmento &gt; párrafo &gt; línea &gt; frase &gt; corte duro, con solapamiento, prefijos de sección (DOCX) y de cabecera
/// (XLSX), ubicación, tokens estimados y hash por fragmento. No persiste nada ni llama a ningún proveedor.
/// </summary>
public static class Fragmentador
{
    public const string Version = "chunk-v1";

    public const int TamanoObjetivo = 1500;
    public const int TamanoMaximo = 2000;
    public const int Solapamiento = 200;
    public const int TamanoMinimo = 200;
    public const int PrefijoMaximo = 500;

    public const string PrefijoSeccion = "Sección: ";
    public const string SeparadorRuta = " > ";
    public static readonly string MarcaRutaRecortada = char.ConvertFromUtf32(0x2026) + SeparadorRuta;
    private static readonly string Raya = char.ConvertFromUtf32(0x2013);

    private readonly record struct Segmento(int Inicio, int Fin, TextSegment Origen);

    /// <summary>
    /// Ruta de sección con los títulos de menor a mayor nivel, unidos con " > ". Si supera 500 caracteres se
    /// eliminan los niveles menos profundos y se antepone "… > " hasta que quepa; si ni el más profundo cabe, NULL.
    /// </summary>
    public static string? ComponerRutaSeccion(IReadOnlyList<string> titulos)
    {
        ArgumentNullException.ThrowIfNull(titulos);
        if (titulos.Count == 0)
        {
            return null;
        }

        var completa = string.Join(SeparadorRuta, titulos);
        if (completa.Length <= PrefijoMaximo)
        {
            return completa;
        }

        for (var desde = 1; desde < titulos.Count; desde++)
        {
            var recortada = MarcaRutaRecortada + string.Join(SeparadorRuta, titulos.Skip(desde));
            if (recortada.Length <= PrefijoMaximo)
            {
                return recortada;
            }
        }

        return null;
    }

    public static ResultadoFragmentacion Fragmentar(
        IReadOnlyList<TextSegment> segmentos, int limiteFragmentos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segmentos);
        ArgumentOutOfRangeException.ThrowIfLessThan(limiteFragmentos, 1);
        cancellationToken.ThrowIfCancellationRequested();

        if (segmentos.Count == 0)
        {
            return SinTexto();
        }

        var tipo = ValidarTipo(segmentos);
        var separador = tipo == UbicacionSegmento.Paginas ? "\n\n" : "\n";

        // §8.2: normalización por segmento, descarte de los segmentos sin texto y texto del documento T.
        var conservados = new List<Segmento>();
        var constructor = new StringBuilder();
        foreach (var segmento in segmentos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizado = TextoNormalizador.Normalizar(segmento.Texto);
            if (string.IsNullOrWhiteSpace(normalizado))
            {
                continue;
            }

            if (conservados.Count > 0)
            {
                constructor.Append(separador);
            }

            conservados.Add(new Segmento(constructor.Length, constructor.Length + normalizado.Length, segmento));
            constructor.Append(normalizado);
        }

        if (conservados.Count == 0)
        {
            return SinTexto();
        }

        var t = constructor.ToString();
        var niveles = CalcularNiveles(t, conservados);
        var fragmentos = new List<FragmentoPreparado>();

        foreach (var (primero, ultimo) in Unidades(tipo, conservados))
        {
            FragmentarUnidad(t, niveles, tipo, conservados, primero, ultimo, fragmentos, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return fragmentos.Count > limiteFragmentos
            ? new ResultadoFragmentacion(EstadoFragmentacion.LimiteSuperado, [], fragmentos.Count, limiteFragmentos)
            : new ResultadoFragmentacion(EstadoFragmentacion.Fragmentado, fragmentos, fragmentos.Count, null);
    }

    private static ResultadoFragmentacion SinTexto() => new(EstadoFragmentacion.SinTexto, [], null, null);

    private static string ValidarTipo(IReadOnlyList<TextSegment> segmentos)
    {
        string? tipo = null;
        foreach (var segmento in segmentos)
        {
            if (segmento?.Ubicacion is null || segmento.Texto is null)
            {
                throw new ArgumentException("Segmento sin texto o sin ubicación.", nameof(segmentos));
            }

            if (segmento.Ubicacion.Tipo is not (UbicacionSegmento.Paginas or UbicacionSegmento.Parrafos
                or UbicacionSegmento.Hoja or UbicacionSegmento.Lineas))
            {
                throw new ArgumentException("Tipo de ubicación desconocido.", nameof(segmentos));
            }

            if (tipo != null && segmento.Ubicacion.Tipo != tipo)
            {
                throw new ArgumentException("Los segmentos de un documento deben tener el mismo tipo de ubicación.", nameof(segmentos));
            }

            if (segmento.RutaSeccion is { Length: > PrefijoMaximo })
            {
                throw new ArgumentException("La ruta de sección supera 500 caracteres.", nameof(segmentos));
            }

            tipo = segmento.Ubicacion.Tipo;
        }

        return tipo!;
    }

    /// <summary>
    /// Nivel de corte de cada posición p (corte inmediatamente después de un separador): 1 límite de segmento,
    /// 2 "\n\n", 3 "\n", 4 ". " / "; " / ": ". 0 si no es punto de corte.
    /// </summary>
    private static byte[] CalcularNiveles(string t, List<Segmento> conservados)
    {
        var niveles = new byte[t.Length + 1];
        for (var k = 1; k < conservados.Count; k++)
        {
            niveles[conservados[k].Inicio] = 1;
        }

        for (var p = 1; p <= t.Length; p++)
        {
            if (niveles[p] != 0)
            {
                continue;
            }

            var anterior = t[p - 1];
            if (anterior == '\n')
            {
                niveles[p] = (byte)(p >= 2 && t[p - 2] == '\n' ? 2 : 3);
            }
            else if (anterior == ' ' && p >= 2 && t[p - 2] is '.' or ';' or ':')
            {
                niveles[p] = 4;
            }
        }

        return niveles;
    }

    /// <summary>XLSX: cada hoja es una unidad independiente. Resto de formatos: el documento entero.</summary>
    private static IEnumerable<(int Primero, int Ultimo)> Unidades(string tipo, List<Segmento> conservados)
    {
        if (tipo != UbicacionSegmento.Hoja)
        {
            yield return (0, conservados.Count - 1);
            yield break;
        }

        var inicio = 0;
        for (var k = 1; k <= conservados.Count; k++)
        {
            if (k == conservados.Count || conservados[k].Origen.Ubicacion.Etiqueta != conservados[inicio].Origen.Ubicacion.Etiqueta)
            {
                yield return (inicio, k - 1);
                inicio = k;
            }
        }
    }

    private static void FragmentarUnidad(
        string t, byte[] niveles, string tipo, List<Segmento> conservados, int primero, int ultimo,
        List<FragmentoPreparado> fragmentos, CancellationToken cancellationToken)
    {
        // Entrada efectiva: sin el espacio en blanco inicial y final de la unidad.
        var inicioUnidad = conservados[primero].Inicio;
        while (char.IsWhiteSpace(t[inicioUnidad]))
        {
            inicioUnidad++;
        }

        var finUnidad = conservados[ultimo].Fin;
        while (char.IsWhiteSpace(t[finUnidad - 1]))
        {
            finUnidad--;
        }

        // XLSX: la cabecera (primera fila con valor) se repite desde el 2.º fragmento de la hoja si cabe en 500.
        string? cabecera = null;
        if (tipo == UbicacionSegmento.Hoja)
        {
            var fila = t[conservados[primero].Inicio..conservados[primero].Fin];
            if (fila.Length + 1 <= PrefijoMaximo)
            {
                cabecera = fila + "\n";
            }
        }

        var inicioNuevo = inicioUnidad;
        var o = inicioNuevo;
        var emitidosEnUnidad = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? ruta = null;
            var prefijo = string.Empty;
            if (tipo == UbicacionSegmento.Parrafos)
            {
                ruta = conservados[SegmentoEn(conservados, inicioNuevo)].Origen.RutaSeccion;
                if (ruta != null)
                {
                    prefijo = PrefijoSeccion + ruta + "\n";
                }
            }
            else if (cabecera != null && emitidosEnUnidad > 0)
            {
                prefijo = cabecera;
            }

            var presupuesto = TamanoMaximo - prefijo.Length;
            var objetivo = Math.Min(TamanoObjetivo, presupuesto);

            int e;
            if (finUnidad - o <= presupuesto)
            {
                e = finUnidad;
            }
            else
            {
                e = UltimoCorte(niveles, o + TamanoMinimo + 1, o + objetivo);
                if (e < 0)
                {
                    e = PrimerCorte(niveles, o + objetivo + 1, o + presupuesto);
                }

                if (e < 0)
                {
                    e = CorteDuro(t, o + objetivo, inicioNuevo);
                }
            }

            // Paso 5: una parte nueva solo de espacio en blanco no genera fragmento (ni solapamiento posterior).
            if (EsBlanco(t, inicioNuevo, e))
            {
                inicioNuevo = e;
                while (char.IsWhiteSpace(t[inicioNuevo]))
                {
                    inicioNuevo++;
                }

                o = inicioNuevo;
                continue;
            }

            var texto = prefijo + t[o..e];
            fragmentos.Add(new FragmentoPreparado(
                fragmentos.Count, texto, Ubicar(t, tipo, conservados, o, e), ruta, o, e,
                Math.Max(1, (texto.Length + 3) / 4), Hash(texto)));
            emitidosEnUnidad++;

            if (e == finUnidad)
            {
                return;
            }

            var oAnterior = o;
            inicioNuevo = e;
            o = InicioSolapamiento(t, niveles, e, oAnterior);
        }
    }

    /// <summary>El corte de mayor nivel y, dentro de él, el más lejano en (desde - 1, hasta].</summary>
    private static int UltimoCorte(byte[] niveles, int desde, int hasta)
    {
        for (byte nivel = 1; nivel <= 4; nivel++)
        {
            for (var p = hasta; p >= desde; p--)
            {
                if (niveles[p] == nivel)
                {
                    return p;
                }
            }
        }

        return -1;
    }

    /// <summary>El corte de mayor nivel y, dentro de él, el más cercano al objetivo en [desde, hasta].</summary>
    private static int PrimerCorte(byte[] niveles, int desde, int hasta)
    {
        for (byte nivel = 1; nivel <= 4; nivel++)
        {
            for (var p = desde; p <= hasta; p++)
            {
                if (niveles[p] == nivel)
                {
                    return p;
                }
            }
        }

        return -1;
    }

    /// <summary>Corte duro en <paramref name="posicion"/>, retrocediendo para no partir un par sustituto ni dejar un carácter combinante al inicio del siguiente.</summary>
    private static int CorteDuro(string t, int posicion, int minimo)
    {
        var e = posicion;
        while (e > minimo + 1 && NoSePuedeCortar(t, e))
        {
            e--;
        }

        if (e > minimo + 1)
        {
            return e;
        }

        // Caso degenerado (solo marcas combinantes): al menos no se parte un par sustituto.
        e = posicion;
        while (e > minimo + 1 && char.IsLowSurrogate(t[e]) && char.IsHighSurrogate(t[e - 1]))
        {
            e--;
        }

        return e;
    }

    private static bool NoSePuedeCortar(string t, int e) =>
        e < t.Length && ((char.IsLowSurrogate(t[e]) && char.IsHighSurrogate(t[e - 1])) || EsCombinante(t, e));

    private static bool EsCombinante(string t, int i) => CharUnicodeInfo.GetUnicodeCategory(t, i)
        is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>Primer punto de corte (nivel 1–4) de la ventana [máx(e − 200, oAnterior + 1), e); si no hay, el inicio de la ventana ajustado hacia delante.</summary>
    private static int InicioSolapamiento(string t, byte[] niveles, int e, int oAnterior)
    {
        var desde = Math.Max(e - Solapamiento, oAnterior + 1);
        for (var p = desde; p < e; p++)
        {
            if (niveles[p] != 0)
            {
                return p;
            }
        }

        var o = desde;
        while (o < e && ((char.IsLowSurrogate(t[o]) && char.IsHighSurrogate(t[o - 1])) || EsCombinante(t, o)))
        {
            o++;
        }

        return o;
    }

    private static bool EsBlanco(string t, int desde, int hasta)
    {
        for (var i = desde; i < hasta; i++)
        {
            if (!char.IsWhiteSpace(t[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Índice del último segmento conservado cuyo inicio es ≤ posición.</summary>
    private static int SegmentoEn(List<Segmento> conservados, int posicion)
    {
        int bajo = 0, alto = conservados.Count - 1;
        while (bajo < alto)
        {
            var medio = (bajo + alto + 1) / 2;
            if (conservados[medio].Inicio <= posicion)
            {
                bajo = medio;
            }
            else
            {
                alto = medio - 1;
            }
        }

        return bajo;
    }

    private static UbicacionSegmento Ubicar(string t, string tipo, List<Segmento> conservados, int o, int e)
    {
        if (tipo == UbicacionSegmento.Lineas)
        {
            var desdeLinea = 1 + ContarSaltos(t, 0, o);
            var hastaLinea = desdeLinea + ContarSaltos(t, o, e - 1);
            return new UbicacionSegmento(tipo, desdeLinea, hastaLinea, Etiqueta("línea", "líneas", desdeLinea, hastaLinea));
        }

        // Primer segmento que toca el rango (fin > o) y último (inicio < e).
        var primero = SegmentoEn(conservados, o);
        if (conservados[primero].Fin <= o)
        {
            primero++;
        }

        var ultimo = SegmentoEn(conservados, e - 1);
        var desde = conservados[primero].Origen.Ubicacion.Desde;
        var hasta = conservados[ultimo].Origen.Ubicacion.Hasta;
        var etiqueta = tipo switch
        {
            UbicacionSegmento.Paginas => Etiqueta("página", "páginas", desde, hasta),
            UbicacionSegmento.Parrafos => Etiqueta("párrafo", "párrafos", desde, hasta),
            _ => conservados[primero].Origen.Ubicacion.Etiqueta + ", " + Etiqueta("fila", "filas", desde, hasta)
        };

        return new UbicacionSegmento(tipo, desde, hasta, etiqueta);
    }

    private static string Etiqueta(string singular, string plural, int desde, int hasta) =>
        desde == hasta
            ? string.Create(CultureInfo.InvariantCulture, $"{singular} {desde}")
            : string.Create(CultureInfo.InvariantCulture, $"{plural} {desde}{Raya}{hasta}");

    private static int ContarSaltos(string t, int desde, int hasta)
    {
        var n = 0;
        for (var i = desde; i < hasta; i++)
        {
            if (t[i] == '\n')
            {
                n++;
            }
        }

        return n;
    }

    private static string Hash(string texto) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(texto)));
}
