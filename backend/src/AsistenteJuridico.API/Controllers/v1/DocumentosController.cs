using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Documentos.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AsistenteJuridico.API.Controllers.v1;

[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
[Authorize]
public class DocumentosController : ControllerBase
{
    private readonly IDocumentoService _documentoService;

    public DocumentosController(IDocumentoService documentoService)
    {
        _documentoService = documentoService;
    }

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [Authorize(Policy = Permissions.DocumentosUpload)]
    [ProducesResponseType(typeof(ApiResponse<DocumentoDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Upload(
        [FromForm] Guid expedienteId,
        [FromForm] string titulo,
        [FromForm] string tipoDocumento,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(ApiResponse<object>.Fail("No se proporcionó ningún archivo para subir."));
        }

        var dto = new UploadDocumentoDto(expedienteId, titulo, tipoDocumento);

        await using var stream = file.OpenReadStream();
        var result = await _documentoService.UploadDocumentoAsync(
            dto,
            stream,
            file.FileName,
            file.ContentType,
            cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = result.Id }, ApiResponse<DocumentoDto>.Ok(result, "Documento subido y almacenado exitosamente."));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.DocumentosRead)]
    [ProducesResponseType(typeof(ApiResponse<DocumentoDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _documentoService.GetDocumentoByIdAsync(id, cancellationToken);
        return Ok(ApiResponse<DocumentoDto>.Ok(result));
    }

    [HttpGet("{id:guid}/download")]
    [Authorize(Policy = Permissions.DocumentosRead)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var result = await _documentoService.DownloadDocumentoAsync(id, cancellationToken);

        // Cabeceras estrictas de seguridad para descarga
        Response.Headers.Append("X-Content-Type-Options", "nosniff");
        Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{Uri.EscapeDataString(result.DownloadFileName)}\"");

        return File(result.FileStream, result.ContentType, result.DownloadFileName);
    }

    [HttpGet("expediente/{expedienteId:guid}")]
    [Authorize(Policy = Permissions.DocumentosRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<DocumentoDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByExpediente(Guid expedienteId, CancellationToken cancellationToken)
    {
        var result = await _documentoService.GetDocumentosByExpedienteAsync(expedienteId, cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<DocumentoDto>>.Ok(result));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.DocumentosDelete)]
    [ProducesResponseType(typeof(ApiResponse<object?>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _documentoService.DeleteDocumentoAsync(id, cancellationToken);
        return Ok(ApiResponse<object?>.Ok(null, "Documento eliminado exitosamente."));
    }
}
