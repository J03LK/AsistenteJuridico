using System.Globalization;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Infrastructure.Services.AI;
using static AsistenteJuridico.Domain.Tests.Fase8.Fase82Archivos;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>
/// Fase 8.3 — Proveedor simulado, validación del lote, clasificación de fallos y firma
/// (FASE_8_3_CONTRATO.md §6, §9, §15 y §16; pruebas E1, E3–E10, E25c y E33).
/// </summary>
public class Fase83MockYContratoTests
{
    private readonly MockEmbeddingProvider _mock = new();

    private Task<EmbeddingBatchResult> Embed(params string[] textos) => _mock.EmbedAsync(textos, EmbeddingPurpose.Documento, CancellationToken.None);

    private static double Norma(float[] v) => Math.Sqrt(v.Sum(x => (double)x * x));
    private static double Coseno(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum() / (Norma(a) * Norma(b));

    // E1
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(64)]
    public async Task Lote_UnVectorPorEntrada_De1536ValoresFinitosYNormaUno(int n)
    {
        var r = await Embed(Respuestas83.Textos(n));

        Assert.Equal(n, r.Vectores.Count);
        Assert.Equal(1536, r.Dimensiones);
        Assert.All(r.Vectores, v =>
        {
            Assert.Equal(1536, v.Length);
            Assert.All(v, x => Assert.True(float.IsFinite(x)));
            Assert.InRange(Norma(v), 1 - 1e-6, 1 + 1e-6);
        });
        Assert.Equal(("mock", "mock-bow-sha256-v1", "mock-bow-sha256-v1"), (r.ProviderId, r.ModelId, r.ModeloDeclarado));
        Assert.Equal(Respuestas83.Textos(n).Sum(t => Math.Max(1, (int)Math.Ceiling(t.Length / 4.0))), r.TokensEntrada);
    }

    // E3
    [Fact]
    public async Task Duplicados_UnaPosicionPorEntrada_ConVectoresIguales()
    {
        var r = await Embed("mismo texto", "otro texto", "mismo texto");
        Assert.Equal(3, r.Vectores.Count);
        Assert.Equal(r.Vectores[0], r.Vectores[2]);
        Assert.NotEqual(r.Vectores[0], r.Vectores[1]);
    }

