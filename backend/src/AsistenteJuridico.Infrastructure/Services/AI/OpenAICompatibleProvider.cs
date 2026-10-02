using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AsistenteJuridico.Infrastructure.Services.AI;

/// <summary>
/// Proveedor de IA desacoplado compatible con la especificación OpenAI Chat Completions API.
/// Lee el modelo y credenciales dinámicamente de la configuración sin hardcodear marcas en Domain/Application.
/// Aplica <see cref="OpenAIOptions.TimeoutSeconds"/> a cada petición y traduce cualquier fallo del proveedor
/// a <see cref="AIProviderException"/> sin propagar cuerpos de respuesta, credenciales ni prompts.
/// </summary>
public class OpenAICompatibleProvider : IAIProvider
{
    private readonly HttpClient _httpClient;
    private readonly OpenAIOptions _options;
    private readonly ILogger<OpenAICompatibleProvider> _logger;

    public OpenAICompatibleProvider(
        HttpClient httpClient,
        IOptions<OpenAIOptions> options,
        ILogger<OpenAICompatibleProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public string ProviderId => "openai-compatible";

    public AIProviderCapabilities Capabilities => new(
        MaxContextTokens: _options.MaxContextTokens,
        MaxOutputTokens: _options.MaxOutputTokens,
        SupportsStreaming: true,
        SupportsSystemPrompt: true,
        SupportedModels: new[] { _options.ModelId }
    );

    public int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }

    public async Task<AIChatCompletionResponse> CompleteChatAsync(
        AIChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(GetTimeout());

        try
        {
            return await CompleteChatCoreAsync(request, timeoutCts.Token);
        }
        catch (Exception ex) when (TryTranslateFailure(ex, cancellationToken, out var translated))
        {
            throw translated;
        }
    }

