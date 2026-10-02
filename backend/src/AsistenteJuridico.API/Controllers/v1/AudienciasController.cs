using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Audiencias.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AsistenteJuridico.API.Controllers.v1;

[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
[Authorize]
public class AudienciasController : ControllerBase
{
    private readonly IAudienciaService _audienciaService;

    public AudienciasController(IAudienciaService audienciaService)
    {
        _audienciaService = audienciaService;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.AudienciasManage)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AudienciaDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPaged([FromQuery] AudienciaFilterRequest request, CancellationToken cancellationToken)
    {
        var result = await _audienciaService.GetAudienciasPagedAsync(request, cancellationToken);
        return Ok(ApiResponse<PagedResult<AudienciaDto>>.Ok(result));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.AudienciasManage)]
    [ProducesResponseType(typeof(ApiResponse<AudienciaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _audienciaService.GetAudienciaByIdAsync(id, cancellationToken);
        return Ok(ApiResponse<AudienciaDto>.Ok(result));
    }

    [HttpPost]
    [Authorize(Policy = Permissions.AudienciasManage)]
    [ProducesResponseType(typeof(ApiResponse<AudienciaDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create([FromBody] CreateAudienciaDto dto, CancellationToken cancellationToken)
    {
        var result = await _audienciaService.CreateAudienciaAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, ApiResponse<AudienciaDto>.Ok(result, "Audiencia programada exitosamente."));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.AudienciasManage)]
    [ProducesResponseType(typeof(ApiResponse<AudienciaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAudienciaDto dto, CancellationToken cancellationToken)
    {
        var result = await _audienciaService.UpdateAudienciaAsync(id, dto, cancellationToken);
        return Ok(ApiResponse<AudienciaDto>.Ok(result, "Audiencia actualizada exitosamente."));
    }

    [HttpPatch("{id:guid}/estado")]
    [Authorize(Policy = Permissions.AudienciasManage)]
    [ProducesResponseType(typeof(ApiResponse<AudienciaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CambiarEstado(Guid id, [FromBody] CambiarEstadoAudienciaDto dto, CancellationToken cancellationToken)
    {
        var result = await _audienciaService.CambiarEstadoAsync(id, dto, cancellationToken);
        return Ok(ApiResponse<AudienciaDto>.Ok(result, "Estado de la audiencia actualizado exitosamente."));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.AudienciasManage)]
    [ProducesResponseType(typeof(ApiResponse<object?>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _audienciaService.DeleteAudienciaAsync(id, cancellationToken);
        return Ok(ApiResponse<object?>.Ok(null, "Audiencia cancelada y eliminada exitosamente."));
    }
}
