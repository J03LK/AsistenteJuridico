using AsistenteJuridico.API.Middleware;
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

    /// <summary>Límite de la petición de subida (archivo de hasta 25 MiB + campos del formulario): 26 MiB.</summary>
    public const long MaxUploadRequestBytes = 26L * 1024 * 1024;

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [Authorize(Policy = Permissions.DocumentosUpload)]
    [RequestSizeLimit(MaxUploadRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadRequestBytes)]
    [LimiteSubidaDocumento]
    [ProducesResponseType(typeof(ApiResponse<DocumentoDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status415UnsupportedMediaType)]
    public async Task<IActionResult> Upload(
        [FromForm] Guid expedienteId,
        [FromForm] string titulo,
        [FromForm] string tipoDocumento,
        [FromForm] string? descripcion,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file == null)
        {
            return BadRequest(ApiResponse<object>.Fail("No se proporcionó ningún archivo para subir."));
        }

        var dto = new UploadDocumentoDto(expedienteId, titulo, tipoDocumento, descripcion);

        // Un archivo vacío lo rechaza la validación de contenido (400 DOCUMENT_MAGIC_BYTES_INVALID)
        await using var stream = file.OpenReadStream();
        var result = await _documentoService.UploadDocumentoAsync(
            dto,
            stream,
            file.FileName,
            file.ContentType,
            file.Length,
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

    /// <summary>
    /// Fase 7.3 — Descarga endurecida. La autorización ocurre en el servicio antes de abrir el archivo.
    /// Siempre attachment (nunca inline); el nombre sale de NombreArchivoOriginal, nunca de la ruta física.
    /// </summary>
    [HttpGet("{id:guid}/download")]
    [Authorize(Policy = Permissions.DocumentosRead)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var result = await _documentoService.DownloadDocumentoAsync(id, cancellationToken);

        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "no-store";

        // File(..., fileDownloadName) emite Content-Disposition: attachment; filename=...; filename*=UTF-8''...
        return File(result.FileStream, result.ContentType, result.DownloadFileName);
    }

    /// <summary>
    /// Fase 7.3 — Listado oficial paginado. expedienteId obligatorio; orden CreatedAt DESC, Id DESC.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = Permissions.DocumentosRead)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<DocumentoDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPaged([FromQuery] DocumentoFilterRequest filtro, CancellationToken cancellationToken)
    {
        var result = await _documentoService.GetDocumentosPagedAsync(filtro, cancellationToken);
        return Ok(ApiResponse<PagedResult<DocumentoDto>>.Ok(result));
    }

    /// <summary>
    /// Fecha de obsolescencia del alias (RFC 9745, Structured Field Date): 2026-10-03T00:00:00Z, fecha de aprobación
    /// de X4 en el contrato de la Fase 7. Constante fija (D73-12); sin Sunset porque no hay fecha de retirada.
    /// </summary>
    private const long ObsolescenciaListadoPorExpedienteUnix = 1790985600;

    /// <summary>
    /// OBSOLETA (X4): usar GET /api/v1/documentos?expedienteId={id}. Misma consulta que la ruta oficial, sin
    /// paginar y con su forma de respuesta de siempre. Las cabeceras se emiten también en las respuestas de error.
    /// </summary>
    [HttpGet("expediente/{expedienteId:guid}")]
    [Authorize(Policy = Permissions.DocumentosRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<DocumentoDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByExpediente(Guid expedienteId, CancellationToken cancellationToken)
    {
        Response.Headers["Deprecation"] = $"@{ObsolescenciaListadoPorExpedienteUnix}";
        Response.Headers.Link = $"</api/v1/documentos?expedienteId={expedienteId}>; rel=\"successor-version\"";

        var result = await _documentoService.GetDocumentosByExpedienteAsync(expedienteId, cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<DocumentoDto>>.Ok(result));
    }

    /// <summary>
    /// Fase 7.3 — Edita SOLO titulo, tipoDocumento y descripcion, con la versión (xmin) que leyó el cliente.
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.DocumentosUpdate)]
    [ProducesResponseType(typeof(ApiResponse<DocumentoDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateDocumentoDto dto, CancellationToken cancellationToken)
    {
        var result = await _documentoService.UpdateDocumentoAsync(id, dto, cancellationToken);
        return Ok(ApiResponse<DocumentoDto>.Ok(result, "Documento actualizado exitosamente."));
    }

    /// <summary>
    /// Fase 7.3 — Borrado lógico con versión (xmin) obligatoria en la query: DELETE /api/v1/documentos/{id}?version=.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.DocumentosDelete)]
    [ProducesResponseType(typeof(ApiResponse<object?>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] uint? version, CancellationToken cancellationToken)
    {
        await _documentoService.DeleteDocumentoAsync(id, version, cancellationToken);
        return Ok(ApiResponse<object?>.Ok(null, "Documento eliminado exitosamente."));
    }
}