    private async Task<AIChatCompletionResponse> CompleteChatCoreAsync(
        AIChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        var model = !string.IsNullOrEmpty(request.ModelId) ? request.ModelId : _options.ModelId;

        using var httpRequest = BuildHttpRequest(request, model, stream: false);
        using var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        EnsureSuccessStatus(httpResponse);

        var json = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var content = string.Empty;
        var finishReasonStr = "stop";

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var contentProp))
            {
                content = contentProp.GetString() ?? string.Empty;
            }
            if (firstChoice.TryGetProperty("finish_reason", out var frProp))
            {
                finishReasonStr = frProp.GetString() ?? "stop";
            }
        }

        var promptTokens = 0;
        var completionTokens = 0;
        var totalTokens = 0;

        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ct)) completionTokens = ct.GetInt32();
            if (usage.TryGetProperty("total_tokens", out var tt)) totalTokens = tt.GetInt32();
        }

        if (totalTokens == 0)
        {
            promptTokens = EstimateTokens(request.SystemPrompt ?? "") + 10;
            completionTokens = EstimateTokens(content);
            totalTokens = promptTokens + completionTokens;
        }

        var finishReason = finishReasonStr switch
        {
            "length" => AIFinishReason.Length,
            "content_filter" => AIFinishReason.ContentFilter,
            _ => AIFinishReason.Stop
        };

        return new AIChatCompletionResponse(
            Content: content,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            TotalTokens: totalTokens,
            FinishReason: finishReason,
            ModelId: model,
            ProviderId: ProviderId
        );
    }

    public async IAsyncEnumerable<AIChatCompletionChunk> StreamChatAsync(
        AIChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = !string.IsNullOrEmpty(request.ModelId) ? request.ModelId : _options.ModelId;
        var timeout = GetTimeout();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        HttpResponseMessage? httpResponse = null;
        StreamReader? reader = null;

        try
        {
            try
            {
                using var httpRequest = BuildHttpRequest(request, model, stream: true);
                httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                EnsureSuccessStatus(httpResponse);

                var stream = await httpResponse.Content.ReadAsStreamAsync(timeoutCts.Token);
                reader = new StreamReader(stream, Encoding.UTF8);
            }
            catch (Exception ex) when (TryTranslateFailure(ex, cancellationToken, out var translated))
            {
                throw translated;
            }

            while (true)
            {
                string? line;
                try
                {
                    // Tiempo máximo de inactividad entre fragmentos: el stream nunca espera indefinidamente.
                    timeoutCts.CancelAfter(timeout);
                    line = await reader.ReadLineAsync(timeoutCts.Token);
                }
                catch (Exception ex) when (TryTranslateFailure(ex, cancellationToken, out var translated))
                {
                    throw translated;
                }

                if (line == null)
                    break;

                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                    continue;

                var data = line.Substring(6).Trim();
                if (data == "[DONE]")
                    break;

                var chunk = ParseStreamChunk(data);
                if (chunk != null)
                {
                    yield return chunk;
                }
            }
        }
        finally
        {
            reader?.Dispose();
            httpResponse?.Dispose();
        }
    }

    private static AIChatCompletionChunk? ParseStreamChunk(string data)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return null;

            var choice = choices[0];
            string? deltaText = null;
            if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var deltaContent))
            {
                deltaText = deltaContent.GetString();
            }

            AIFinishReason? finishReason = null;
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            {
                finishReason = fr.GetString() switch
                {
                    "length" => AIFinishReason.Length,
                    "content_filter" => AIFinishReason.ContentFilter,
                    "stop" => AIFinishReason.Stop,
                    _ => null
                };
            }

            if (string.IsNullOrEmpty(deltaText) && finishReason == null)
                return null;

            return new AIChatCompletionChunk(
                DeltaContent: deltaText,
                FinishReason: finishReason
            );
        }
    }

    private HttpRequestMessage BuildHttpRequest(AIChatCompletionRequest request, string model, bool stream)
    {
        var payload = BuildOpenAIPayload(request, model, stream);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, CombineUrl(_options.BaseUrl, "chat/completions"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        return httpRequest;
    }

    private void EnsureSuccessStatus(HttpResponseMessage httpResponse)
    {
        if (httpResponse.IsSuccessStatusCode)
            return;

        // Solo se registra el código de estado: el cuerpo del proveedor puede reflejar fragmentos del prompt.
        _logger.LogError("El proveedor de IA respondió con estado no exitoso {StatusCode}", (int)httpResponse.StatusCode);
        throw new AIProviderException();
    }

    private TimeSpan GetTimeout()
    {
        var seconds = _options.TimeoutSeconds > 0 ? _options.TimeoutSeconds : 60;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Traduce un fallo de la llamada al proveedor. Devuelve false cuando la excepción debe propagarse sin cambios:
    /// cancelación solicitada por el llamador o una <see cref="AIProviderException"/> ya normalizada.
    /// </summary>
    private bool TryTranslateFailure(Exception ex, CancellationToken callerToken, out AIProviderException translated)
    {
        translated = null!;

        if (ex is AIProviderException)
            return false;

        if (ex is OperationCanceledException)
        {
            if (callerToken.IsCancellationRequested)
                return false;

            _logger.LogWarning("El proveedor de IA superó el tiempo máximo de {TimeoutSeconds} s; operación cancelada", _options.TimeoutSeconds);
            translated = new AIProviderTimeoutException();
            return true;
        }

        // Solo el tipo: el mensaje original puede contener URL, credenciales o contenido del prompt.
        _logger.LogError("Fallo de comunicación con el proveedor de IA: {ExceptionType}", ex.GetType().Name);
        translated = new AIProviderException();
        return true;
    }

    private static object BuildOpenAIPayload(AIChatCompletionRequest request, string model, bool stream)
    {
        var messages = new List<object>();

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new { role = "system", content = request.SystemPrompt });
        }

        foreach (var msg in request.Messages)
        {
            var roleStr = msg.Role switch
            {
                AIRolMensaje.User => "user",
                AIRolMensaje.Assistant => "assistant",
                _ => "user"
            };

            messages.Add(new { role = roleStr, content = msg.Content });
        }

        return new
        {
            model,
            messages,
            temperature = request.Temperature,
            stream,
            max_tokens = request.MaxTokens
        };
    }

    private static string CombineUrl(string baseUrl, string endpoint)
    {
        return baseUrl.TrimEnd('/') + "/" + endpoint.TrimStart('/');
    }
}
