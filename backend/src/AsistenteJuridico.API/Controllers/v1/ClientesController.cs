using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Clientes.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AsistenteJuridico.API.Controllers.v1;

[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
[Authorize]
public class ClientesController : ControllerBase
{
    private readonly IClienteService _clienteService;

    public ClientesController(IClienteService clienteService)
    {
        _clienteService = clienteService;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.ClientesRead)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ClienteDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPaged([FromQuery] PagedRequest request, CancellationToken cancellationToken)
    {
        var result = await _clienteService.GetClientesPagedAsync(request, cancellationToken);
        return Ok(ApiResponse<PagedResult<ClienteDto>>.Ok(result));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.ClientesRead)]
    [ProducesResponseType(typeof(ApiResponse<ClienteDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _clienteService.GetClienteByIdAsync(id, cancellationToken);
        return Ok(ApiResponse<ClienteDto>.Ok(result));
    }

    [HttpPost]
    [Authorize(Policy = Permissions.ClientesCreate)]
    [ProducesResponseType(typeof(ApiResponse<ClienteDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateClienteDto dto, CancellationToken cancellationToken)
    {
        var result = await _clienteService.CreateClienteAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, ApiResponse<ClienteDto>.Ok(result, "Cliente registrado exitosamente."));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ClientesUpdate)]
    [ProducesResponseType(typeof(ApiResponse<ClienteDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateClienteDto dto, CancellationToken cancellationToken)
    {
        var result = await _clienteService.UpdateClienteAsync(id, dto, cancellationToken);
        return Ok(ApiResponse<ClienteDto>.Ok(result, "Cliente actualizado exitosamente."));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ClientesDelete)]
    [ProducesResponseType(typeof(ApiResponse<object?>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _clienteService.DeleteClienteAsync(id, cancellationToken);
        return Ok(ApiResponse<object?>.Ok(null, "Cliente eliminado exitosamente."));
    }
}
