namespace AsistenteJuridico.Application.Common.DTOs;

/// <summary>
/// Envelope estándar para todas las respuestas de la API.
/// </summary>
public class ApiResponse<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public string Message { get; init; } = string.Empty;
    public IEnumerable<string> Errors { get; init; } = [];
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public static ApiResponse<T> Ok(T data, string message = "Operación completada exitosamente") =>
        new() { Success = true, Data = data, Message = message };

    public static ApiResponse<T> Fail(string message, IEnumerable<string>? errors = null) =>
        new() { Success = false, Message = message, Errors = errors ?? [] };
}

/// <summary>
/// Envelope paginado para listas de recursos.
/// </summary>
public class PagedApiResponse<T> : ApiResponse<IEnumerable<T>>
{
    public PaginationMeta Pagination { get; init; } = new();

    public static PagedApiResponse<T> Ok(
        IEnumerable<T> data,
        int page,
        int pageSize,
        int totalRecords,
        string message = "Operación completada exitosamente") =>
        new()
        {
            Success = true,
            Data = data,
            Message = message,
            Pagination = new PaginationMeta
            {
                Page = page,
                PageSize = pageSize,
                TotalRecords = totalRecords,
                TotalPages = (int)Math.Ceiling((double)totalRecords / pageSize)
            }
        };
}

public class PaginationMeta
{
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalRecords { get; init; }
    public int TotalPages { get; init; }
}
