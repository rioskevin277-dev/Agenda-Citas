using System.Dynamic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgendaApi.Domain.Ports;

namespace AgendaApi.Infrastructure.AiProviders;

/// <summary>
/// Adaptador para Anthropic Claude API (claude-3-haiku). Usado como fallback cuando OpenAI falla.
/// Mismo patrón que AdamApi. Convierte el formato de tool-calling de Anthropic al formato unificado.
/// </summary>
public class AnthropicProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly string? _fallbackApiKey;
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string Model = "claude-3-haiku-20240307";

    public AnthropicProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("anthropic-api");
        _fallbackApiKey = Environment.GetEnvironmentVariable("Anthropic__ApiKey");
    }

    public async Task<string> GenerateResponseAsync(
        List<ChatMessage> messages,
        List<object>? tools = null,
        CancellationToken ct = default)
    {
        var apiKey = GetApiKey();
        var lastMessage = messages.Last();

        var body = new
        {
            model = Model,
            max_tokens = 500,
            messages = new[]
            {
                new { role = "user", content = lastMessage.Content }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[Anthropic] ERROR: {response.StatusCode} - {json}");
            throw new Exception($"Anthropic API error: {response.StatusCode}");
        }

        var data = JsonSerializer.Deserialize<AnthropicResponse>(json);
        return data?.Content?.FirstOrDefault(c => c.Type == "text")?.Text ?? "";
    }

    public async Task<AiToolCallResult> GenerateResponseWithToolsAsync(
        List<ChatMessage> messages,
        List<object> tools,
        CancellationToken ct = default)
    {
        var apiKey = GetApiKey();

        // Extract system prompt and non-system messages
        string? systemPrompt = null;
        var nonSystemMessages = new List<object>();

        foreach (var msg in messages)
        {
            if (msg.Role == "system")
                systemPrompt = msg.Content;
            else
                nonSystemMessages.Add(new { role = msg.Role, content = msg.Content });
        }

        var bodyObj = new Dictionary<string, object>
        {
            ["model"] = Model,
            ["max_tokens"] = 500,
            ["messages"] = nonSystemMessages,
            ["tools"] = tools
        };

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            bodyObj["system"] = systemPrompt;

        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        request.Content = new StringContent(
            JsonSerializer.Serialize(bodyObj),
            Encoding.UTF8,
            "application/json");

        Console.WriteLine($"[Anthropic] GenerarRespuestaConTools — messages={nonSystemMessages.Count} tools={tools.Count}");

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[Anthropic] ERROR: {response.StatusCode} - {json}");
            return new AiToolCallResult { Success = false, TextContent = $"Error Anthropic: {response.StatusCode}" };
        }

        var data = JsonSerializer.Deserialize<AnthropicResponse>(json);
        if (data?.Content == null)
            return new AiToolCallResult { Success = false, TextContent = "Respuesta vacía de Anthropic" };

        string? textContent = null;
        var toolCalls = new List<ToolCall>();

        foreach (var block in data.Content)
        {
            if (block.Type == "text" && !string.IsNullOrWhiteSpace(block.Text))
            {
                textContent = textContent == null ? block.Text : textContent + "\n" + block.Text;
            }
            else if (block.Type == "tool_use" && block.ToolUse != null)
            {
                toolCalls.Add(new ToolCall
                {
                    Id = block.ToolUse.Id,
                    Name = block.ToolUse.Name,
                    Arguments = JsonSerializer.Serialize(block.ToolUse.Input)
                });
            }
        }

        string finishReason = data.StopReason switch
        {
            "tool_use" => "tool_calls",
            "end_turn" => "stop",
            _ => "stop"
        };

        return new AiToolCallResult
        {
            Success = true,
            TextContent = textContent,
            FinishReason = finishReason,
            ToolCalls = toolCalls
        };
    }

    private string GetApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_fallbackApiKey))
        {
            Console.WriteLine("[Anthropic] Usando API Key de configuración");
            return _fallbackApiKey;
        }

        throw new InvalidOperationException("No se encontró Anthropic API Key. Configurar Anthropic__ApiKey");
    }

    // DTOs
    private class AnthropicResponse
    {
        [JsonPropertyName("content")]
        public List<AnthropicContentBlock>? Content { get; set; }

        [JsonPropertyName("stop_reason")]
        public string? StopReason { get; set; }
    }

    private class AnthropicContentBlock
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("input")]
        public JsonElement? Input { get; set; }

        [JsonPropertyName("tool_use")]
        public AnthropicToolUse? ToolUse { get; set; }
    }

    private class AnthropicToolUse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("input")]
        public JsonElement Input { get; set; }
    }
}
