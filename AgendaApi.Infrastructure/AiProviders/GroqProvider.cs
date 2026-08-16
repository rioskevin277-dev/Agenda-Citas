using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Infrastructure.AiProviders;

/// <summary>
/// Adaptador para Groq (nivel gratuito). API compatible con OpenAI:
/// usa /chat/completions con formato de tool calling idéntico a OpenAI.
/// Modelo por defecto: llama-3.3-70b-versatile (soporta function calling).
/// Clave en variable de entorno Groq__ApiKey / GROQ_API_KEY.
/// </summary>
/// <remarks>
/// El nivel gratuito de on_demand limita los tokens/minuto (TPM). Cuando el loop de
/// tool-calling hace varias llamadas seguidas se agota la cuota y Groq responde 429 con
/// "Please try again in X.XXXs". El 429 es transitorio y se auto-repone en segundos, así
/// que aquí se reintenta con backoff leyendo el "retry in Xs" del body (o el header
/// Retry-After). Sin esto, el orchestrator caía al fallback (OpenAI/Anthropic) en el primer
/// 429 y, si tampoco tienen cuota, escalaba por error de infraestructura y congelaba la IA.
/// </remarks>
public class GroqProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GroqProvider> _logger;
    private const string ApiUrl = "https://api.groq.com/openai/v1/chat/completions";
    private const string Model = "llama-3.3-70b-versatile";

    /// <summary>Intentos máximos ante 429/503 (el rate limit resetea cada minuto en el tier on_demand).</summary>
    private const int MaxRateLimitRetries = 3;
    /// <summary>Techo para el backoff: nunca esperar más de ~25s por reintento.</summary>
    private static readonly TimeSpan MaxRateLimitBackoff = TimeSpan.FromSeconds(25);

    public GroqProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<GroqProvider> logger)
    {
        _httpClient = httpClientFactory.CreateClient("groq-api");
        _logger = logger;
    }

    public async Task<string> GenerateResponseAsync(
        List<ChatMessage> messages,
        List<object>? tools = null,
        CancellationToken ct = default)
    {
        var apiKey = GetApiKey();
        var body = new
        {
            model = Model,
            messages = messages.Select(m => new
            {
                role = m.Role,
                content = m.Content
            }).ToList()
        };

        for (int attempt = 0; attempt < MaxRateLimitRetries; attempt++)
        {
            var request = CreateRequest(apiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                var data = JsonSerializer.Deserialize<GroqChatResponse>(json);
                return data?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
            }

            // Sólo reintentamos ante rate limit (429) o indisponibilidad temporal (503);
            // el resto de errores es definitivo y no esperaríamos a que cambie solo.
            if (ShouldRetry(response.StatusCode, json) && attempt < MaxRateLimitRetries - 1)
            {
                var delay = GetBackoff(response, json);
                _logger.LogWarning("[Groq] Respuesta transitoria ({Status}: {Reason}), reintento {Attempt}/{Max} en {Delay:0.0}s",
                    response.StatusCode, response.ReasonPhrase, attempt + 1, MaxRateLimitRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                continue;
            }

            _logger.LogError("[Groq] Error en API: {StatusCode} - {Response}", response.StatusCode, json);
            throw new Exception($"Groq API error: {response.StatusCode}");
        }
        throw new Exception("Groq API error: reintentos de rate limit agotados");
    }

    public async Task<AiToolCallResult> GenerateResponseWithToolsAsync(
        List<ChatMessage> messages,
        List<object> tools,
        CancellationToken ct = default)
    {
        var apiKey = GetApiKey();
        var body = new
        {
            model = Model,
            messages = messages.Select(MapOpenAiMessage).ToList(),
            tools = tools,
            tool_choice = "auto"
        };
        var jsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        for (int attempt = 0; attempt < MaxRateLimitRetries; attempt++)
        {
            var request = CreateRequest(apiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, jsonOptions),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                return ParseToolResponse(json, _logger);
            }

            if (ShouldRetry(response.StatusCode, json) && attempt < MaxRateLimitRetries - 1)
            {
                var delay = GetBackoff(response, json);
                _logger.LogWarning("[Groq] Respuesta transitoria ({Status}: {Reason}), reintento {Attempt}/{Max} en {Delay:0.0}s",
                    response.StatusCode, response.ReasonPhrase, attempt + 1, MaxRateLimitRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                continue;
            }

            _logger.LogError("[Groq] Error en API: {StatusCode} - {Response}", response.StatusCode, json);
            return new AiToolCallResult { Success = false, TextContent = $"Error: {response.StatusCode}" };
        }
        return new AiToolCallResult { Success = false, TextContent = "Error: rate limit agotado" };
    }

    /// <summary>Interpreta la respuesta de chat cuando fue success (200). Separado para lectura y tests.</summary>
    internal static AiToolCallResult ParseToolResponse(string json, ILogger logger)
    {
        try
        {
            var data = JsonSerializer.Deserialize<GroqChatResponse>(json);
            if (data?.Choices == null || data.Choices.Count == 0)
                return new AiToolCallResult { Success = false, TextContent = "Respuesta vacía" };

            var choice = data.Choices[0];
            var message = choice.Message;

            if (choice.FinishReason == "tool_calls" && message?.ToolCalls != null && message.ToolCalls.Count > 0)
            {
                return new AiToolCallResult
                {
                    Success = true,
                    TextContent = message.Content,
                    FinishReason = "tool_calls",
                    ToolCalls = message.ToolCalls.Select(tc => new ToolCall
                    {
                        Id = tc.Id,
                        Name = tc.Function?.Name ?? "",
                        Arguments = tc.Function?.Arguments ?? "{}"
                    }).ToList()
                };
            }

            return new AiToolCallResult
            {
                Success = true,
                TextContent = message?.Content ?? "",
                FinishReason = "stop"
            };
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "[Groq] JSON de respuesta inválido: {Json}", json);
            return new AiToolCallResult { Success = false, TextContent = "Respuesta inválida" };
        }
    }

    /// <summary>
    /// Serializa un ChatMessage al formato OpenAI-compatible que espera la API.
    /// - Rol "tool": incluye tool_call_id y name (resultado de la herramienta).
    /// - Rol "assistant" con ToolCalls: reenvía el array tool_calls (obligatorio para continuar).
    /// </summary>
    private static object MapOpenAiMessage(ChatMessage m)
    {
        if (m.Role == "tool")
            return new { role = "tool", content = m.Content, tool_call_id = m.ToolCallId, name = m.ToolName };

        if (m.Role == "assistant" && m.ToolCalls.Count > 0)
        {
            return new
            {
                role = "assistant",
                content = m.Content,
                tool_calls = m.ToolCalls.Select(tc => new
                {
                    id = tc.Id,
                    type = "function",
                    function = new { name = tc.Name, arguments = tc.Arguments }
                })
            };
        }

        return new { role = m.Role, content = m.Content };
    }

    /// <summary>
