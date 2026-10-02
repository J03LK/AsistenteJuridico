using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.ProcesosJudiciales.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AsistenteJuridico.API.Controllers.v1;

[ApiController]
[Route("api/v1/procesos-judiciales")]
[Route("api/v1/[controller]")]
[Produces("application/json")]
[Authorize]
public class ProcesosJudicialesController : ControllerBase
{
    private readonly IProcesoJudicialService _procesoService;

    public ProcesosJudicialesController(IProcesoJudicialService procesoService)
    {
        _procesoService = procesoService;
    }

    [HttpGet("consultar/{numeroProceso}")]
    [HttpGet("mock/{numeroProceso}")]
    [HttpGet("buscar/{numeroProceso}")]
    [Authorize(Policy = Permissions.ProcesosRead)]
    [ProducesResponseType(typeof(ApiResponse<ProcesoJudicialConsultaResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ConsultarEnMock(string numeroProceso, CancellationToken cancellationToken)
    {
        var result = await _procesoService.ConsultarEnMockAsync(numeroProceso, cancellationToken);
        if (result == null)
        {
            return NotFound(ApiResponse<object>.Fail($"No se encontró información judicial para el proceso '{numeroProceso}'."));
        }

        return Ok(ApiResponse<ProcesoJudicialConsultaResult>.Ok(result));
    }

    [HttpPost("sincronizar")]
    [HttpPost("sincronizar-mock")]
    [HttpPost("importar")]
    [Authorize(Policy = Permissions.ProcesosLink)]
    [ProducesResponseType(typeof(ApiResponse<ProcesoJudicialDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Sincronizar([FromBody] SincronizarProcesoDto dto, CancellationToken cancellationToken)
    {
        var result = await _procesoService.SincronizarProcesoAsync(dto, cancellationToken);
        return Ok(ApiResponse<ProcesoJudicialDto>.Ok(result, "Causa judicial sincronizada exitosamente."));
    }

    [HttpGet]
    [Authorize(Policy = Permissions.ProcesosRead)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ProcesoJudicialDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPaged([FromQuery] PagedRequest request, CancellationToken cancellationToken)
    {
        var result = await _procesoService.GetProcesosPagedAsync(request, cancellationToken);
        return Ok(ApiResponse<PagedResult<ProcesoJudicialDto>>.Ok(result));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.ProcesosRead)]
    [ProducesResponseType(typeof(ApiResponse<ProcesoJudicialDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _procesoService.GetProcesoByIdAsync(id, cancellationToken);
        return Ok(ApiResponse<ProcesoJudicialDto>.Ok(result));
    }
}
