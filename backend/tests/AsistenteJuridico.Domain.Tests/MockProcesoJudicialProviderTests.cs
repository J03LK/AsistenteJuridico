using AsistenteJuridico.Infrastructure.Services;
using Xunit;

namespace AsistenteJuridico.Domain.Tests;

public class MockProcesoJudicialProviderTests
{
    private readonly MockProcesoJudicialProvider _provider = new();

    [Fact]
    public async Task ConsultarPorNumeroAsync_ShouldReturnProceso_WhenMatchesFormat()
    {
        // Act
        var result = await _provider.ConsultarPorNumeroAsync("17230-2023-00456");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("17230-2023-00456", result.NumeroProceso);
        Assert.Contains("Iñaquito", result.Judicatura);
        Assert.NotEmpty(result.PartesProcesales);
    }

    [Fact]
    public async Task ConsultarPorIdentificacionAsync_ShouldReturnProcesos_WhenPartyExists()
    {
        // Act (Cedula de Juan Alberto Pérez Morales)
        var results = await _provider.ConsultarPorIdentificacionAsync("Cedula", "1712345678");

        // Assert
        Assert.NotNull(results);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.NumeroProceso == "17230-2023-00456");
    }

    [Fact]
    public async Task ConsultarPorNumeroAsync_ShouldReturnNull_WhenNotFound()
    {
        // Act
        var result = await _provider.ConsultarPorNumeroAsync("99999-9999-99999");

        // Assert
        Assert.Null(result);
    }
}
