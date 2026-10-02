using AsistenteJuridico.Application.Common.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace AsistenteJuridico.API.Controllers.v1;

/// <summary>
/// Health check endpoint para verificar que la API está activa
/// y que los servicios dependientes responden correctamente.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
public class HealthController : ControllerBase
{
    private readonly ILogger<HealthController> _logger;

    public HealthController(ILogger<HealthController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Verifica el estado de la API.
    /// </summary>
    /// <returns>Estado de salud de la aplicación.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<HealthResponseDto>), StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        _logger.LogInformation("Health check ejecutado exitosamente");

        var health = new HealthResponseDto
        {
            Status = "Healthy",
            Version = "1.0.0",
            Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Unknown",
            Timestamp = DateTime.UtcNow
        };

        return Ok(ApiResponse<HealthResponseDto>.Ok(health, "API operativa"));
    }
}

public record HealthResponseDto
{
    public string Status { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
}
