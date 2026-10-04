using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Infrastructure.Services.AI;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>
/// Fase 8.3 — Configuración validada al arrancar, prohibición del simulado en Production, registro en DI y límites
/// de fase (FASE_8_3_CONTRATO.md §11, §12 y §18; pruebas E29–E32, E30b, E31 y E34).
/// </summary>
public class Fase83ConfiguracionYArquitecturaTests
{
    private static Dictionary<string, string?> Real(params (string Clave, string? Valor)[] cambios)
    {
        var config = new Dictionary<string, string?>
        {
            ["AI:Embeddings:Provider"] = "OpenAICompatible",
            ["AI:Embeddings:BaseUrl"] = "https://api.openai.com/v1",
            ["AI:Embeddings:ApiKey"] = Entorno83.Clave,
        };
        foreach (var (clave, valor) in cambios)
        {
            config["AI:Embeddings:" + clave] = valor;
        }

        return config;
    }

    private static OptionsValidationException Invalida(Dictionary<string, string?> config, string entorno = "Development")
    {
        using var e = new Entorno83(null, config, entorno, openAI: false);
        var error = Assert.Throws<OptionsValidationException>(() => e.Servicios.GetRequiredService<IOptions<EmbeddingOptions>>().Value);
        Assert.DoesNotContain(Entorno83.Clave, error.Message);
        Assert.DoesNotContain("usuario:contrasena", error.Message);
        return error;
    }

    private static EmbeddingOptions Valida(Dictionary<string, string?> config, string entorno = "Development")
    {
        using var e = new Entorno83(null, config, entorno, openAI: false);
        return e.Servicios.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
    }

    // E29
    public static TheoryData<string, string?, string> ConfiguracionesInvalidas => new()
    {
        { "Dimensiones", "1024", "1536" },
        { "Dimensiones", "0", "1536" },
        { "MaxEntradasPorLote", "65", "MaxEntradasPorLote" },
        { "MaxEntradasPorLote", "0", "MaxEntradasPorLote" },
        { "TimeoutSeconds", "0", "TimeoutSeconds" },
        { "TimeoutSeconds", "-5", "TimeoutSeconds" },
        { "Provider", "Otro", "Provider" },
        { "BaseUrl", "http://api.openai.com/v1", "HTTPS" },
        { "BaseUrl", "http://10.0.0.5:8080/v1", "HTTPS" },
        { "BaseUrl", "/v1/embeddings", "BaseUrl" },
        { "BaseUrl", "ftp://servidor/v1", "BaseUrl" },
        { "BaseUrl", "https://usuario:contrasena@api.openai.com/v1", "credenciales" },
        { "BaseUrl", "https://api.openai.com/v1?clave=1", "query" },
        { "BaseUrl", "https://api.openai.com/v1#f", "query" },
        { "ApiKey", "", "ApiKey" },
        { "ApiKey", "   ", "ApiKey" },
        { "ModelId", "", "ModelId" },
    };

    [Theory]
    [MemberData(nameof(ConfiguracionesInvalidas))]
    public void ConfiguracionInvalida_NoArranca_YElMensajeNoContieneSecretos(string clave, string? valor, string esperadoEnMensaje)
    {
        var error = Invalida(Real((clave, valor)));
        Assert.Contains(esperadoEnMensaje, error.Message);
    }

    // E30
    [Fact]
    public void ConfiguracionesValidas_Arrancan()
    {
        Assert.True(Valida(Real()).UsaOpenAICompatible);
        Assert.True(Valida(Real(("Provider", "openaicompatible"))).UsaOpenAICompatible);

        // Loopback: http y sin clave (servidores locales compatibles).
        foreach (var url in new[] { "http://localhost:8080/v1", "http://127.0.0.1:11434/v1", "http://[::1]:8000/v1", "https://localhost/v1" })
        {
            Assert.True(Valida(Real(("BaseUrl", url), ("ApiKey", ""))).UsaOpenAICompatible);
        }

        // Mock fuera de Production: por defecto y explícito, aunque las opciones de OpenAI estén vacías o sean inválidas.
        Assert.False(Valida([]).UsaOpenAICompatible);
        Assert.False(Valida(new() { ["AI:Embeddings:Provider"] = "Mock", ["AI:Embeddings:BaseUrl"] = "", ["AI:Embeddings:ApiKey"] = "" }).UsaOpenAICompatible);
        Assert.False(Valida(new() { ["AI:Embeddings:Provider"] = "mock" }, "Staging").UsaOpenAICompatible);
    }

