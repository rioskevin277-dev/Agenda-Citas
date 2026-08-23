using System.Collections.Concurrent;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Infrastructure.Services;

/// <summary>
/// Memoria de conversación en memoria, clave por tenant + teléfono del cliente.
/// Guarda la transcripción (user/assistant) para que el asistente conserve contexto
/// entre mensajes de WhatsApp, que por diseño llegan como peticiones independientes.
/// Incluye expiración (24h) y tope de mensajes retenidos.
/// </summary>
public class ConversationMemoryService : IConversationSessionService
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _store = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastActivity = new();
    private readonly ILogger<ConversationMemoryService> _logger;

    // Cuántas entradas de historial se devuelven al modelo en cada turno.
    private const int MaxHistoryMessages = 20;
    // Cuántas entradas se retienen en memoria por conAversación (mayor que lo enviado al modelo).
    private const int MaxRetainedMessages = 60;
    private static readonly TimeSpan Expiry = TimeSpan.FromHours(24);

    public ConversationMemoryService(ILogger<ConversationMemoryService> logger)
    {
        _logger = logger;
    }

    public static string GetKey(Guid tenantId, string identity)
        => $"{tenantId:N}:{NormalizeIdentity(identity)}";

    /// <summary>
    /// Normaliza la identidad para la clave de conversación. Para teléfonos legacy se mantiene el
    /// digit-normalize (tolera formatos/espacios). Para BSUID (contiene un punto, ej "US.123…") NO
    /// se quitan los no-dígitos: hacerlo colisionaría "US.1349…" con un teléfono "1349…".
    /// </summary>
    private static string NormalizeIdentity(string identity)
        => identity.Contains('.') ? identity : new string(identity.Where(char.IsDigit).ToArray());

    /// <summary>
    /// Devuelve system + últimas entradas user/assistant del historial, listos para el modelo.
    /// </summary>
    public List<ChatMessage> GetHistory(string key, string systemPrompt)
    {
        if (!_store.TryGetValue(key, out var history))
            return FreshHistory(systemPrompt);

        // Expiración: si pasó demasiado tiempo sin actividad, se descarta.
        if (_lastActivity.TryGetValue(key, out var last) && DateTime.UtcNow - last > Expiry)
        {
            _logger.LogInformation("[Memoria] Conversación {Key} expirada por inactividad", key);
            _store.TryRemove(key, out _);
            _lastActivity.TryRemove(key, out _);
            return FreshHistory(systemPrompt);
        }

        List<ChatMessage> result;
        lock (history)
        {
            result = history.TakeLast(MaxHistoryMessages).ToList();
        }

        var full = new List<ChatMessage> { new() { Role = "system", Content = systemPrompt } };
        full.AddRange(result);
        return full;
    }

    public void AddUser(string key, string content)
        => Append(key, new ChatMessage { Role = "user", Content = content });

    public void AddAssistant(string key, string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        Append(key, new ChatMessage { Role = "assistant", Content = content });
    }

    private void Append(string key, ChatMessage msg)
    {
        var history = _store.GetOrAdd(key, _ => new List<ChatMessage>());
        lock (history)
        {
            history.Add(msg);
            if (history.Count > MaxRetainedMessages)
                history.RemoveRange(0, history.Count - MaxRetainedMessages);
        }
        _lastActivity[key] = DateTime.UtcNow;
    }

    private static List<ChatMessage> FreshHistory(string systemPrompt)
        => new() { new() { Role = "system", Content = systemPrompt } };

    /// <summary>
    /// ¿El cliente tiene una sesión de WhatsApp activa (actividad en las últimas 24h)?
    /// Determina si un mensaje de texto libre puede enviarse o se necesita un template aprobado.
    /// </summary>
    public bool HasActiveSession(Guid tenantId, string userPhone)
    {
        var key = GetKey(tenantId, userPhone);
        return _lastActivity.TryGetValue(key, out var last) && DateTime.UtcNow - last <= Expiry;
    }
}