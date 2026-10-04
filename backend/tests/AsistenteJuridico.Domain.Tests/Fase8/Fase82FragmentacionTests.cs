using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AsistenteJuridico.Application.Common.Indexacion;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using static AsistenteJuridico.Domain.Tests.Fase8.Fase82Archivos;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>Segmentos de prueba y verificación de las propiedades de chunk-v1 (contrato 8.2 §8 y §10).</summary>
internal static class Fase82Fragmentos
{
    public static TextSegment Txt(string texto) =>
        new(texto, new UbicacionSegmento(UbicacionSegmento.Lineas, 1, texto.Count(c => c == '\n') + 1, "líneas"), null);

    public static TextSegment Pagina(int numero, string texto) =>
        new(texto, new UbicacionSegmento(UbicacionSegmento.Paginas, numero, numero, $"página {numero}"), null);

    public static TextSegment Parrafo(int numero, string texto, string? ruta = null) =>
        new(texto, new UbicacionSegmento(UbicacionSegmento.Parrafos, numero, numero, $"párrafo {numero}"), ruta);

    public static TextSegment Fila(string hoja, int numero, string texto) =>
        new(texto, new UbicacionSegmento(UbicacionSegmento.Hoja, numero, numero, "hoja " + hoja), null);

    /// <summary>Prosa jurídica determinista con frases, saltos de línea y párrafos.</summary>
    public static string Prosa(int frases) => string.Concat(Enumerable.Range(1, frases).Select(i =>
        $"Cláusula {i}: el arrendatario se obliga a pagar el canon número {i * 7} en el plazo pactado; "
        + (i % 9 == 0 ? "\n\n" : i % 4 == 0 ? "\n" : string.Empty)));

    public static ResultadoFragmentacion Fragmentar(params TextSegment[] segmentos) => Fragmentador.Fragmentar(segmentos, 100_000);

    /// <summary>T del contrato 8.2 §8.2, reconstruido de forma independiente al fragmentador.</summary>
    public static string ConstruirT(IReadOnlyList<TextSegment> segmentos)
    {
        var separador = segmentos[0].Ubicacion.Tipo == UbicacionSegmento.Paginas ? "\n\n" : "\n";
        return string.Join(separador, segmentos.Select(s => TextoNormalizador.Normalizar(s.Texto)).Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>Propiedades de §8.4, §8.8 y §10 (T22) que debe cumplir toda fragmentación.</summary>
    public static void VerificarPropiedades(IReadOnlyList<TextSegment> segmentos, ResultadoFragmentacion resultado)
    {
        Assert.Equal(EstadoFragmentacion.Fragmentado, resultado.Estado);
        var t = ConstruirT(segmentos);
        var f = resultado.Fragmentos;
        Assert.Equal(f.Count, resultado.FragmentosCalculados);
        Assert.Null(resultado.LimiteAplicado);

        var cubiertos = new bool[t.Length];
        for (var i = 0; i < f.Count; i++)
        {
            var x = f[i];
            Assert.Equal(i, x.Orden);
            Assert.True(x.Texto.Length <= Fragmentador.TamanoMaximo, $"Fragmento {i}: {x.Texto.Length} caracteres");
            Assert.True(x.CaracterInicio >= 0 && x.CaracterFin > x.CaracterInicio);
            if (i > 0)
            {
                Assert.True(x.CaracterInicio > f[i - 1].CaracterInicio);
                Assert.True(f[i - 1].CaracterFin - x.CaracterInicio <= Fragmentador.Solapamiento);
            }

            // Reconstrucción: Texto = prefijo + T[inicio, fin); prefijo vacío, de sección o de cabecera.
            var rango = t[x.CaracterInicio..x.CaracterFin];
            Assert.EndsWith(rango, x.Texto, StringComparison.Ordinal);
            var prefijo = x.Texto[..^rango.Length];
            Assert.True(prefijo.Length == 0 || prefijo.EndsWith('\n'));
            Assert.True(prefijo.Length <= Fragmentador.PrefijoMaximo + Fragmentador.PrefijoSeccion.Length + 1);
            if (x.RutaSeccion != null)
            {
                Assert.Equal(Fragmentador.PrefijoSeccion + x.RutaSeccion + "\n", prefijo);
            }

            Assert.Equal(Math.Max(1, (int)Math.Ceiling(x.Texto.Length / 4.0)), x.TokensEstimados);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(x.Texto))), x.HashFragmento);
            Assert.Matches("^[0-9a-f]{64}$", x.HashFragmento);

            // Nunca se parte un par sustituto ni se deja una marca combinante al inicio.
            Assert.False(char.IsLowSurrogate(rango[0]));
            Assert.False(char.IsHighSurrogate(rango[^1]));
            Assert.False(CharUnicodeInfo.GetUnicodeCategory(rango, 0) is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark);

            for (var p = x.CaracterInicio; p < x.CaracterFin; p++)
            {
                cubiertos[p] = true;
            }
        }

