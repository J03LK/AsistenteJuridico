using System.Runtime.CompilerServices;

namespace AsistenteJuridico.Domain.Tests;

/// <summary>
/// Valores por defecto de los hosts de prueba (WebApplicationFactory) de este proceso.
/// </summary>
internal static class TestHostDefaults
{
    /// <summary>
    /// Fase 6.X (X1): el worker alojado de recuperación de Procesando no se ejecuta en los hosts de prueba. Todos
    /// comparten la base de desarrollo; un worker alojado en cualquier host recuperaría documentos de otras pruebas en
    /// paralelo y haría sus resultados no deterministas. Las pruebas del worker crean su propia instancia y controlan
    /// cuándo se ejecuta. Producción no cambia (Enabled = true por defecto).
    /// </summary>
#pragma warning disable CA2255 // Inicializador de módulo en un ensamblado de pruebas: se ejecuta antes de crear cualquier host.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Inicializar() =>
        Environment.SetEnvironmentVariable("AI__ProcessingRecovery__Enabled", "false");
}
