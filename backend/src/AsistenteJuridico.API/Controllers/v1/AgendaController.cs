using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Agenda.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AsistenteJuridico.API.Controllers.v1;

[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
[Authorize]
public class AgendaController : ControllerBase
{
    private readonly IAgendaService _agendaService;

    public AgendaController(IAgendaService agendaService)
    {
        _agendaService = agendaService;
    }

    [HttpGet("eventos")]
    [Authorize(Policy = Permissions.AgendaRead)]
    [ProducesResponseType(typeof(ApiResponse<AgendaPaginadaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetEventos([FromQuery] AgendaFilterRequest request, CancellationToken cancellationToken)
    {
        var result = await _agendaService.GetEventosAsync(request, cancellationToken);
        return Ok(ApiResponse<AgendaPaginadaDto>.Ok(result));
    }

    [HttpGet("hoy")]
    [Authorize(Policy = Permissions.AgendaRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EventoAgendaDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetHoy(CancellationToken cancellationToken)
    {
        var result = await _agendaService.GetEventosHoyAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<EventoAgendaDto>>.Ok(result));
    }
}
