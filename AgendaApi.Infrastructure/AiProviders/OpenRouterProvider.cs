using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Infrastructure.AiProviders;

/// <summary>
/// Adaptador para OpenRouter (https://openrouter.ai): un gateway OpenAI-compatible que expone
/// docenas de modelos —varios con tier gratuito (:free)— detrás de UNA sola API key. Es el
/// respaldo ideal de Groq cuando su cuota gratuita diaria se agota: se usa el MISMO formato
/// OpenAI de chat completions y tool calling, así que la clase sigue el patrón de
/// <see cref="OpenAIProvider"/> cambiando base URL, modelos (:free) y el header de origen.
///
/// La API key se lee de la variable de entorno OpenRouter__ApiKey (respaldada por
/// OPENROUTER_API_KEY). El modelo se configura con OpenRouter__Model (default:
/// "deepseek/deepseek-chat-v3-0324:free", un modelo con cupo libre amplio y buen tool-calling).
/// </summary>
public class OpenRouterProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenRouterProvider> _logger;
    private const string ApiUrl = "https://openrouter.ai/api/v1/chat/completions";
    private const string SiteName = "AgendaApi";

    public OpenRouterProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<OpenRouterProvider> logger)
    {
        _httpClient = httpClientFactory.CreateClient("openrouter-api");
        _logger = logger;
    }

    private static string Model =>
        Environment.GetEnvironmentVariable("OpenRouter__Model")
        ?? Environment.GetEnvironmentVariable("OPENROUTER_MODEL")
        ?? "nvidia/nemotron-3-super-120b-a12b:free";

    // Los modelos con sufijo ":free" de OpenRouter a veces gastan tokens en "reasoning" y
    // cortan en "length" sin emitir la tool call si el límite es bajo. Dejar un límite alto
    // (p. ej. 1200) garantiza que el razonamiento agotador no apague el turno. Configurable
    // para poder bajar costo con un modelo de pago barato (p. ej. deepseek-chat, 300).
    private static int MaxTokens =>
        int.TryParse(
            Environment.GetEnvironmentVariable("OpenRouter__MaxTokens")
            ?? Environment.GetEnvironmentVariable("OPENROUTER_MAX_TOKENS"),
            out var mt) && mt > 0
                ? mt
                : 1200;

    public async Task<string> GenerateResponseAsync(
        List<ChatMessage> messages,
        List<object>? tools = null,
        CancellationToken ct = default)
    {
        var apiKey = GetApiKey();
        var request = CreateRequest(apiKey);

        var body = new
        {
            model = Model,
            max_tokens = MaxTokens,
            messages = messages.Select(m => new
            {
                role = m.Role,
                content = m.Content
            }).ToList()
        };

        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[OpenRouter] Error en API: {StatusCode} - {Response}", response.StatusCode, json);
            throw new Exception($"OpenRouter API error: {response.StatusCode}");
        }

        var data = JsonSerializer.Deserialize<OpenRouterChatResponse>(json);
        return data?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
    }

    public async Task<AiToolCallResult> GenerateResponseWithToolsAsync(
        List<ChatMessage> messages,
        List<object> tools,
        CancellationToken ct = default)
    {
        var apiKey = GetApiKey();
        var request = CreateRequest(apiKey);

        var body = new
        {
            model = Model,
            max_tokens = MaxTokens,
            messages = messages.Select(MapOpenAiMessage).ToList(),
            tools = tools,
            tool_choice = "auto"
        };

        request.Content = new StringContent(
            JsonSerializer.Serialize(body, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[OpenRouter] Error en API: {StatusCode} - {Response}", response.StatusCode, json);
            return new AiToolCallResult { Success = false, TextContent = $"Error: {response.StatusCode}" };
        }

        var data = JsonSerializer.Deserialize<OpenRouterChatResponse>(json);
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

    /// <summary>
    /// Serializa un ChatMessage al formato OpenAI-compatible (igual que OpenAI — OpenRouter
    /// acepta el mismo schema). Rol "tool" con tool_call_id/name; assistant con ToolCalls
    /// reenvía el array tool_calls.
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

    private static string GetApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("OpenRouter__ApiKey")
                     ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            if (envKey.Contains("xxx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "OpenRouter API Key parece ser un placeholder (contiene 'xxx'). Configurar una key real en OpenRouter__ApiKey");
            return envKey;
        }

        throw new InvalidOperationException("No se encontró OpenRouter API Key. Configurar OpenRouter__ApiKey");
    }

    private HttpRequestMessage CreateRequest(string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        // OpenRouter usa estos headers opcionales para dar el "app slate" (qué modelo/URL pidió
        // cada app). Son buenas prácticas del proveedor pero opcionales para que responda.
        if (!request.Headers.Contains("HTTP-Referer"))
            request.Headers.Add("HTTP-Referer", "https://agenda-api.local");
        if (!request.Headers.Contains("X-Title"))
            request.Headers.Add("X-Title", SiteName);
        return request;
    }

    // Response DTOs (mismo schema que OpenAI)
    private class OpenRouterChatResponse
    {
        [JsonPropertyName("choices")]
        public List<OpenRouterChoice>? Choices { get; set; }
    }

    private class OpenRouterChoice
    {
        [JsonPropertyName("message")]
        public OpenRouterMessage? Message { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private class OpenRouterMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<OpenRouterToolCall>? ToolCalls { get; set; }
    }

    private class OpenRouterToolCall
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public OpenRouterFunction? Function { get; set; }
    }

    private class OpenRouterFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        public string Arguments { get; set; } = string.Empty;
    }
}