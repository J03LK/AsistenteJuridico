using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Alertas.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AsistenteJuridico.API.Controllers.v1;

[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
[Authorize]
public class AlertasController : ControllerBase
{
    private readonly IAlertasService _alertasService;

    public AlertasController(IAlertasService alertasService)
    {
        _alertasService = alertasService;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.AlertasRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AlertaProcesalDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAlertas([FromQuery] AlertasFilterRequest request, CancellationToken cancellationToken)
    {
        var result = await _alertasService.GetAlertasAsync(request, cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<AlertaProcesalDto>>.Ok(result));
    }

    [HttpGet("conteo-no-leidas")]
    [Authorize(Policy = Permissions.AlertasRead)]
    [ProducesResponseType(typeof(ApiResponse<ConteoNoLeidasDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetConteoNoLeidas(CancellationToken cancellationToken)
    {
        var result = await _alertasService.GetConteoNoLeidasAsync(cancellationToken);
        return Ok(ApiResponse<ConteoNoLeidasDto>.Ok(result));
    }

    [HttpPut("{id:guid}/marcar-leida")]
    [Authorize(Policy = Permissions.AlertasManage)]
    [ProducesResponseType(typeof(ApiResponse<AlertaProcesalDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MarcarLeida(
        Guid id,
        [FromBody] MarcarLeidaRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _alertasService.MarcarLeidaAsync(id, request?.Version, cancellationToken);
        return Ok(ApiResponse<AlertaProcesalDto>.Ok(result, "Alerta marcada como leída."));
    }

    [HttpPut("{id:guid}/descartar")]
    [Authorize(Policy = Permissions.AlertasManage)]
    [ProducesResponseType(typeof(ApiResponse<AlertaProcesalDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Descartar(
        Guid id,
        [FromBody] DescartarAlertaRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _alertasService.DescartarAlertaAsync(id, request?.Motivo, request?.Version, cancellationToken);
        return Ok(ApiResponse<AlertaProcesalDto>.Ok(result, "Alerta descartada con éxito."));
    }

    [HttpPut("marcar-todas-leidas")]
    [Authorize(Policy = Permissions.AlertasManage)]
    [ProducesResponseType(typeof(ApiResponse<MarcarTodasLeidasResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> MarcarTodasLeidas(
        [FromBody] MarcarTodasLeidasRequest? request,
        CancellationToken cancellationToken)
    {
        var req = request ?? new MarcarTodasLeidasRequest();
        var result = await _alertasService.MarcarTodasLeidasAsync(req, cancellationToken);
        return Ok(ApiResponse<MarcarTodasLeidasResponseDto>.Ok(result, "Todas las alertas han sido marcadas como leídas."));
    }
}
