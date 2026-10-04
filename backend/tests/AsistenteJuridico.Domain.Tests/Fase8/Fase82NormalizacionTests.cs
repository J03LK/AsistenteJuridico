using System.Globalization;
using AsistenteJuridico.Application.Common.Indexacion;
using static AsistenteJuridico.Domain.Tests.Fase8.Fase82Archivos;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>Fase 8.2 — norm-v1 (contrato 8.2 §7; pruebas T1–T8).</summary>
public class Fase82NormalizacionTests
{
    private static string N(string texto) => TextoNormalizador.Normalizar(texto);

    /// <summary>Unidad UTF-16 suelta (permite sustitutos aislados, que ConvertFromUtf32 rechaza).</summary>
    private static string U(int unidad) => ((char)unidad).ToString();

    // T1
    [Fact]
    public void Nfc_ComponeLosCaracteresDescompuestos_YNoCambiaElTextoYaNormalizado()
    {
        Assert.Equal("Café", N("Cafe" + C(0x301)));
        Assert.Equal("Ñandú", N("N" + C(0x303) + "andu" + C(0x301)));
        Assert.Equal("Señor juez: artículo 1.500", N("Señor juez: artículo 1.500"));
    }

    // T2
    [Fact]
    public void ConservaTildesMayusculasNumerosDeArticuloYPuntuacion()
    {
        const string texto = "ARTÍCULO 1.453.- El Código Civil (Ecuador) dice: «Obligación»; ¿Cómo? ¡Sí! 50 % — art. 2.214-A, ñ, Ü.";
        Assert.Equal(texto, N(texto));
    }

    // T3 (A4)
    [Fact]
    public void SaltosDeLinea_CrLfCrNelU2028U2029_PasanANuevaLinea()
    {
        Assert.Equal("a\nb\nc\nd\ne\nf", N("a\r\nb\rc" + C(0x85) + "d" + C(0x2028) + "e" + C(0x2029) + "f"));
        Assert.Equal("a\n\nb", N("a\r\n\r\nb"));
        Assert.Equal("a\n\nb", N("a\r\rb"));   // dos \r aislados: dos saltos
    }

    // T4
    [Fact]
    public void Controles_SeEliminanSalvoNuevaLineaYTabulador_YLosDeFormatoSeConservan()
    {
        Assert.Equal("ab\tc\nd", N("a" + C(0x01) + "b\tc\n" + C(0x7F) + "d" + C(0x9F)));
        foreach (var formato in new[] { 0xAD, 0x200B, 0xFEFF })
        {
            Assert.Equal("x" + C(formato) + "y", N("x" + C(formato) + "y"));
        }
    }

    // T5 (A5)
    [Fact]
    public void Espacios_DosOMasZsSeColapsan_ElAisladoYTabuladoresYSaltosSeConservan()
    {
        Assert.Equal("a b", N("a   b"));
        Assert.Equal("a b", N("a" + C(0xA0) + C(0xA0) + "b"));
        Assert.Equal("a b", N("a " + C(0x2003) + C(0x3000) + "b"));
        Assert.Equal("a" + C(0xA0) + "b", N("a" + C(0xA0) + "b"));
        Assert.Equal("a\t\tb", N("a\t\tb"));
        Assert.Equal("a\n\n\nb", N("a\n\n\nb"));
        Assert.Equal(" a ", N(" a "));   // no recorta
    }

    // T6
    [Fact]
    public void ControlEntreEspacios_SeEliminaAntesDeColapsar()
    {
        Assert.Equal("a b", N("a " + C(0x01) + " b"));
    }

    // T7
    [Theory]
    [InlineData("es-EC")]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    public void DeterministaIdempotenteEIndependienteDeLaCultura_YNuncaMasLarga(string cultura)
    {
        var entradas = new[]
        {
            TextoBomCrlf, TextoUnicode, "İstanbul ISTANBUL ıi", "  \r\n\t" + C(0x01), "", "Texto normal.",
            "a" + C(0x1F642) + "b" + C(0x1F468) + C(0x200D) + C(0x1F469)
        };
        var referencia = entradas.Select(N).ToList();

        var anterior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(cultura);
            for (var i = 0; i < entradas.Length; i++)
            {
                var salida = N(entradas[i]);
                Assert.Equal(referencia[i], salida);
                Assert.Equal(salida, N(salida));                         // idempotencia
                Assert.True(salida.Length <= entradas[i].Length);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = anterior;
        }

        Assert.Equal("İstanbul ISTANBUL ıi", referencia[2]);   // sin cambios de mayúsculas sensibles a la cultura
    }

