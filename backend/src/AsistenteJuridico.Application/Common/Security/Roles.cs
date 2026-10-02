namespace AsistenteJuridico.Application.Common.Security;

/// <summary>
/// Roles estándar predefinidos en el sistema jurídico.
/// </summary>
public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string AdminEstudio = "AdminEstudio";
    public const string AbogadoSenior = "AbogadoSenior";
    public const string AbogadoJunior = "AbogadoJunior";
    public const string AsistenteLegal = "AsistenteLegal";

    public static readonly IReadOnlyList<string> All =
    [
        SuperAdmin,
        AdminEstudio,
        AbogadoSenior,
        AbogadoJunior,
        AsistenteLegal
    ];
}
