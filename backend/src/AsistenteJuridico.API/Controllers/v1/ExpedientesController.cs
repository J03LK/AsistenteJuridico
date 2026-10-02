using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Expedientes.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AsistenteJuridico.API.Controllers.v1;

[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
[Authorize]
public class ExpedientesController : ControllerBase
{
    private readonly IExpedienteService _expedienteService;

    public ExpedientesController(IExpedienteService expedienteService)
    {
        _expedienteService = expedienteService;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.ExpedientesRead)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ExpedienteDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPaged([FromQuery] ExpedienteFilterRequest request, CancellationToken cancellationToken)
    {
        var result = await _expedienteService.GetExpedientesPagedAsync(request, cancellationToken);
        return Ok(ApiResponse<PagedResult<ExpedienteDto>>.Ok(result));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.ExpedientesRead)]
    [ProducesResponseType(typeof(ApiResponse<ExpedienteDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _expedienteService.GetExpedienteByIdAsync(id, cancellationToken);
        return Ok(ApiResponse<ExpedienteDetailDto>.Ok(result));
    }

    [HttpPost]
    [Authorize(Policy = Permissions.ExpedientesCreate)]
    [ProducesResponseType(typeof(ApiResponse<ExpedienteDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create([FromBody] CreateExpedienteDto dto, CancellationToken cancellationToken)
    {
        var result = await _expedienteService.CreateExpedienteAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, ApiResponse<ExpedienteDto>.Ok(result, "Expediente creado exitosamente."));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ExpedientesUpdate)]
    [ProducesResponseType(typeof(ApiResponse<ExpedienteDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateExpedienteDto dto, CancellationToken cancellationToken)
    {
        var result = await _expedienteService.UpdateExpedienteAsync(id, dto, cancellationToken);
        return Ok(ApiResponse<ExpedienteDto>.Ok(result, "Expediente actualizado exitosamente."));
    }

    [HttpPatch("{id:guid}/estado")]
    [Authorize(Policy = Permissions.ExpedientesUpdate)]
    [ProducesResponseType(typeof(ApiResponse<ExpedienteDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CambiarEstado(Guid id, [FromBody] CambiarEstadoExpedienteDto dto, CancellationToken cancellationToken)
    {
        var result = await _expedienteService.CambiarEstadoAsync(id, dto, cancellationToken);
        return Ok(ApiResponse<ExpedienteDto>.Ok(result, "Estado del expediente actualizado exitosamente."));
    }

    [HttpPost("{id:guid}/procesos-judiciales")]
    [Authorize(Policy = Permissions.ProcesosLink)]
    [ProducesResponseType(typeof(ApiResponse<ExpedienteProcesoJudicialDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> VincularProceso(Guid id, [FromBody] VincularProcesoDto dto, CancellationToken cancellationToken)
    {
        var result = await _expedienteService.VincularProcesoAsync(id, dto, cancellationToken);
        return Ok(ApiResponse<ExpedienteProcesoJudicialDto>.Ok(result, "Causa judicial vinculada exitosamente al expediente."));
    }

    [HttpDelete("{id:guid}/procesos-judiciales/{procesoJudicialId:guid}")]
    [Authorize(Policy = Permissions.ProcesosLink)]
    [ProducesResponseType(typeof(ApiResponse<object?>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DesvincularProceso(Guid id, Guid procesoJudicialId, CancellationToken cancellationToken)
    {
        await _expedienteService.DesvincularProcesoAsync(id, procesoJudicialId, cancellationToken);
        return Ok(ApiResponse<object?>.Ok(null, "Causa judicial desvinculada exitosamente del expediente."));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ExpedientesDelete)]
    [ProducesResponseType(typeof(ApiResponse<object?>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _expedienteService.DeleteExpedienteAsync(id, cancellationToken);
        return Ok(ApiResponse<object?>.Ok(null, "Expediente eliminado exitosamente."));
    }
}