    [Fact]
    public void TextoVacioOSoloBlancos_SalidaVaciaOSoloBlancos()
    {
        Assert.Equal(string.Empty, N(string.Empty));
        foreach (var entrada in new[] { "   ", "\t\n", C(0x01) + C(0x02), " " + C(0x85) + " ", C(0xA0) })
        {
            Assert.True(string.IsNullOrWhiteSpace(N(entrada)));
        }
    }

    // Corrección de la desviación #1: UTF-16 mal formado es contenido inválido; norm-v1 nunca lo repara.

    [Theory]
    [InlineData(0xD800, "a", "b")]      // sustituto alto aislado en medio
    [InlineData(0xDBFF, "texto", "")]   // sustituto alto al final
    [InlineData(0xDC00, "", "x")]       // sustituto bajo aislado al inicio
    [InlineData(0xDFFF, "a", "b")]      // sustituto bajo aislado en medio
    public void SustitutoAislado_SeRechaza_SinReparar(int sustituto, string antes, string despues)
    {
        var texto = antes + U(sustituto) + despues;

        Assert.False(TextoNormalizador.EsUtf16BienFormado(texto));
        var error = Assert.Throws<ArgumentException>(() => N(texto));
        Assert.Contains("UTF-16 mal formado", error.Message);
    }

    [Fact]
    public void ParesInvertidosODuplicados_SeRechazan()
    {
        Assert.Throws<ArgumentException>(() => N(U(0xDC00) + U(0xD800)));             // orden invertido
        Assert.Throws<ArgumentException>(() => N(U(0xD800) + U(0xD800) + U(0xDC00)));  // alto sin bajo seguido de un par
        Assert.Throws<ArgumentException>(() => N(C(0x1F642) + U(0xDC00)));              // par válido y bajo suelto
    }

    [Fact]
    public void ParValidoYEmojis_ContinuanNormalmente_SinIntroducirU_FFFD()
    {
        var textos = new[]
        {
            C(0x1F642),
            "Acuerdo firmado " + C(0x1F91D) + " por " + C(0x1F468) + C(0x200D) + C(0x2696) + C(0xFE0F) + " el abogado",
            "Música " + C(0x1D11E) + " y " + C(0x20BB7) + " (plano astral)",
            TextoUnicode,
        };

        foreach (var texto in textos)
        {
            Assert.True(TextoNormalizador.EsUtf16BienFormado(texto));
            var salida = N(texto);
            Assert.True(TextoNormalizador.EsUtf16BienFormado(salida));
            Assert.DoesNotContain(C(0xFFFD), salida);
            Assert.Equal(texto.Normalize(System.Text.NormalizationForm.FormC), salida);   // sin espacios repetidos ni controles: solo NFC
        }

        Assert.Equal("a" + C(0x1F642) + "b", N("a" + C(0x1F642) + "b"));
    }

    [Fact]
    public void U_FFFD_PresenteEnElContenido_SeConservaTalCual_SinAnadirOtros()
    {
        var texto = "dato " + C(0xFFFD) + " ilegible del original";
        Assert.Equal(texto, N(texto));
        Assert.Single(N(texto), c => c == (char)0xFFFD);
    }

    [Fact]
    public void Fragmentador_SegmentoConSustitutoAislado_ArgumentException_SinFragmentos()
    {
        var segmentos = new[] { Fase82Fragmentos.Txt("válido"), Fase82Fragmentos.Txt("roto " + U(0xD800)) };
        Assert.Throws<ArgumentException>(() => Fragmentador.Fragmentar(segmentos, 10));
    }

    // T8
    [Fact]
    public void NoAnonimiza_NombresCedulasYRucIntactos()
    {
        const string texto = "Comparecen Juan Pérez Andrade (cédula 1712345678) y Constructora Andina S.A. (RUC 1790012345001), "
            + "representada por la Dra. María José López.";
        Assert.Equal(texto, N(texto));
    }
}
