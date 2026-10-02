using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace AsistenteJuridico.Domain.Tests.Security;

/// <summary>
/// El seeder de desarrollo no contiene contraseñas: las toma de configuración externa y falla con un error
/// explícito que nombra cada clave ausente.
/// </summary>
public class DevSeedConfigurationTests
{
    private static string ValorDePrueba() => Guid.NewGuid().ToString("N");

    [Fact]
    public void SinContrasenasConfiguradas_FallaNombrandoTodasLasClaves()
    {
        var configuracion = new ConfigurationBuilder().Build();

        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseSeeder.GetRequiredSeedPasswords(configuracion));

        Assert.Equal(5, DatabaseSeeder.RequiredPasswordKeys.Count);
        foreach (var clave in DatabaseSeeder.RequiredPasswordKeys)
        {
            Assert.Contains(clave, ex.Message);
        }
    }

    [Fact]
    public void ConUnaClaveVacia_FallaNombrandoSoloEsaClave()
    {
        var valores = DatabaseSeeder.RequiredPasswordKeys.ToDictionary(k => k, _ => (string?)ValorDePrueba());
        valores[DatabaseSeeder.PasswordSuperAdminKey] = "  ";
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseSeeder.GetRequiredSeedPasswords(configuracion));

        Assert.Contains(DatabaseSeeder.PasswordSuperAdminKey, ex.Message);
        Assert.DoesNotContain(DatabaseSeeder.PasswordAdminKey, ex.Message);
    }

    [Fact]
    public void ConTodasLasClaves_DevuelveLosValoresConfigurados()
    {
        var valores = DatabaseSeeder.RequiredPasswordKeys.ToDictionary(k => k, _ => (string?)ValorDePrueba());
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

        var passwords = DatabaseSeeder.GetRequiredSeedPasswords(configuracion);

        foreach (var clave in DatabaseSeeder.RequiredPasswordKeys)
        {
            Assert.Equal(valores[clave], passwords[clave]);
        }
    }
}