    [Fact]
    public void ValoresPorDefecto_SonLosDelContrato()
    {
        var opciones = Valida([]);
        Assert.Equal(("https://api.openai.com/v1", "", "text-embedding-3-small", 1536, 60d, 64),
            (opciones.BaseUrl, opciones.ApiKey, opciones.ModelId, opciones.Dimensiones, opciones.TimeoutSeconds, opciones.MaxEntradasPorLote));
        Assert.Null(opciones.Provider);
        Assert.Equal((2, TimeSpan.FromSeconds(1.5), 16 * 1024 * 1024, 64, 8191),
            (EmbeddingOptions.MaxReintentos, EmbeddingOptions.RetardoBaseReintento, EmbeddingOptions.MaxBytesRespuesta,
                EmbeddingOptions.MaxEntradasPorLoteContractual, EmbeddingOptions.MaxTokensPorEntrada));
    }

    // E25b (parte de configuración): no existe ninguna vía para configurar reintentos, retardo, coste ni estimación.
    [Fact]
    public void Opciones_SinPropiedadesDeReintentosRetardoCosteNiEstimacion()
    {
        var propiedades = typeof(EmbeddingOptions).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "ApiKey", "BaseUrl", "Dimensiones", "MaxEntradasPorLote", "ModelId", "Provider", "TimeoutSeconds", "UsaOpenAICompatible" },
            propiedades);
        Assert.False(typeof(EmbeddingOptions).GetProperty(nameof(EmbeddingOptions.UsaOpenAICompatible))!.CanWrite);

        // Las constantes contractuales no son asignables.
        Assert.True(typeof(EmbeddingOptions).GetField(nameof(EmbeddingOptions.MaxReintentos))!.IsLiteral);
        Assert.True(typeof(EmbeddingOptions).GetField(nameof(EmbeddingOptions.RetardoBaseReintento))!.IsInitOnly);
    }

    // E30b — arranque real del host (ValidateOnStart)
    [Fact]
    public async Task Production_SinProveedor_NoArranca()
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => ArrancarAsync("Production", []));
        Assert.Contains("Production", error.Message);
        Assert.Contains("AI:Embeddings:Provider", error.Message);
    }

    [Theory]
    [InlineData("Mock")]
    [InlineData("mock")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Production_ConMockOVacio_NoArranca(string proveedor)
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            ArrancarAsync("Production", new() { ["AI:Embeddings:Provider"] = proveedor, ["AI:Embeddings:ApiKey"] = Entorno83.Clave }));
        Assert.Contains("Production", error.Message);
        Assert.DoesNotContain(Entorno83.Clave, error.Message);
    }

    [Fact]
    public async Task Production_ConProveedorRealSinClave_NoArranca()
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => ArrancarAsync("Production", Real(("ApiKey", ""))));
        Assert.Contains("ApiKey", error.Message);
    }

    [Fact]
    public async Task Production_ConProveedorRealYClave_Arranca_YResuelveElProveedorReal()
    {
        using var host = await ArrancarAsync("Production", Real());
        using var scope = host.Services.CreateScope();
        Assert.IsType<OpenAICompatibleEmbeddingProvider>(scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>());
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task FueraDeProduction_SinConfiguracion_ArrancaConElSimulado(string entorno)
    {
        using var host = await ArrancarAsync(entorno, []);
        using var scope = host.Services.CreateScope();
        Assert.IsType<MockEmbeddingProvider>(scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>());
        await host.StopAsync();
    }

    private static async Task<IHost> ArrancarAsync(string entorno, Dictionary<string, string?> config)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = entorno });
        builder.Configuration.AddInMemoryCollection(config);
        builder.Logging.ClearProviders();
        builder.Services.AddEmbeddingProvider(builder.Configuration);
        var host = builder.Build();
        try
        {
            await host.StartAsync();
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    // Corrección final 2: el simulado y el proveedor real aplican el mismo límite de lote, con la misma validación.

    private static Entorno83 EntornoDe(bool real, Dictionary<string, string?>? config = null) => real
        ? new Entorno83(new TransporteSimulado((_, _) => throw new InvalidOperationException("No debe haber llamadas de red.")), config)
        : new Entorno83(null, config, openAI: false);

    private static Entorno83 EntornoQueResponde(bool real, int n, Dictionary<string, string?>? config = null) => real
        ? new Entorno83(TransporteSimulado.Fijo(() => Respuestas83.Ok(Respuestas83.Json(n))), config)
        : new Entorno83(null, config, openAI: false);

    [Theory]
    [InlineData(false, 63)]
    [InlineData(false, 64)]
    [InlineData(true, 63)]
    [InlineData(true, 64)]
    public async Task Lote_De63YDe64_SeAcepta_EnElSimuladoYEnElReal(bool real, int n)
    {
        using var e = EntornoQueResponde(real, n);
        Assert.Equal(real ? typeof(OpenAICompatibleEmbeddingProvider) : typeof(MockEmbeddingProvider), e.Proveedor.GetType());
        Assert.Equal(64, e.Proveedor.MaxEntradasPorLote);

        var r = await e.Proveedor.EmbedAsync(Respuestas83.Textos(n), EmbeddingPurpose.Documento, default);

        Assert.Equal(n, r.Vectores.Count);
        Assert.All(r.Vectores, v => Assert.Equal(1536, v.Length));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lote_De65_SeRechaza_ConLaMismaExcepcion_EnElSimuladoYEnElReal(bool real)
    {
        using var e = EntornoDe(real);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => e.Proveedor.EmbedAsync(Respuestas83.Textos(65), EmbeddingPurpose.Documento, default));

        Assert.Equal("entradas", error.ParamName);
        Assert.Contains("número máximo de entradas por lote", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lote_ConElLimiteConfigurado_MismaSemanticaEnElSimuladoYEnElReal(bool real)
    {
        // AI:Embeddings:MaxEntradasPorLote es la opción del contrato (1–64): si se reduce, rige igual para los dos.
        var config = new Dictionary<string, string?> { ["AI:Embeddings:MaxEntradasPorLote"] = "10" };

        using (var e = EntornoQueResponde(real, 10, config))
        {
            Assert.Equal(10, e.Proveedor.MaxEntradasPorLote);
            Assert.Equal(10, (await e.Proveedor.EmbedAsync(Respuestas83.Textos(10), EmbeddingPurpose.Documento, default)).Vectores.Count);
        }

        using (var e = EntornoDe(real, config))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => e.Proveedor.EmbedAsync(Respuestas83.Textos(11), EmbeddingPurpose.Documento, default));
        }
    }

    // Revisión final: 64 es un máximo ABSOLUTO; ninguna configuración puede elevarlo.

    [Theory]
    [InlineData("65")]
    [InlineData("100")]
    [InlineData("2048")]
    [InlineData("2147483647")]
    public void Lote_ConfiguracionPorEncimaDe64_NoArranca(string valor)
    {
        foreach (var config in new[]
        {
            Real(("MaxEntradasPorLote", valor)),
            new Dictionary<string, string?> { ["AI:Embeddings:Provider"] = "Mock", ["AI:Embeddings:MaxEntradasPorLote"] = valor }
        })
        {
            Assert.Contains("MaxEntradasPorLote", Invalida(config).Message);
        }
    }

    [Fact]
    public async Task Lote_OpcionesSinValidar_ElTopeAbsolutoDe64RigeIgual_EnElSimuladoYEnElReal()
    {
        // Proveedores construidos directamente con opciones que se saltan la validación de arranque.
        var opciones = Options.Create(new EmbeddingOptions { MaxEntradasPorLote = 1000, ApiKey = Entorno83.Clave, BaseUrl = Entorno83.BaseUrl });
        var transporte = TransporteSimulado.Fijo(() => Respuestas83.Ok(Respuestas83.Json(64)));
        using var http = new HttpClient(transporte);
        IEmbeddingProvider[] proveedores =
        [
            new MockEmbeddingProvider(opciones),
            new OpenAICompatibleEmbeddingProvider(http, opciones, NullLogger<OpenAICompatibleEmbeddingProvider>.Instance)
        ];

        foreach (var proveedor in proveedores)
        {
            Assert.Equal(64, proveedor.MaxEntradasPorLote);
            Assert.Equal(64, (await proveedor.EmbedAsync(Respuestas83.Textos(64), EmbeddingPurpose.Documento, default)).Vectores.Count);
            await Assert.ThrowsAsync<ArgumentException>(() => proveedor.EmbedAsync(Respuestas83.Textos(65), EmbeddingPurpose.Documento, default));
        }

        Assert.Equal(1, transporte.Llamadas);   // solo el lote de 64 del proveedor real llegó a la red
    }

    [Fact]
    public void Lote_ValidacionComun_AplicaSiempreElTopeAbsoluto()
    {
        Assert.Equal(64, LoteEmbeddings.MaxEntradasPorLoteContractual);
        Assert.Equal((1, 10, 64, 64, 64), (LoteEmbeddings.LimiteEfectivo(0), LoteEmbeddings.LimiteEfectivo(10), LoteEmbeddings.LimiteEfectivo(64),
            LoteEmbeddings.LimiteEfectivo(65), LoteEmbeddings.LimiteEfectivo(int.MaxValue)));

        LoteEmbeddings.Validar(Respuestas83.Textos(64), int.MaxValue, 8191);
        Assert.Throws<ArgumentException>(() => LoteEmbeddings.Validar(Respuestas83.Textos(65), int.MaxValue, 8191));
        Assert.Throws<ArgumentException>(() => LoteEmbeddings.Validar(Respuestas83.Textos(65), 65, 8191));
        Assert.Throws<ArgumentException>(() => LoteEmbeddings.Validar(Respuestas83.Textos(11), 10, 8191));
    }

    [Fact]
    public void Lote_SimuladoSinOpciones_UsaElMaximoContractualDe64_YLaValidacionEsUnica()
    {
        Assert.Equal(64, new MockEmbeddingProvider().MaxEntradasPorLote);
        Assert.Equal(64, EmbeddingOptions.MaxEntradasPorLoteContractual);

        // Una sola validación de lote en producción, usada por los dos proveedores.
        var src = Fase82ExtraccionTests.UbicarDirectorio("backend", "src");
        var conValidacion = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && File.ReadAllText(f).Contains("LoteEmbeddings.Validar("))
            .Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(new[] { "MockEmbeddingProvider.cs", "OpenAICompatibleEmbeddingProvider.cs" }, conValidacion);
    }

    // E31
    [Fact]
    public async Task AplicacionReal_EnDesarrollo_RegistraElSimulado_SinConsumidores()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();

        var proveedor = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        Assert.IsType<MockEmbeddingProvider>(proveedor);
        Assert.Equal("mock:mock-bow-sha256-v1@1536", FirmaEmbedding.De(proveedor));
        Assert.Equal(1536, (await proveedor.EmbedAsync(["texto de prueba"], EmbeddingPurpose.Documento, default)).Vectores[0].Length);
    }

    [Fact]
    public void ManejadorPrimario_SinRedireccionesNiCookies()
    {
        var manejador = Assert.IsType<SocketsHttpHandler>(EmbeddingServiceCollectionExtensions.CrearManejadorPrimario());
        Assert.False(manejador.AllowAutoRedirect);
        Assert.False(manejador.UseCookies);
        Assert.Equal("ai-embeddings-retry", EmbeddingServiceCollectionExtensions.NombreManejadorResiliencia);
    }

    // E32
    [Fact]
    public void ConfiguracionVersionada_SinClaveDeEmbeddingsNiPrecios()
    {
        var api = Fase82ExtraccionTests.UbicarDirectorio("backend", "src", "AsistenteJuridico.API");
        var archivos = Directory.EnumerateFiles(api, "appsettings*.json").ToList();
        Assert.NotEmpty(archivos);
        foreach (var archivo in archivos)
        {
            var contenido = File.ReadAllText(archivo);
            foreach (var prohibido in new[] { "\"Embeddings\"", "CostoUsd", "CostPer", "PorMillon", "RetryBaseDelay", "MaxRetries\": 5" })
            {
                Assert.DoesNotContain(prohibido, contenido, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // E34 — no contaminación de fase
    [Fact]
    public void Arquitectura_SinConsumidoresNiPersistenciaNiWorkerNiConsumoNiCoste()
    {
        var src = Fase82ExtraccionTests.UbicarDirectorio("backend", "src");
        var archivos = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => Path.GetRelativePath(src, f).Replace('\\', '/'), File.ReadAllText);

        string[] Con(string simbolo) => archivos.Where(a => a.Value.Contains(simbolo, StringComparison.Ordinal)).Select(a => a.Key).Order().ToArray();

        const string interfaz = "AsistenteJuridico.Application/Common/Interfaces/AI/IEmbeddingProvider.cs";
        const string mock = "AsistenteJuridico.Infrastructure/Services/AI/MockEmbeddingProvider.cs";
        const string openAI = "AsistenteJuridico.Infrastructure/Services/AI/OpenAICompatibleEmbeddingProvider.cs";
        const string registro = "AsistenteJuridico.Infrastructure/Services/AI/EmbeddingServiceCollectionExtensions.cs";
        const string opciones = "AsistenteJuridico.Infrastructure/Services/AI/EmbeddingOptions.cs";
        const string excepciones = "AsistenteJuridico.Application/Common/Exceptions/EmbeddingProviderExceptions.cs";

        // La interfaz solo aparece en su definición, sus dos implementaciones, el registro en DI y, desde la 8.4, su
        // único consumidor: el servicio de indexación (y el perfil de indexación, que compone la firma).
        const string indexacion = "AsistenteJuridico.Infrastructure/Services/IndexacionSemanticaService.cs";
        const string codigos = "AsistenteJuridico.Application/Common/Indexacion/CodigosIndexacion.cs";
        Assert.Equal(new[] { codigos, interfaz, registro, mock, openAI, indexacion }.Order(), Con("IEmbeddingProvider"));
        Assert.Equal(new[] { interfaz, mock, openAI, indexacion }.Order(), Con("EmbedAsync"));
        Assert.Equal(new[] { "AsistenteJuridico.Infrastructure/DependencyInjection.cs", registro }, Con("AddEmbeddingProvider"));

        // Los archivos de la 8.3 no persisten, no registran consumo, no calculan costes y no indexan ni buscan.
        foreach (var archivo in new[] { interfaz, mock, openAI, registro, opciones, excepciones })
        {
            foreach (var prohibido in new[]
            {
                "DbContext", "SaveChanges", "DocumentoFragmento", "DocumentoIndices", "AIUsageLog", "CostoEstimado", "CalculateCost",
                "BackgroundService", "IHostedService", "SKIP LOCKED", "<=>", "websearch_to_tsquery", "ControllerBase", "MapPost", "Fragmentador"
            })
            {
                Assert.False(archivos[archivo].Contains(prohibido, StringComparison.Ordinal), $"{archivo} contiene {prohibido}");
            }
        }

        // Los servicios en segundo plano: los dos anteriores a la 8.3 y, desde la 8.4, el worker de indexación.
        Assert.Equal(new[]
        {
            "AsistenteJuridico.Infrastructure/BackgroundServices/AlertasBackgroundService.cs",
            "AsistenteJuridico.Infrastructure/BackgroundServices/IndexacionSemanticaBackgroundService.cs",
            "AsistenteJuridico.Infrastructure/BackgroundServices/ProcesamientoIaRecoveryBackgroundService.cs"
        }, Con(": BackgroundService"));

        // Desde la 8.4, la única asignación a la propiedad Embedding y la única alta de fragmentos están en el servicio
        // de indexación. Los índices se siguen sin dar de alta con EF (el sembrado es SQL); el único
        // "new DocumentoIndice" es la entidad adjunta con la que ese servicio escribe sobre su propia fila.
        var asignacion = new System.Text.RegularExpressions.Regex(@"\bEmbedding\s*=[^=>]");
        Assert.Equal(new[] { indexacion }, archivos.Where(a => !a.Key.Contains("/Migrations/") && asignacion.IsMatch(a.Value)).Select(a => a.Key));
        Assert.Equal(new[] { indexacion }, Con("DocumentoFragmentos.Add"));
        Assert.Equal(new[] { indexacion }, Con("new DocumentoFragmento"));
        Assert.Empty(Con("DocumentoIndices.Add"));
        Assert.Equal(new[] { indexacion }, Con("new DocumentoIndice"));

        // La API no referencia el proveedor (ningún endpoint nuevo).
        Assert.DoesNotContain(archivos.Keys, a => a.StartsWith("AsistenteJuridico.API/") && archivos[a].Contains("Embedding", StringComparison.Ordinal));
    }
}
