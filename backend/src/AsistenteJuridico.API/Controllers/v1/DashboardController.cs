using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Dashboard.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AsistenteJuridico.API.Controllers.v1;

[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IDashboardService _dashboardService;

    public DashboardController(IDashboardService dashboardService)
    {
        _dashboardService = dashboardService;
    }

    [HttpGet("resumen")]
    [Authorize(Policy = Permissions.DashboardRead)]
    [ProducesResponseType(typeof(ApiResponse<DashboardResumenDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetResumen(CancellationToken cancellationToken)
    {
        var result = await _dashboardService.GetResumenAsync(cancellationToken);
        return Ok(ApiResponse<DashboardResumenDto>.Ok(result));
    }

    [HttpGet("eficiencia")]
    [Authorize(Policy = Permissions.DashboardRead)]
    [ProducesResponseType(typeof(ApiResponse<MetricasEficienciaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetEficiencia(
        [FromQuery] DateTime? desdeUtc,
        [FromQuery] DateTime? hastaUtc,
        CancellationToken cancellationToken)
    {
        var request = new EficienciaRequest
        {
            FechaDesdeUtc = desdeUtc,
            FechaHastaUtc = hastaUtc
        };
        var result = await _dashboardService.GetEficienciaAsync(request, cancellationToken);
        return Ok(ApiResponse<MetricasEficienciaDto>.Ok(result));
    }
}