    // E4
    [Fact]
    public async Task LoteInvalido_ArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _mock.EmbedAsync(null!, EmbeddingPurpose.Documento, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => Embed());
        await Assert.ThrowsAsync<ArgumentException>(() => Embed(Respuestas83.Textos(65)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Embed("a", null!));
        await Assert.ThrowsAsync<ArgumentException>(() => Embed("a", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => Embed("a", " \n\t "));
        await Assert.ThrowsAsync<ArgumentException>(() => Embed("roto " + ((char)0xD800)));
        await Assert.ThrowsAsync<ArgumentException>(() => Embed(new string('a', 8191 * 4 + 1)));
        Assert.Single((await Embed(new string('a', 8191 * 4))).Vectores);   // exactamente el máximo: válido
    }

    // E5
    [Fact]
    public async Task Determinismo_DosEjecucionesEInstanciasDistintas_BitABit()
    {
        var textos = new[] { "Contrato de arrendamiento", TextoUnicode, "ARTÍCULO 1.453 del Código Civil" };
        var a = await Embed(textos);
        var b = await Embed(textos);
        var c = await new MockEmbeddingProvider().EmbedAsync(textos, EmbeddingPurpose.Consulta, CancellationToken.None);

        for (var i = 0; i < textos.Length; i++)
        {
            Assert.Equal(a.Vectores[i], b.Vectores[i]);
            Assert.Equal(a.Vectores[i], c.Vectores[i]);   // otra instancia y otro propósito: mismo vector
        }
    }

    // E6
    [Theory]
    [InlineData("es-EC")]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    public async Task IndependienteDeLaCultura(string cultura)
    {
        var textos = new[] { "İstanbul ISTANBUL ıi TÍTULO", "Cláusula QUINTA" };
        var referencia = await Embed(textos);

        var anterior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(cultura);
            var r = await Embed(textos);
            Assert.Equal(referencia.Vectores[0], r.Vectores[0]);
            Assert.Equal(referencia.Vectores[1], r.Vectores[1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = anterior;
        }
    }

    // E7
    [Fact]
    public async Task UnicodeTildesEmojisSignosYTextoLargo_SinExcepcion_NormaUno()
    {
        var textos = new[]
        {
            "Señor juez: año 2026, cédula 1712345678", C(0x1F642) + C(0x1F91D), "¿?¡!... --- ;;;", C(0x1F468) + C(0x200D) + C(0x2696) + C(0xFE0F),
            new string('x', 2000), Fase82Fragmentos.Prosa(40)[..2000], "a b a b"
        };
        var r = await Embed(textos);

        Assert.Equal(textos.Length, r.Vectores.Count);
        Assert.All(r.Vectores, v =>
        {
            Assert.Equal(1536, v.Length);
            Assert.InRange(Norma(v), 1 - 1e-6, 1 + 1e-6);
        });
    }

    // E8
    [Fact]
    public async Task Similitud_TextosConPalabrasEnComunSonMasParecidos()
    {
        var r = await Embed(
            "contrato de arrendamiento del inmueble",
            "el arrendamiento del inmueble termina con el contrato",
            "peritaje contable sobre honorarios impagos");

        Assert.True(Coseno(r.Vectores[0], r.Vectores[1]) > Coseno(r.Vectores[0], r.Vectores[2]) + 0.2);
    }

    // E9: valores calculados de forma independiente (SHA-256, primeros 8 bytes little-endian mod 1536; signo por el noveno byte).
    [Fact]
    public async Task Golden_PosicionesYValoresFijados()
    {
        var r = await Embed("contrato", "Contrato de arrendamiento: CONTRATO", "cláusula", "2026", C(0x1F642), "ñandú");

        static (int Posicion, float Valor)[] NoNulos(float[] v) =>
            v.Select((x, i) => (i, x)).Where(p => p.x != 0).ToArray();

        Assert.Equal(new[] { (1354, -1f) }, NoNulos(r.Vectores[0]));
        Assert.Equal(new[] { (1033, 1f) }, NoNulos(r.Vectores[2]));
        Assert.Equal(new[] { (21, 1f) }, NoNulos(r.Vectores[3]));
        Assert.Equal(new[] { (976, -1f) }, NoNulos(r.Vectores[4]));   // texto solo con un emoji: el rune es el token
        Assert.Equal(new[] { (835, 1f) }, NoNulos(r.Vectores[5]));

        // "contrato" ×2 (minúsculas invariantes), "de" y "arrendamiento": (-2, +1, -1) / sqrt(6).
        var frase = NoNulos(r.Vectores[1]);
        Assert.Equal(new[] { 472, 1173, 1354 }, frase.Select(p => p.Posicion));
        Assert.Equal(-0.40824829, frase[0].Valor, 6);
        Assert.Equal(0.40824829, frase[1].Valor, 6);
        Assert.Equal(-0.816496581, frase[2].Valor, 6);
    }

    // E10
    [Fact]
    public async Task Cancelacion_Propaga_SinResultado()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _mock.EmbedAsync(["texto"], EmbeddingPurpose.Documento, cts.Token));
    }