/// Determinamos si vale la pena reintentar. Son transitorios:
/// - 429 (rate limit TPM/RPM) y 503 (indisponibilidad temporal), que se auto-reponen.
/// - 400 con code "tool_use_failed": Groq falló al generar la llamada a función (p. ej. emite
///   "&lt;function=...&gt;" en vez del JSON de tool_calls). Es una falla de generación puntual,
///   normalmente se resuelve en el siguiente intento (el modelo re-rolla).
/// Otros 400 (esquema malformado, etc.) son definitivos y no se reintentan.
/// </summary>
private static bool ShouldRetry(HttpStatusCode status, string json)
    => status == HttpStatusCode.TooManyRequests
       || status == (HttpStatusCode)503
       || (status == HttpStatusCode.BadRequest
           && json.IndexOf("\"tool_use_failed\"", StringComparison.OrdinalIgnoreCase) >= 0);

/// <summary>Backoff según el tipo de fallo transitorio.</summary>
private static TimeSpan GetBackoff(HttpResponseMessage response, string json)
{
    // tool_use_failed: el modelo re-rolla al instante; basta un backoff corto fijo.
    if (response.StatusCode == HttpStatusCode.BadRequest)
        return TimeSpan.FromMilliseconds(1200);
    return GetRateLimitBackoff(response, json);
}

    /// <summary>
    /// Backoff según el rate limit: Groq indica el tiempo en el body del 429
    /// ("Please try again in X.XXXs") y/o en el header Retry-After. Si no lo trae,
    /// asumimos un valor razonable. Acotado por <see cref="MaxRateLimitBackoff"/>.
    /// </summary>
    private static TimeSpan GetRateLimitBackoff(HttpResponseMessage response, string json)
    {
        // 1) Header Retry-After (segundos enteros). 2) "again in Xs" del body. 3) default.
        double? seconds = null;

        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var raw = values.FirstOrDefault();
            if (raw != null
                && double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var headerSeconds))
                seconds = headerSeconds;
        }

        if (!seconds.HasValue)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                json, @"again in ([\d.]+)\s*s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success
                && double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var bodySeconds))
                seconds = bodySeconds;
        }

        var delay = TimeSpan.FromSeconds(seconds ?? 5);
        return delay > MaxRateLimitBackoff ? MaxRateLimitBackoff : delay;
    }

    private string GetApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("Groq__ApiKey");
        if (string.IsNullOrWhiteSpace(envKey))
            envKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
            return envKey;

        throw new InvalidOperationException("No se encontró Groq API Key. Configurar Groq__ApiKey");
    }

    private HttpRequestMessage CreateRequest(string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    // Response DTOs (formato OpenAI-compatible)
    private class GroqChatResponse
    {
        [JsonPropertyName("choices")]
        public List<GroqChoice>? Choices { get; set; }
    }

    private class GroqChoice
    {
        [JsonPropertyName("message")]
        public GroqMessage? Message { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private class GroqMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<GroqToolCall>? ToolCalls { get; set; }
    }

    private class GroqToolCall
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public GroqFunction? Function { get; set; }
    }

    private class GroqFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        public string Arguments { get; set; } = string.Empty;
    }
}