using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Infrastructure.AiProviders;

/// <summary>
/// Adaptador para OpenAI API (GPT-4o-mini). Mismo patrón que AdamApi.
/// Usa API Key del tenant o fallback global.
/// </summary>
public class OpenAIProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<OpenAIProvider> _logger;
    private const string ApiUrl = "https://api.openai.com/v1/chat/completions";
    private const string Model = "gpt-4o-mini";

    public OpenAIProvider(
        IHttpClientFactory httpClientFactory,
        ITenantContext tenantContext,
        ILogger<OpenAIProvider> logger)
    {
        _httpClient = httpClientFactory.CreateClient("openai-api");
        _tenantContext = tenantContext;
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
            _logger.LogError("[OpenAI] Error en API: {StatusCode} - {Response}", response.StatusCode, json);
            throw new Exception($"OpenAI API error: {response.StatusCode}");
        }

        var data = JsonSerializer.Deserialize<OpenAiChatResponse>(json);
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
            _logger.LogError("[OpenAI] Error en API: {StatusCode} - {Response}", response.StatusCode, json);
            return new AiToolCallResult { Success = false, TextContent = $"Error: {response.StatusCode}" };
        }

        var data = JsonSerializer.Deserialize<OpenAiChatResponse>(json);
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
    /// Serializa un ChatMessage al formato OpenAI-compatible.
    /// - Rol "tool": incluye tool_call_id y name.
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

    private string GetApiKey()
    {
        if (_tenantContext.IsSet && !string.IsNullOrWhiteSpace(_tenantContext.WhatsAppAccessToken))
        {
            // Reusing the pattern: tenant stores OpenAI key alongside WhatsApp config
            // In AgendaApi, we use a dedicated field; for now use WhatsAppAccessToken as API key
            // or fetch from tenant-specific config
            // TODO: Store OpenAI key per tenant properly
            _logger.LogWarning("[OpenAI] Tenant {TenantId} no tiene API key propia, usando global", _tenantContext.TenantId);
        }

        // Fallback: read from config (set via environment variable or appsettings)
        var envKey = Environment.GetEnvironmentVariable("OpenAI__ApiKey");
        if (!string.IsNullOrWhiteSpace(envKey))
            return envKey;

        throw new InvalidOperationException("No se encontró OpenAI API Key. Configurar OpenAI__ApiKey");
    }

    private HttpRequestMessage CreateRequest(string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    // Response DTOs
    private class OpenAiChatResponse
    {
        [JsonPropertyName("choices")]
        public List<OpenAiChoice>? Choices { get; set; }
    }

    private class OpenAiChoice
    {
        [JsonPropertyName("message")]
        public OpenAiMessage? Message { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private class OpenAiMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<OpenAiToolCall>? ToolCalls { get; set; }
    }

    private class OpenAiToolCall
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public OpenAiFunction? Function { get; set; }
    }

    private class OpenAiFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        public string Arguments { get; set; } = string.Empty;
    }
}