    [Fact]
    public void Mock_SinGanchosDeFallo_SoloLaInterfaz()
    {
        var publicos = typeof(MockEmbeddingProvider).GetMembers()
            .Where(m => m.DeclaringType == typeof(MockEmbeddingProvider) && m.MemberType is System.Reflection.MemberTypes.Property or System.Reflection.MemberTypes.Method or System.Reflection.MemberTypes.Field)
            .Select(m => m.Name).Where(n => !n.StartsWith("get_")).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "Dimensiones", "EmbedAsync", "MaxEntradasPorLote", "MaxTokensPorEntrada", "ModelId", "Modelo", "Proveedor", "ProviderId" },
            publicos);
        Assert.DoesNotContain(typeof(MockEmbeddingProvider).GetProperties(), p => p.CanWrite);
    }

    // E33
    [Fact]
    public void FirmaEmbedding_DeLosDosProveedores()
    {
        Assert.Equal("mock:mock-bow-sha256-v1@1536", FirmaEmbedding.De(_mock));

        using var entorno = new Entorno83(TransporteSimulado.QueNuncaResponde());
        Assert.IsType<OpenAICompatibleEmbeddingProvider>(entorno.Proveedor);
        Assert.Equal("openai-compatible:text-embedding-3-small@1536", FirmaEmbedding.De(entorno.Proveedor));
        Assert.NotEqual(FirmaEmbedding.De(_mock), FirmaEmbedding.De(entorno.Proveedor));
    }

    // E25c
    [Fact]
    public void Clasificacion_TablaFijaMotivoTransitorio_YCodigosPublicosExistentes()
    {
        var transitorios = new[] { MotivoFalloEmbedding.Timeout, MotivoFalloEmbedding.LimiteDeTasa, MotivoFalloEmbedding.NoDisponible, MotivoFalloEmbedding.ErrorDelServidor };
        foreach (var motivo in Enum.GetValues<MotivoFalloEmbedding>())
        {
            Assert.Equal(transitorios.Contains(motivo), ClasificacionFalloEmbedding.EsTransitorio(motivo));
            if (motivo == MotivoFalloEmbedding.Timeout)
            {
                continue;
            }

            var fallo = new EmbeddingProviderException(motivo, 500, 3, TimeSpan.FromSeconds(7));
            Assert.Equal((motivo, transitorios.Contains(motivo), (int?)500, 3, (TimeSpan?)TimeSpan.FromSeconds(7)),
                (fallo.Motivo, fallo.EsTransitorio, fallo.CodigoEstadoHttp, fallo.Intentos, fallo.EsperaSugerida));
            Assert.Equal("AI_PROVIDER_ERROR", fallo.ErrorCode);
            Assert.IsAssignableFrom<AIProviderException>(fallo);
            Assert.IsAssignableFrom<IFalloProveedorEmbeddings>(fallo);
            Assert.Null(fallo.InnerException);
        }

        var timeout = new EmbeddingProviderTimeoutException(2);
        Assert.Equal((MotivoFalloEmbedding.Timeout, true, 2, "AI_PROVIDER_TIMEOUT"), (timeout.Motivo, timeout.EsTransitorio, timeout.Intentos, timeout.ErrorCode));
        Assert.IsAssignableFrom<AIProviderTimeoutException>(timeout);
        Assert.IsAssignableFrom<IFalloProveedorEmbeddings>(timeout);
        Assert.Throws<ArgumentException>(() => new EmbeddingProviderException(MotivoFalloEmbedding.Timeout));

        // EsTransitorio se deriva del motivo: no hay forma de asignarlo.
        Assert.All(new[] { typeof(EmbeddingProviderException), typeof(EmbeddingProviderTimeoutException), typeof(IFalloProveedorEmbeddings) },
            t => Assert.False(t.GetProperty(nameof(IFalloProveedorEmbeddings.EsTransitorio))!.CanWrite));

        Assert.Equal(new[]
        {
            "Timeout", "LimiteDeTasa", "NoDisponible", "ErrorDelServidor",
            "Autenticacion", "SolicitudRechazada", "RespuestaInvalida", "DimensionInvalida", "ModeloInesperado"
        }, Enum.GetNames<MotivoFalloEmbedding>());
    }

    [Fact]
    public void ResultadoDelLote_FormaDeLaAdendaA831()
    {
        var parametros = typeof(EmbeddingBatchResult).GetConstructors().Single().GetParameters()
            .Select(p => $"{p.Name}:{(Nullable.GetUnderlyingType(p.ParameterType) is { } t ? t.Name + "?" : p.ParameterType.Name)}").ToArray();
        Assert.Equal(new[] { "Vectores:IReadOnlyList`1", "TokensEntrada:Int32?", "ModelId:String", "ModeloDeclarado:String", "ProviderId:String", "Dimensiones:Int32" }, parametros);

        var nulabilidad = new System.Reflection.NullabilityInfoContext();
        Assert.Equal(System.Reflection.NullabilityState.Nullable, nulabilidad.Create(typeof(EmbeddingBatchResult).GetProperty("ModeloDeclarado")!).ReadState);
        Assert.Equal(System.Reflection.NullabilityState.NotNull, nulabilidad.Create(typeof(EmbeddingBatchResult).GetProperty("ModelId")!).ReadState);

        Assert.Equal(new[] { "Dimensiones", "MaxEntradasPorLote", "MaxTokensPorEntrada", "ModelId", "ProviderId" },
            typeof(IEmbeddingProvider).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("EmbedAsync", Assert.Single(typeof(IEmbeddingProvider).GetMethods(), m => !m.IsSpecialName).Name);
    }
}
