using AsistenteJuridico.Application.Common.Exceptions;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AsistenteJuridico.API.Middleware;

/// <summary>
/// Fase 7.2 (decisión C) — Filtro de recurso de la subida de documentos. Lee el formulario antes del model binding
/// para traducir el exceso de tamaño de la petición (límite de Kestrel o del formulario multipart) en
/// <see cref="PayloadTooLargeException"/> con DOCUMENT_SIZE_EXCEEDED, que GlobalExceptionMiddleware responde como 413
/// con envelope. Sin este filtro, MVC captura el error al leer el formulario y responde un 400 automático.
///
/// Corre después de [RequestSizeLimit] y [RequestFormLimits] (Order 900), que fijan los límites. Cualquier otro error
/// de formulario se deja pasar: el formulario queda cacheado y el model binding responde exactamente como antes.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LimiteSubidaDocumentoAttribute : Attribute, IAsyncResourceFilter, IOrderedFilter
{
    public int Order => 1000;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (request.HasFormContentType)
        {
            try
            {
                await request.ReadFormAsync(context.HttpContext.RequestAborted);
            }
            catch (Exception ex) when (EsExcesoDeTamanio(ex))
            {
                throw new PayloadTooLargeException("La solicitud excede el tamaño máximo permitido para la subida de documentos.")
                {
                    ErrorCode = DocumentoErrorCodes.SizeExceeded
                };
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                // Formulario mal formado u otro error de lectura: lo resuelve el model binding como siempre
            }
        }

        await next();
    }

    private static bool EsExcesoDeTamanio(Exception ex) =>
        ex is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge }
        || (ex is InvalidDataException && ex.Message.Contains("body length limit", StringComparison.OrdinalIgnoreCase));
}
