using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using Xunit;

namespace AsistenteJuridico.Domain.Tests.Fase4;

public class ExpedienteStateMachineTests
{
    [Theory]
    [InlineData(EstadoExpediente.Abierto, EstadoExpediente.EnTramite)]
    [InlineData(EstadoExpediente.Abierto, EstadoExpediente.Suspendido)]
    [InlineData(EstadoExpediente.Abierto, EstadoExpediente.Archivado)]
    [InlineData(EstadoExpediente.EnTramite, EstadoExpediente.Suspendido)]
    [InlineData(EstadoExpediente.EnTramite, EstadoExpediente.Cerrado)]
    [InlineData(EstadoExpediente.EnTramite, EstadoExpediente.Archivado)]
    [InlineData(EstadoExpediente.Suspendido, EstadoExpediente.EnTramite)]
    [InlineData(EstadoExpediente.Suspendido, EstadoExpediente.Archivado)]
    [InlineData(EstadoExpediente.Cerrado, EstadoExpediente.Archivado)]
    [InlineData(EstadoExpediente.Cerrado, EstadoExpediente.EnTramite)] // Reapertura
    [InlineData(EstadoExpediente.Archivado, EstadoExpediente.EnTramite)] // Desarchivo
    public void PuedeTransicionarA_TransicionValida_RetornaTrue(EstadoExpediente estadoInicial, EstadoExpediente estadoDestino)
    {
        var expediente = new Expediente { Estado = estadoInicial };
        var result = expediente.PuedeTransicionarA(estadoDestino);
        Assert.True(result, $"La transición de {estadoInicial} a {estadoDestino} debería ser válida según diseño v4.0.");
    }

    [Theory]
    [InlineData(EstadoExpediente.Abierto, EstadoExpediente.Cerrado)] // Debe pasar por EnTramite
    [InlineData(EstadoExpediente.EnTramite, EstadoExpediente.Abierto)]
    [InlineData(EstadoExpediente.Suspendido, EstadoExpediente.Abierto)]
    [InlineData(EstadoExpediente.Suspendido, EstadoExpediente.Cerrado)]
    [InlineData(EstadoExpediente.Cerrado, EstadoExpediente.Abierto)]
    [InlineData(EstadoExpediente.Cerrado, EstadoExpediente.Suspendido)]
    [InlineData(EstadoExpediente.Archivado, EstadoExpediente.Abierto)]
    [InlineData(EstadoExpediente.Archivado, EstadoExpediente.Suspendido)]
    [InlineData(EstadoExpediente.Archivado, EstadoExpediente.Cerrado)]
    public void PuedeTransicionarA_TransicionInvalida_RetornaFalse(EstadoExpediente estadoInicial, EstadoExpediente estadoDestino)
    {
        var expediente = new Expediente { Estado = estadoInicial };
        var result = expediente.PuedeTransicionarA(estadoDestino);
        Assert.False(result, $"La transición de {estadoInicial} a {estadoDestino} debería ser inválida según diseño v4.0.");
    }
}
