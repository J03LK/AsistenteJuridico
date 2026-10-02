using System.Text.RegularExpressions;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Application.Common.Validators;

/// <summary>
/// Validador algorítmico estricto para identificaciones ecuatorianas (Cédula, RUC personas naturales,
/// RUC sociedades privadas, RUC entidades públicas y Pasaportes).
/// </summary>
public static partial class EcuadorianIdentityValidator
{
    private static readonly int[] CoeficientesCedula = [2, 1, 2, 1, 2, 1, 2, 1, 2];
    private static readonly int[] CoeficientesRucPrivada = [4, 3, 2, 7, 6, 5, 4, 3, 2];
    private static readonly int[] CoeficientesRucPublica = [3, 2, 7, 6, 5, 4, 3, 2];

    [GeneratedRegex("^[a-zA-Z0-9]{5,20}$")]
    private static partial Regex PasaporteRegex();

    public static (bool IsValid, string? ErrorMessage) Validate(TipoIdentificacion tipo, string? identificacion)
    {
        if (string.IsNullOrWhiteSpace(identificacion))
        {
            return (false, "El número de identificación es obligatorio.");
        }

        var trimmed = identificacion.Trim();

        return tipo switch
        {
            TipoIdentificacion.Cedula => ValidateCedula(trimmed),
            TipoIdentificacion.Ruc => ValidateRuc(trimmed),
            TipoIdentificacion.Pasaporte => ValidatePasaporte(trimmed),
            TipoIdentificacion.Otro => (trimmed.Length >= 3 && trimmed.Length <= 30, trimmed.Length < 3 || trimmed.Length > 30 ? "La identificación debe tener entre 3 y 30 caracteres." : null),
            _ => (false, "Tipo de identificación no reconocido.")
        };
    }

    public static (bool IsValid, string? ErrorMessage) ValidateCedula(string cedula)
    {
        if (cedula.Length != 10 || !cedula.All(char.IsDigit))
        {
            return (false, "La cédula debe contener exactamente 10 dígitos numéricos.");
        }

        var provincia = int.Parse(cedula[..2]);
        if ((provincia < 1 || provincia > 24) && provincia != 30)
        {
            return (false, $"Código de provincia '{provincia:D2}' inválido para cédula ecuatoriana.");
        }

        var tercerDigito = cedula[2] - '0';
        if (tercerDigito >= 6)
        {
            return (false, "El tercer dígito de una cédula debe ser menor a 6.");
        }

        var suma = 0;
        for (var i = 0; i < 9; i++)
        {
            var prod = (cedula[i] - '0') * CoeficientesCedula[i];
            if (prod >= 10) prod -= 9;
            suma += prod;
        }

        var digitoVerificadorCalculado = (10 - (suma % 10)) % 10;
        var digitoVerificadorReal = cedula[9] - '0';

        if (digitoVerificadorCalculado != digitoVerificadorReal)
        {
            return (false, "Dígito verificador de cédula inválido (Módulo 10).");
        }

        return (true, null);
    }

    public static (bool IsValid, string? ErrorMessage) ValidateRuc(string ruc)
    {
        if (ruc.Length != 13 || !ruc.All(char.IsDigit))
        {
            return (false, "El RUC debe contener exactamente 13 dígitos numéricos.");
        }

        var provincia = int.Parse(ruc[..2]);
        if ((provincia < 1 || provincia > 24) && provincia != 30)
        {
            return (false, $"Código de provincia '{provincia:D2}' inválido para RUC.");
        }

        var tercerDigito = ruc[2] - '0';

        // 1. RUC Persona Natural (tercer dígito < 6): Primeros 10 dígitos son cédula válida + establecimiento > 000
        if (tercerDigito < 6)
        {
            var cedulaParte = ruc[..10];
            var (isCedulaValid, cedulaErr) = ValidateCedula(cedulaParte);
            if (!isCedulaValid)
            {
                return (false, $"RUC Persona Natural inválido: {cedulaErr}");
            }

            var establecimiento = ruc[10..13];
            if (establecimiento == "000")
            {
                return (false, "El código de establecimiento del RUC de Persona Natural no puede ser '000'.");
            }

            return (true, null);
        }

        // 2. RUC Sociedad Pública (tercer dígito == 6): Módulo 11 con 8 coeficientes, verif en 9º dígito
        if (tercerDigito == 6)
        {
            var establecimiento = ruc[9..13];
            if (establecimiento == "0000")
            {
                return (false, "El código de establecimiento del RUC de Sociedad Pública no puede ser '0000'.");
            }

            var suma = 0;
            for (var i = 0; i < 8; i++)
            {
                suma += (ruc[i] - '0') * CoeficientesRucPublica[i];
            }

            var residuo = suma % 11;
            var digitoVerificadorCalculado = residuo == 0 ? 0 : 11 - residuo;
            if (digitoVerificadorCalculado == 11) digitoVerificadorCalculado = 0;

            var digitoVerificadorReal = ruc[8] - '0';
            if (digitoVerificadorCalculado != digitoVerificadorReal)
            {
                return (false, "Dígito verificador de RUC Sociedad Pública inválido (Módulo 11).");
            }

            return (true, null);
        }

        // 3. RUC Sociedad Privada o Extranjeros (tercer dígito == 9): Módulo 11 con 9 coeficientes, verif en 10º dígito
        if (tercerDigito == 9)
        {
            var establecimiento = ruc[10..13];
            if (establecimiento == "000")
            {
                return (false, "El código de establecimiento del RUC de Sociedad Privada no puede ser '000'.");
            }

            var suma = 0;
            for (var i = 0; i < 9; i++)
            {
                suma += (ruc[i] - '0') * CoeficientesRucPrivada[i];
            }

            var residuo = suma % 11;
            var digitoVerificadorCalculado = residuo == 0 ? 0 : 11 - residuo;
            if (digitoVerificadorCalculado == 11) digitoVerificadorCalculado = 0;

            var digitoVerificadorReal = ruc[9] - '0';
            if (digitoVerificadorCalculado != digitoVerificadorReal)
            {
                return (false, "Dígito verificador de RUC Sociedad Privada inválido (Módulo 11).");
            }

            return (true, null);
        }

        return (false, $"Tercer dígito '{tercerDigito}' no es válido para ningún tipo de RUC ecuatoriano.");
    }

    public static (bool IsValid, string? ErrorMessage) ValidatePasaporte(string pasaporte)
    {
        if (pasaporte.Length < 5 || pasaporte.Length > 20)
        {
            return (false, "El pasaporte debe tener entre 5 y 20 caracteres.");
        }

        if (!PasaporteRegex().IsMatch(pasaporte))
        {
            return (false, "El pasaporte solo puede contener caracteres alfanuméricos.");
        }

        return (true, null);
    }
}
