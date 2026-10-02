using Microsoft.Extensions.Configuration;

namespace AsistenteJuridico.Domain.Tests;

/// <summary>
/// Configuración de las pruebas de integración contra PostgreSQL. Usa las mismas fuentes externas que la API
/// (dotnet user-secrets del proyecto API y variables de entorno); ninguna credencial vive en el repositorio.
/// </summary>
internal static class TestConfiguration
{
    private static readonly Lazy<IConfiguration> Configuration = new(() => new ConfigurationBuilder()
        .AddUserSecrets(typeof(Program).Assembly, optional: true)
        .AddEnvironmentVariables()
        .Build());

    /// <summary>
    /// Cadena de conexión a la base de desarrollo: 'ConnectionStrings:DefaultConnection' en user-secrets
    /// o la variable de entorno 'ConnectionStrings__DefaultConnection'.
    /// </summary>
    public static string PostgresConnectionString =>
        Configuration.Value.GetConnectionString("DefaultConnection") is { Length: > 0 } connectionString
            ? connectionString
            : throw new InvalidOperationException(
                "Falta 'ConnectionStrings:DefaultConnection' para las pruebas de integración. Configúrela con " +
                "'dotnet user-secrets set \"ConnectionStrings:DefaultConnection\" <cadena> --project src/AsistenteJuridico.API' " +
                "o con la variable de entorno 'ConnectionStrings__DefaultConnection'.");
}
