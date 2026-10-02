using AsistenteJuridico.Application.Common.Validators;
using AsistenteJuridico.Domain.Enums;
using Xunit;

namespace AsistenteJuridico.Domain.Tests.Fase4;

public class EcuadorianIdentityValidatorTests
{
    // Cédulas válidas
    [Theory]
    [InlineData("1710034065")] // Pichincha válida
    [InlineData("0922484050")] // Guayas válida
    [InlineData("0104863071")] // Azuay válida
    public void Validar_CedulaValida_RetornaTrue(string cedula)
    {
        var result = EcuadorianIdentityValidator.Validate(TipoIdentificacion.Cedula, cedula);
        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Theory]
    [InlineData("1710034066")] // Dígito verificador incorrecto
    [InlineData("3010034065")] // Provincia 30 inválida (> 24 y != 30 especial)
    [InlineData("17100340")]   // Longitud incorrecta
    [InlineData("17100340659")] // Longitud incorrecta
    [InlineData("171003406A")] // Caracter no numérico
    public void Validar_CedulaInvalida_RetornaFalse(string cedula)
    {
        var result = EcuadorianIdentityValidator.Validate(TipoIdentificacion.Cedula, cedula);
        Assert.False(result.IsValid);
    }

    // RUC Persona Natural (Cédula válida + 001)
    [Theory]
    [InlineData("1710034065001")]
    [InlineData("0922484050001")]
    public void Validar_RucPersonaNaturalValido_RetornaTrue(string ruc)
    {
        var result = EcuadorianIdentityValidator.Validate(TipoIdentificacion.Ruc, ruc);
        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Theory]
    [InlineData("1710034066001")] // Cédula base inválida
    [InlineData("1710034065000")] // Establecimiento 000 inválido
    [InlineData("1710034065")]    // RUC corto
    public void Validar_RucPersonaNaturalInvalido_RetornaFalse(string ruc)
    {
        var result = EcuadorianIdentityValidator.Validate(TipoIdentificacion.Ruc, ruc);
        Assert.False(result.IsValid);
    }

    // RUC Sociedad Privada (tercer dígito = 9, módulo 11)
    [Theory]
    [InlineData("1790016919001")] // Corporación Favorita C.A.
    [InlineData("0990004196001")] // Banco Guayaquil S.A.
    public void Validar_RucSociedadPrivadaValido_RetornaTrue(string ruc)
    {
        var result = EcuadorianIdentityValidator.Validate(TipoIdentificacion.Ruc, ruc);
        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Theory]
    [InlineData("1790016918001")] // Dígito verificador incorrecto
    [InlineData("1790016919000")] // Establecimiento 000
    public void Validar_RucSociedadPrivadaInvalido_RetornaFalse(string ruc)
    {
        var result = EcuadorianIdentityValidator.Validate(TipoIdentificacion.Ruc, ruc);
        Assert.False(result.IsValid);
    }

    // RUC Institución Pública (tercer dígito = 6, módulo 11)
    [Theory]
    [InlineData("1760001550001")] // IESS
    [InlineData("1760000820001")] // Ministerio de Finanzas
    public void Validar_RucInstitucionPublicaValido_RetornaTrue(string ruc)
    {
        var result = EcuadorianIdentityValidator.Validate(TipoIdentificacion.Ruc, ruc);
        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Theory]
    [InlineData("1760001590001")] // Dígito verificador incorrecto
    [InlineData("1760001550000")] // Establecimiento 000
    public void Validar_RucInstitucionPublicaInvalido_RetornaFalse(string ruc)
    {
        var result = EcuadorianIdentityValidator.Validate(TipoIdentificacion.Ruc, ruc);
        Assert.False(result.IsValid);
    }

    // Pasaporte
    [Theory]
    [InlineData("A1234567")]
    [InlineData("PA987654321")]
    [InlineData("EC123456")]
    public void Validar_PasaporteValido_RetornaTrue(string pasaporte)
    {
        var result = EcuadorianIdentityValidator.Validate(TipoIdentificacion.Pasaporte, pasaporte);
        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Theory]
    [InlineData("A12")] // Demasiado corto (< 5)
    [InlineData("A123456789012345678901")] // Demasiado largo (> 20)
    [InlineData("A123#456")] // Caracter especial inválido
    [InlineData("")]
    public void Validar_PasaporteInvalido_RetornaFalse(string pasaporte)
    {
        var result = EcuadorianIdentityValidator.Validate(TipoIdentificacion.Pasaporte, pasaporte);
        Assert.False(result.IsValid);
    }
}
