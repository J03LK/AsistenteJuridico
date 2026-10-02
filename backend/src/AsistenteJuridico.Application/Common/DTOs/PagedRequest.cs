namespace AsistenteJuridico.Application.Common.DTOs;

/// <summary>
/// Parámetros estándar para consultas paginadas deterministas.
/// </summary>
public record PagedRequest
{
    private int _pageNumber = 1;
    private int _pageSize = 10;

    public int PageNumber
    {
        get => _pageNumber;
        init => _pageNumber = value < 1 ? 1 : value;
    }

    public int PageSize
    {
        get => _pageSize;
        init => _pageSize = value switch
        {
            < 1 => 10,
            > 100 => 100,
            _ => value
        };
    }

    public string? SearchTerm { get; init; }
}
