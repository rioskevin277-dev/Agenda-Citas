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
public class GroqProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GroqProvider> _logger;
    private const string ApiUrl = "https://api.groq.com/openai/v1/chat/completions";
    private const string Model = "llama-3.3-70b-versatile";

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
        var request = CreateRequest(apiKey);

        var body = new
        {
            model = Model,
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
            _logger.LogError("[Groq] Error en API: {StatusCode} - {Response}", response.StatusCode, json);
            throw new Exception($"Groq API error: {response.StatusCode}");
        }

        var data = JsonSerializer.Deserialize<GroqChatResponse>(json);
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
            messages = messages.Select(m => new
            {
                role = m.Role,
                content = m.Content,
                tool_call_id = m.Role == "tool" ? m.ToolCallId : null,
                name = m.Role == "tool" ? m.ToolName : null
            }).ToList(),
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
            _logger.LogError("[Groq] Error en API: {StatusCode} - {Response}", response.StatusCode, json);
            return new AiToolCallResult { Success = false, TextContent = $"Error: {response.StatusCode}" };
        }

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