        // Cobertura: todo carácter no blanco de T está en algún fragmento.
        for (var p = 0; p < t.Length; p++)
        {
            Assert.True(cubiertos[p] || char.IsWhiteSpace(t[p]), $"Carácter {p} sin cubrir");
        }
    }

    /// <summary>Huella determinista de un resultado (todos los campos de todos los fragmentos).</summary>
    public static string Huella(ResultadoFragmentacion resultado) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{resultado.Estado}|{resultado.FragmentosCalculados}|{resultado.LimiteAplicado}\n" + string.Concat(resultado.Fragmentos.Select(x =>
            $"{x.Orden}|{x.CaracterInicio}|{x.CaracterFin}|{x.Ubicacion.Tipo}|{x.Ubicacion.Desde}|{x.Ubicacion.Hasta}|{x.Ubicacion.Etiqueta}|"
            + $"{x.RutaSeccion}|{x.TokensEstimados}|{x.HashFragmento}\n")))));
}

/// <summary>Fase 8.2 — chunk-v1 (contrato 8.2 §8 y §9; pruebas T9–T27 y T42).</summary>
public class Fase82FragmentacionTests
{
    private static readonly string Raya = C(0x2013);

    private static ResultadoFragmentacion F(params TextSegment[] s) => Fase82Fragmentos.Fragmentar(s);
    private static TextSegment Txt(string t) => Fase82Fragmentos.Txt(t);

    // T9
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \n\t \r\n ")]
    public void EntradaVaciaOSoloBlancos_SinTexto(string texto)
    {
        var r = F(Txt(texto));
        Assert.Equal(EstadoFragmentacion.SinTexto, r.Estado);
        Assert.Empty(r.Fragmentos);
        Assert.Null(r.FragmentosCalculados);
        Assert.Null(r.LimiteAplicado);
    }

    [Fact]
    public void SoloControles_OListaVacia_SinTexto()
    {
        Assert.Equal(EstadoFragmentacion.SinTexto, F(Txt(C(0x01) + C(0x02) + C(0x7F))).Estado);
        Assert.Equal(EstadoFragmentacion.SinTexto, Fragmentador.Fragmentar([], 2000).Estado);
        Assert.Equal(EstadoFragmentacion.SinTexto, F(Fase82Fragmentos.Pagina(1, " "), Fase82Fragmentos.Pagina(2, "\n")).Estado);
    }

