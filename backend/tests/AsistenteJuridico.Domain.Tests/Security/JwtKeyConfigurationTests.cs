using AsistenteJuridico.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AsistenteJuridico.Domain.Tests.Security;

/// <summary>
/// La API no tiene clave JWT por defecto: sin una clave externa válida debe fallar al iniciar con un error claro.
/// </summary>
public class JwtKeyConfigurationTests
{
    private static IConfiguration Configuracion(string? jwtKey)
    {
        var valores = new Dictionary<string, string?>
        {
            // Cadena ficticia: AddInfrastructureServices no abre la conexión
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=no_usada"
        };
        if (jwtKey != null)
        {
            valores["Jwt:Key"] = jwtKey;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SinClaveJwt_FallaAlIniciarConMensajeClaro(string? jwtKey)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddInfrastructureServices(Configuracion(jwtKey)));

        Assert.Contains("Jwt:Key", ex.Message);
        Assert.Contains("Jwt__Key", ex.Message);
    }

    [Fact]
    public void ClaveDeEjemploDeEnvExample_EsRechazada()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddInfrastructureServices(Configuracion("CAMBIAR_EN_PRODUCCION_MINIMO_32_CARACTERES")));

        Assert.Contains("ejemplo", ex.Message);
    }

    [Fact]
    public void ClaveDemasiadoCorta_EsRechazada()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddInfrastructureServices(Configuracion("corta")));

        Assert.Contains("32 bytes", ex.Message);
    }

    [Fact]
    public void ClaveExternaValida_PermiteRegistrarServicios()
    {
        var claveDePrueba = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

        var services = new ServiceCollection().AddInfrastructureServices(Configuracion(claveDePrueba));

        Assert.NotEmpty(services);
    }
}
