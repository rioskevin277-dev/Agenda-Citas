namespace AgendaApi.Domain.Ports;

/// <summary>
/// Puerto para el proveedor de IA (OpenAI, Anthropic, etc.)
/// que se usa para entender lenguaje natural en la conversación.
/// </summary>
public interface IAiProvider
{
    Task<string> GenerateResponseAsync(
        List<ChatMessage> messages,
        List<object>? tools = null,
        CancellationToken ct = default);

    Task<AiToolCallResult> GenerateResponseWithToolsAsync(
        List<ChatMessage> messages,
        List<object> tools,
        CancellationToken ct = default);
}

public class ChatMessage
{
    public string Role { get; set; } = string.Empty; // "system", "user", "assistant", "tool"
    public string Content { get; set; } = string.Empty;
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public string? ToolArguments { get; set; }
}

public class AiToolCallResult
{
    public string? TextContent { get; set; }
    public List<ToolCall> ToolCalls { get; set; } = new();
    public string FinishReason { get; set; } = "stop";
    public bool Success { get; set; }
}

public class ToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}