    // T10 y T11 (A1)
    [Theory]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(1500)]
    [InlineData(1501)]
    [InlineData(1800)]
    [InlineData(2000)]
    public void HastaDosMilCaracteres_UnSoloFragmento(int longitud)
    {
        var texto = Fase82Fragmentos.Prosa(40)[..longitud].TrimEnd() is var p && p.Length == longitud ? p : new string('a', longitud);
        var r = F(Txt(texto));

        var unico = Assert.Single(r.Fragmentos);
        Assert.Equal(texto, unico.Texto);
        Assert.Equal(0, unico.CaracterInicio);
        Assert.Equal(longitud, unico.CaracterFin);
        Assert.Equal(new UbicacionSegmento(UbicacionSegmento.Lineas, 1, texto.Count(c => c == '\n') + 1, unico.Ubicacion.Etiqueta), unico.Ubicacion);
    }

    // T12
    [Theory]
    [InlineData(2001)]
    [InlineData(3500)]
    [InlineData(20_000)]
    public void MasDeDosMil_VariosFragmentosDeComoMaximoDosMilConSolapamiento(int longitud)
    {
        var texto = new string('a', 100) + Fase82Fragmentos.Prosa(400);
        texto = texto[..longitud];
        var segmentos = new[] { Txt(texto) };
        var r = Fragmentador.Fragmentar(segmentos, 100_000);

        Assert.True(r.Fragmentos.Count >= 2);
        Fase82Fragmentos.VerificarPropiedades(segmentos, r);
        for (var i = 1; i < r.Fragmentos.Count; i++)
        {
            Assert.True(r.Fragmentos[i].CaracterInicio < r.Fragmentos[i - 1].CaracterFin, "Debe haber solapamiento");
        }
    }

    // T13
    [Fact]
    public void Prioridad_ParrafoSobreLineaYFrase()
    {
        // "\n\n" en 800, "\n" en 1300 y ". " en 1400: gana el párrafo (nivel 2), aunque esté más lejos del objetivo.
        var texto = new string('a', 798) + "\n\n" + new string('b', 498) + "\n" + new string('c', 99) + ". " + new string('d', 1600);
        var r = F(Txt(texto));
        Assert.Equal(800, r.Fragmentos[0].CaracterFin);
    }

    [Fact]
    public void Prioridad_LimiteDeSegmentoSobreParrafo()
    {
        // Página 1 de 600 caracteres; en la página 2 hay "\n\n" más cerca del objetivo: gana el límite de página.
        var r = F(Fase82Fragmentos.Pagina(1, new string('a', 600)),
            Fase82Fragmentos.Pagina(2, new string('b', 300) + "\n\n" + new string('c', 2500)));
        Assert.Equal(602, r.Fragmentos[0].CaracterFin);
        Assert.Equal(new UbicacionSegmento(UbicacionSegmento.Paginas, 1, 1, "página 1"), r.Fragmentos[0].Ubicacion);
    }

    [Fact]
    public void Prioridad_LineaSobreFrase_YFraseSobreCorteDuro()
    {
        var conLinea = new string('a', 900) + ". " + new string('b', 300) + "\n" + new string('c', 2000);
        Assert.Equal(1203, F(Txt(conLinea)).Fragmentos[0].CaracterFin);

        var conFrase = new string('a', 900) + "; " + new string('b', 2500);
        Assert.Equal(902, F(Txt(conFrase)).Fragmentos[0].CaracterFin);
    }

    // T14
    [Fact]
    public void SinCortesEnLaVentanaObjetivo_SeUsaElMasCercanoHastaElMaximo()
    {
        var texto = new string('a', 1700) + ". " + new string('b', 400) + ": " + new string('c', 1000);
        var r = F(Txt(texto));
        Assert.Equal(1702, r.Fragmentos[0].CaracterFin);
    }

    // T15
    [Fact]
    public void SinSeparadores_CortesDurosDelObjetivo()
    {
        var segmentos = new[] { Txt(new string('x', 5000)) };
        var r = Fragmentador.Fragmentar(segmentos, 100);

        Assert.Equal(1500, r.Fragmentos[0].CaracterFin);
        Assert.Equal(1300, r.Fragmentos[1].CaracterInicio);   // solapamiento sin frase: exactamente 200
        Assert.Equal(2800, r.Fragmentos[1].CaracterFin);
        Fase82Fragmentos.VerificarPropiedades(segmentos, r);
    }

    [Fact]
    public void CorteDuro_NoParteParesSustitutosNiSeparaMarcasCombinantes()
    {
        var emojis = "a" + string.Concat(Enumerable.Repeat(C(0x1F642), 2500));       // la posición 1500 cae en un sustituto bajo
        var combinantes = "a" + string.Concat(Enumerable.Repeat("q" + C(0x301) + C(0x302), 1700));   // "q" no tiene forma precompuesta: NFC no la compone

        foreach (var texto in new[] { emojis, combinantes })
        {
            var segmentos = new[] { Txt(texto) };
            var r = Fragmentador.Fragmentar(segmentos, 100);
            Assert.True(r.Fragmentos.Count >= 2);
            Assert.True(r.Fragmentos[0].CaracterFin < 1500);
            Fase82Fragmentos.VerificarPropiedades(segmentos, r);
        }
    }

    // T16
    [Fact]
    public void Solapamiento_AlineadoAlPrimerInicioDeFraseDeLaVentana()
    {
        var texto = new string('a', 1400) + ". " + new string('b', 48) + ". " + new string('c', 1000);
        var r = F(Txt(texto));

        Assert.Equal(1452, r.Fragmentos[0].CaracterFin);
        Assert.Equal(1402, r.Fragmentos[1].CaracterInicio);
        Assert.StartsWith("bbbb", r.Fragmentos[1].Texto);
        Assert.Equal(2452, r.Fragmentos[1].CaracterFin);
    }

    // T17
    [Fact]
    public void FragmentoFinalCorto_SeConservaSoloSiNoCabeEnElAnterior()
    {
        var texto = new string('a', 1990) + ". " + new string('b', 58);
        var segmentos = new[] { Txt(texto) };
        var r = Fragmentador.Fragmentar(segmentos, 100);

        Assert.Equal(2, r.Fragmentos.Count);
        var (anterior, ultimo) = (r.Fragmentos[0], r.Fragmentos[1]);
        Assert.Equal(1992, anterior.CaracterFin);
        Assert.True(ultimo.CaracterFin - anterior.CaracterFin < Fragmentador.TamanoMinimo);
        Assert.True(ultimo.CaracterFin - anterior.CaracterInicio > Fragmentador.TamanoMaximo);   // unirlo superaría el máximo
        Fase82Fragmentos.VerificarPropiedades(segmentos, r);
    }

    [Fact]
    public void FragmentoFinalCorto_EnDocumentosLargos_NuncaPodiaUnirse()
    {
        foreach (var frases in new[] { 30, 55, 120, 333 })
        {
            var segmentos = new[] { Txt(Fase82Fragmentos.Prosa(frases)) };
            var f = Fragmentador.Fragmentar(segmentos, 100_000).Fragmentos;
            if (f.Count >= 2 && f[^1].CaracterFin - f[^2].CaracterFin < Fragmentador.TamanoMinimo)
            {
                Assert.True(f[^1].CaracterFin - f[^2].CaracterInicio > Fragmentador.TamanoMaximo);
            }
        }
    }

    // T18
    [Fact]
    public void TramoIntermedioSoloDeSaltos_NoGeneraFragmentosVacios()
    {
        var texto = "Inicio del documento." + new string('\n', 6000) + "Fin del documento.";
        var r = F(Txt(texto));

        Assert.Equal(2, r.Fragmentos.Count);
        Assert.StartsWith("Inicio del documento.", r.Fragmentos[0].Texto);
        Assert.Equal("Fin del documento.", r.Fragmentos[1].Texto);
        Assert.All(r.Fragmentos, x => Assert.False(string.IsNullOrWhiteSpace(x.Texto)));
    }

    // T19 (A6)
    [Fact]
    public void Docx_RutaDeSeccionPrefijoYCambioDeSeccion()
    {
        var segmentos = new[]
        {
            Fase82Fragmentos.Parrafo(1, "CONTRATO", "CONTRATO"),
            Fase82Fragmentos.Parrafo(2, Fase82Fragmentos.Prosa(20), "CONTRATO"),
            Fase82Fragmentos.Parrafo(3, "Cláusulas", "CONTRATO > Cláusulas"),
            Fase82Fragmentos.Parrafo(4, Fase82Fragmentos.Prosa(30), "CONTRATO > Cláusulas"),
        };
        var r = Fragmentador.Fragmentar(segmentos, 100);

        Fase82Fragmentos.VerificarPropiedades(segmentos, r);
        Assert.StartsWith("Sección: CONTRATO\nCONTRATO\n", r.Fragmentos[0].Texto);
        Assert.Equal("CONTRATO", r.Fragmentos[0].RutaSeccion);
        Assert.Contains(r.Fragmentos, x => x.RutaSeccion == "CONTRATO > Cláusulas" && x.Texto.StartsWith("Sección: CONTRATO > Cláusulas\n"));
        Assert.All(r.Fragmentos, x => Assert.Equal(UbicacionSegmento.Parrafos, x.Ubicacion.Tipo));
    }

    [Fact]
    public void Docx_RutaLarga_PrefijoCuentaParaElMaximo()
    {
        var ruta = new string('R', 500);
        var segmentos = new[] { Fase82Fragmentos.Parrafo(1, new string('p', 5000), ruta) };
        var r = Fragmentador.Fragmentar(segmentos, 100);

        Fase82Fragmentos.VerificarPropiedades(segmentos, r);
        Assert.All(r.Fragmentos, x => Assert.StartsWith("Sección: " + ruta + "\n", x.Texto));
        // Prefijo de 510: presupuesto del rango 1.490; el corte duro agota exactamente los 2.000 caracteres.
        Assert.Equal(1490, r.Fragmentos[0].CaracterFin);
        Assert.Equal(2000, r.Fragmentos[0].Texto.Length);
    }

    [Fact]
    public void ComponerRutaSeccion_RecortaLosNivelesAltosConMarca_ONull()
    {
        Assert.Null(Fragmentador.ComponerRutaSeccion([]));
        Assert.Equal("A > B > C", Fragmentador.ComponerRutaSeccion(["A", "B", "C"]));

        var largo = Fragmentador.ComponerRutaSeccion([new string('X', 300), new string('Y', 300)]);
        Assert.Equal(C(0x2026) + " > " + new string('Y', 300), largo);
        Assert.True(largo!.Length <= 500);

        Assert.Null(Fragmentador.ComponerRutaSeccion([new string('Z', 600)]));
        Assert.Null(Fragmentador.ComponerRutaSeccion(["A", new string('Z', 499)]));   // "… > " + 499 no cabe
        Assert.Equal(new string('Z', 500), Fragmentador.ComponerRutaSeccion([new string('Z', 500)]));
    }

    // T20 (A3, A6)
    [Fact]
    public void Xlsx_CabeceraRepetida_HojasIndependientes_SinSolapamientoEntreHojas()
    {
        var segmentos = new List<TextSegment> { Fase82Fragmentos.Fila("Cobros", 1, "Cliente\tMonto\tEstado") };
        for (var i = 2; i <= 150; i++)
        {
            segmentos.Add(Fase82Fragmentos.Fila("Cobros", i, $"Cliente número {i}\t{i * 100}\tPendiente de pago"));
        }

        segmentos.Add(Fase82Fragmentos.Fila("Gastos", 1, "Concepto\tValor"));
        segmentos.Add(Fase82Fragmentos.Fila("Gastos", 2, "Peritaje\t300"));

        var r = Fragmentador.Fragmentar(segmentos, 100);
        Fase82Fragmentos.VerificarPropiedades(segmentos, r);

        var cobros = r.Fragmentos.Where(x => x.Ubicacion.Etiqueta.StartsWith("hoja Cobros")).ToList();
        var gastos = r.Fragmentos.Where(x => x.Ubicacion.Etiqueta.StartsWith("hoja Gastos")).ToList();
        Assert.Equal(r.Fragmentos.Count, cobros.Count + gastos.Count);   // ninguna etiqueta mezcla hojas
        Assert.True(cobros.Count >= 3);
        Assert.StartsWith("Cliente\tMonto\tEstado\nCliente número 2", cobros[0].Texto);
        Assert.All(cobros.Skip(1), x => Assert.StartsWith("Cliente\tMonto\tEstado\n", x.Texto));

        var unicoGastos = Assert.Single(gastos);
        Assert.Equal("Concepto\tValor\nPeritaje\t300", unicoGastos.Texto);   // sin solapamiento con la hoja anterior
        Assert.Equal(new UbicacionSegmento(UbicacionSegmento.Hoja, 1, 2, $"hoja Gastos, filas 1{Raya}2"), unicoGastos.Ubicacion);
        Assert.True(unicoGastos.CaracterInicio >= cobros[^1].CaracterFin);
    }

    [Fact]
    public void Xlsx_CabeceraDeMasDe500_NoSeRepite()
    {
        var segmentos = new List<TextSegment> { Fase82Fragmentos.Fila("H", 1, new string('C', 500)) };
        for (var i = 2; i <= 80; i++)
        {
            segmentos.Add(Fase82Fragmentos.Fila("H", i, $"Fila {i}\tdato de prueba con texto suficiente"));
        }

        var r = Fragmentador.Fragmentar(segmentos, 100);
        Fase82Fragmentos.VerificarPropiedades(segmentos, r);
        Assert.True(r.Fragmentos.Count >= 2);
        Assert.All(r.Fragmentos.Skip(1), x => Assert.DoesNotContain(new string('C', 500), x.Texto));
    }

    // T21
    [Fact]
    public void UbicacionYEtiquetas()
    {
        var pdf = F(Fase82Fragmentos.Pagina(3, "Página tres."), Fase82Fragmentos.Pagina(4, "Página cuatro."));
        Assert.Equal(new UbicacionSegmento(UbicacionSegmento.Paginas, 3, 4, $"páginas 3{Raya}4"), Assert.Single(pdf.Fragmentos).Ubicacion);

        var docx = F(Fase82Fragmentos.Parrafo(7, "Único párrafo."));
        Assert.Equal(new UbicacionSegmento(UbicacionSegmento.Parrafos, 7, 7, "párrafo 7"), Assert.Single(docx.Fragmentos).Ubicacion);

        var xlsx = F(Fase82Fragmentos.Fila("Honorarios", 1, "a"), Fase82Fragmentos.Fila("Honorarios", 2, "b"), Fase82Fragmentos.Fila("Honorarios", 5, "c"));
        Assert.Equal(new UbicacionSegmento(UbicacionSegmento.Hoja, 1, 5, $"hoja Honorarios, filas 1{Raya}5"), Assert.Single(xlsx.Fragmentos).Ubicacion);

        var unaFila = F(Fase82Fragmentos.Fila("Datos", 9, "solo"));
        Assert.Equal("hoja Datos, fila 9", Assert.Single(unaFila.Fragmentos).Ubicacion.Etiqueta);

        var txt = F(Txt("uno\ndos\ntres"));
        Assert.Equal(new UbicacionSegmento(UbicacionSegmento.Lineas, 1, 3, $"líneas 1{Raya}3"), Assert.Single(txt.Fragmentos).Ubicacion);
        Assert.Equal("línea 1", Assert.Single(F(Txt("una sola línea")).Fragmentos).Ubicacion.Etiqueta);
    }

    [Fact]
    public void Ubicacion_RangoQueTerminaTrasNuevaLinea_NoCuentaLaLineaSiguiente()
    {
        var lineas = string.Concat(Enumerable.Range(1, 300).Select(i => $"Línea número {i:D3} del escrito\n"));   // 26 caracteres
        var r = F(Txt(lineas));
        var primero = r.Fragmentos[0];
        Assert.EndsWith("\n", primero.Texto);
        Assert.Equal(1, primero.Ubicacion.Desde);
        Assert.Equal(primero.Texto.Count(c => c == '\n'), primero.Ubicacion.Hasta);

        var segundo = r.Fragmentos[1];
        Assert.Equal(1 + lineas[..segundo.CaracterInicio].Count(c => c == '\n'), segundo.Ubicacion.Desde);
    }

    [Fact]
    public void Pdf_FragmentoQueAbarcaPaginas_RangoDePaginas()
    {
        var segmentos = Enumerable.Range(1, 12).Select(n => Fase82Fragmentos.Pagina(n, $"Página {n}. " + Fase82Fragmentos.Prosa(3))).ToArray();
        var r = Fragmentador.Fragmentar(segmentos, 100);
        Fase82Fragmentos.VerificarPropiedades(segmentos, r);
        Assert.Contains(r.Fragmentos, x => x.Ubicacion.Hasta > x.Ubicacion.Desde && x.Ubicacion.Etiqueta.StartsWith("páginas "));
    }

    // T22
    [Fact]
    public void Propiedades_EnDocumentosVariados()
    {
        var casos = new[]
        {
            new[] { Txt(Fase82Fragmentos.Prosa(500)) },
            new[] { Txt(TextoBomCrlf + Fase82Fragmentos.Prosa(60) + TextoUnicode) },
            Enumerable.Range(1, 30).Select(n => Fase82Fragmentos.Pagina(n, n % 5 == 0 ? "" : Fase82Fragmentos.Prosa(n % 7 + 1))).ToArray(),
            Enumerable.Range(1, 60).Select(n => Fase82Fragmentos.Parrafo(n, Fase82Fragmentos.Prosa(n % 4 + 1), n > 30 ? "Segunda parte" : null)).ToArray(),
        };

        foreach (var segmentos in casos)
        {
            Fase82Fragmentos.VerificarPropiedades(segmentos, Fragmentador.Fragmentar(segmentos, 100_000));
        }
    }

    // T23
    [Fact]
    public void TokensEstimadosYHash_VectoresConocidos()
    {
        var abc = Assert.Single(F(Txt("abc")).Fragmentos);
        Assert.Equal(1, abc.TokensEstimados);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", abc.HashFragmento);

        Assert.Equal(2, Assert.Single(F(Txt("abcde")).Fragmentos).TokensEstimados);
        Assert.Equal(500, Assert.Single(F(Txt(new string('a', 2000))).Fragmentos).TokensEstimados);

        // UTF-8 sin BOM: "ñ" son dos bytes.
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([0xC3, 0xB1])), Assert.Single(F(Txt("ñ")).Fragmentos).HashFragmento);
    }

    // T24
    [Fact]
    public void Limite_IgualAlNumeroFragmentado_UnoMenosLimiteSuperadoSinFragmentos()
    {
        var segmentos = new[] { Txt(new string('x', 20_000)) };
        var n = Fragmentador.Fragmentar(segmentos, 100_000).Fragmentos.Count;

        var justo = Fragmentador.Fragmentar(segmentos, n);
        Assert.Equal(EstadoFragmentacion.Fragmentado, justo.Estado);
        Assert.Equal(n, justo.Fragmentos.Count);

        var superado = Fragmentador.Fragmentar(segmentos, n - 1);
        Assert.Equal(EstadoFragmentacion.LimiteSuperado, superado.Estado);
        Assert.Empty(superado.Fragmentos);
        Assert.Equal(n, superado.FragmentosCalculados);
        Assert.Equal(n - 1, superado.LimiteAplicado);
    }

    // T25
    [Theory]
    [InlineData("es-EC")]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    public void Determinismo_MismaEntradaMismoResultado_EnCualquierCultura(string cultura)
    {
        var segmentos = Enumerable.Range(1, 40)
            .Select(n => Fase82Fragmentos.Parrafo(n, "İ ı " + Fase82Fragmentos.Prosa(n % 6 + 1), n % 10 == 0 ? "TÍTULO İ" : null)).ToArray();
        var referencia = Fase82Fragmentos.Huella(Fragmentador.Fragmentar(segmentos, 100_000));

        var anterior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(cultura);
            Assert.Equal(referencia, Fase82Fragmentos.Huella(Fragmentador.Fragmentar(segmentos, 100_000)));
            Assert.Equal(referencia, Fase82Fragmentos.Huella(Fragmentador.Fragmentar(segmentos.ToList(), 100_000)));
        }
        finally
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = anterior;
        }
    }

    // T26
    [Fact]
    public void Cancelacion_AntesYDurante_PropagaSinResultado()
    {
        using var antes = new CancellationTokenSource();
        antes.Cancel();
        Assert.Throws<OperationCanceledException>(() => Fragmentador.Fragmentar([Txt("texto")], 10, antes.Token));

        using var durante = new CancellationTokenSource();
        var segmentos = new ListaQueCancela(
            Enumerable.Range(1, 50).Select(n => Fase82Fragmentos.Pagina(n, Fase82Fragmentos.Prosa(5))).ToList(), durante, cancelarEn: 20);
        Assert.Throws<OperationCanceledException>(() => Fragmentador.Fragmentar(segmentos, 10_000, durante.Token));
    }

    // T27
    [Fact]
    public void EntradasInvalidas_ArgumentException()
    {
        Assert.Throws<ArgumentNullException>(() => Fragmentador.Fragmentar(null!, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fragmentador.Fragmentar([Txt("a")], 0));
        Assert.Throws<ArgumentException>(() => Fragmentador.Fragmentar([Txt("a"), Fase82Fragmentos.Pagina(1, "b")], 10));
        Assert.Throws<ArgumentException>(() => Fragmentador.Fragmentar([new TextSegment("a", new UbicacionSegmento("otro", 1, 1, "x"), null)], 10));
        Assert.Throws<ArgumentException>(() => Fragmentador.Fragmentar([Fase82Fragmentos.Parrafo(1, "a", new string('r', 501))], 10));
    }

    // T42
    [Fact]
    public void FragmentoPreparado_NoExponeIdentificadoresTitulosNiRutas()
    {
        var propiedades = typeof(FragmentoPreparado).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "CaracterFin", "CaracterInicio", "HashFragmento", "Orden", "RutaSeccion", "Texto", "TokensEstimados", "Ubicacion" },
            propiedades);
        Assert.Equal(new[] { "Desde", "Etiqueta", "Hasta", "Tipo" },
            typeof(UbicacionSegmento).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Constantes_DeChunkV1()
    {
        Assert.Equal("chunk-v1", Fragmentador.Version);
        Assert.Equal("norm-v1", TextoNormalizador.Version);
        Assert.Equal("ext-v1", ExtractionProfile.VersionIndexacion);
        Assert.Equal((1500, 2000, 200, 200, 500),
            (Fragmentador.TamanoObjetivo, Fragmentador.TamanoMaximo, Fragmentador.Solapamiento, Fragmentador.TamanoMinimo, Fragmentador.PrefijoMaximo));
    }

    /// <summary>Lista que cancela el token al leer el elemento indicado (cancelación a mitad de la fragmentación).</summary>
    private sealed class ListaQueCancela(List<TextSegment> interna, CancellationTokenSource cts, int cancelarEn) : IReadOnlyList<TextSegment>
    {
        public TextSegment this[int index] => interna[index];
        public int Count => interna.Count;

        public IEnumerator<TextSegment> GetEnumerator()
        {
            for (var i = 0; i < interna.Count; i++)
            {
                if (i == cancelarEn)
                {
                    cts.Cancel();
                }

                yield return interna[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
