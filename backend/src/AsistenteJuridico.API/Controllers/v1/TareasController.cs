using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Tareas.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AsistenteJuridico.API.Controllers.v1;

[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
[Authorize]
public class TareasController : ControllerBase
{
    private readonly ITareaService _tareaService;

    public TareasController(ITareaService tareaService)
    {
        _tareaService = tareaService;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.TareasRead)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<TareaDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPaged([FromQuery] TareaFilterRequest request, CancellationToken cancellationToken)
    {
        var result = await _tareaService.GetTareasPagedAsync(request, cancellationToken);
        return Ok(ApiResponse<PagedResult<TareaDto>>.Ok(result));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.TareasRead)]
    [ProducesResponseType(typeof(ApiResponse<TareaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _tareaService.GetTareaByIdAsync(id, cancellationToken);
        return Ok(ApiResponse<TareaDto>.Ok(result));
    }

    [HttpPost]
    [Authorize(Policy = Permissions.TareasManage)]
    [ProducesResponseType(typeof(ApiResponse<TareaDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create([FromBody] CreateTareaDto dto, CancellationToken cancellationToken)
    {
        var result = await _tareaService.CreateTareaAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, ApiResponse<TareaDto>.Ok(result, "Tarea creada exitosamente."));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.TareasManage)]
    [ProducesResponseType(typeof(ApiResponse<TareaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTareaDto dto, CancellationToken cancellationToken)
    {
        var result = await _tareaService.UpdateTareaAsync(id, dto, cancellationToken);
        return Ok(ApiResponse<TareaDto>.Ok(result, "Tarea actualizada exitosamente."));
    }

    [HttpPatch("{id:guid}/estado")]
    [Authorize(Policy = Permissions.TareasManage)]
    [ProducesResponseType(typeof(ApiResponse<TareaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CambiarEstado(Guid id, [FromBody] CambiarEstadoTareaDto dto, CancellationToken cancellationToken)
    {
        var result = await _tareaService.CambiarEstadoAsync(id, dto, cancellationToken);
        return Ok(ApiResponse<TareaDto>.Ok(result, "Estado de la tarea actualizado exitosamente."));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.TareasManage)]
    [ProducesResponseType(typeof(ApiResponse<object?>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _tareaService.DeleteTareaAsync(id, cancellationToken);
        return Ok(ApiResponse<object?>.Ok(null, "Tarea eliminada exitosamente."));
    }
}
