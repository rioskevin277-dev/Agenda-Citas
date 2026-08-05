using System.Collections.Concurrent;
using System.Threading.Channels;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Infrastructure.Services;

/// <summary>
/// Buffer de mensajes entrantes con Channel. Mismo patrón que AdamApi.
///
/// - Cada usuario tiene un buffer individual de 30s
/// - Deduplicación por ExternalMessageId
/// - Rate-limit: máx 5 mensajes cada 15s por usuario
/// - Timers de limpieza para buffers expirados
/// - Los mensajes se encolan y procesan en lotes por usuario
/// </summary>
public class MessageBufferService : BackgroundService
{
    private readonly Channel<IncomingMessageEvent> _channel;
    private readonly ConcurrentDictionary<string, UserMessageBuffer> _userBuffers;
    private readonly ConcurrentDictionary<string, List<DateTime>> _userRateLimits;
    private readonly ILogger<MessageBufferService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private const int FlushDelayMs = 30_000; // 30s por buffer de usuario
    private const int MaxMessagesPerWindow = 5;
    private const int RateLimitWindowMs = 15_000; // 15s
    private const int CleanupIntervalMs = 60_000; // limpiar buffers viejos cada 60s

    public MessageBufferService(
        ILogger<MessageBufferService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _channel = Channel.CreateBounded<IncomingMessageEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _userBuffers = new ConcurrentDictionary<string, UserMessageBuffer>();
        _userRateLimits = new ConcurrentDictionary<string, List<DateTime>>();
    }

    /// <summary>
    /// Encola un mensaje entrante en el Channel.
    /// </summary>
    public async Task EnqueueMessageAsync(
        string externalMessageId,
        string from,
        string? fromName,
        string content,
        string? mediaType,
        string? mediaUrl,
        Guid tenantId,
        CancellationToken ct = default)
    {
        var evt = new IncomingMessageEvent
        {
            ExternalMessageId = externalMessageId,
            From = from,
            FromName = fromName,
            Content = content,
            MediaType = mediaType,
            MediaUrl = mediaUrl,
            TenantId = tenantId,
            ReceivedAt = DateTime.UtcNow
        };

        await _channel.Writer.WriteAsync(evt, ct);
        _logger.LogDebug("[Buffer] Encolado mensaje {Id} de {From}", externalMessageId, from);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Buffer] MessageBufferService iniciado");

        // Timer de limpieza periódica
        using var cleanupTimer = new Timer(
            CleanupBuffers,
            null,
            CleanupIntervalMs,
            CleanupIntervalMs);

        // Reader loop
        var readerTask = ProcessMessagesAsync(stoppingToken);

        try
        {
            await readerTask;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Buffer] MessageBufferService detenido");
        }
    }

    private async Task ProcessMessagesAsync(CancellationToken ct)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                // 1. Deduplicación
                if (IsDuplicate(evt.ExternalMessageId))
                {
                    _logger.LogWarning("[Buffer] Mensaje duplicado ignorado: {Id}", evt.ExternalMessageId);
                    continue;
                }

                // 2. Rate-limit por usuario
                if (IsRateLimited(evt.From))
                {
                    _logger.LogWarning("[Buffer] Rate-limit alcanzado para {From}", evt.From);
                    continue;
                }

                // 3. Acumular en buffer del usuario (o crear uno nuevo)
                var buffer = _userBuffers.GetOrAdd(evt.From, _ =>
                {
                    var b = new UserMessageBuffer(evt.From);
                    b.FlushTimer = new Timer(
                        async _ => await FlushUserBufferAsync(evt.From, ct),
                        null,
                        FlushDelayMs,
                        Timeout.Infinite);
                    return b;
                });

                buffer.Messages.Add(evt);
                buffer.LastActivityAt = DateTime.UtcNow;

                _logger.LogDebug("[Buffer] Buffer de {From}: {Count} mensajes acumulados",
                    evt.From, buffer.Messages.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Buffer] Error procesando mensaje {Id}", evt.ExternalMessageId);
            }
        }
    }

    /// <summary>
    /// Procesa el buffer de un usuario: envía todos los mensajes acumulados al ChatOrchestrator.
    /// </summary>
    private async Task FlushUserBufferAsync(string from, CancellationToken ct)
    {
        try
        {
            if (!_userBuffers.TryRemove(from, out var buffer))
                return;

            buffer.FlushTimer?.Dispose();

            if (buffer.Messages.Count == 0)
                return;

            _logger.LogInformation("[Buffer] Flushing {Count} mensajes para {From}",
                buffer.Messages.Count, from);

            // Crear scope para resolver servicios scoped
            using var scope = _scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ChatOrchestratorService>();

            // Concatenar mensajes del buffer en orden
            var fullContent = string.Join("\n", buffer.Messages
                .OrderBy(m => m.ReceivedAt)
                .Select(m => m.Content));

            var lastMsg = buffer.Messages.Last();
            var tenantId = lastMsg.TenantId;
            var clientName = lastMsg.FromName;

            await orchestrator.ProcessMessageAsync(
                from,
                fullContent,
                tenantId,
                ct,
                clientName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Buffer] Error en flush para {From}", from);
        }
    }

    private bool IsDuplicate(string externalMessageId)
    {
        // Evitar re-procesar mensajes ya vistos
        return _userBuffers.Values.Any(b =>
            b.Messages.Any(m => m.ExternalMessageId == externalMessageId));
    }

    private bool IsRateLimited(string from)
    {
        var now = DateTime.UtcNow;
        var timestamps = _userRateLimits.GetOrAdd(from, _ => new List<DateTime>());

        lock (timestamps)
        {
            // Limpiar timestamps fuera de la ventana
            timestamps.RemoveAll(t => (now - t).TotalMilliseconds >= RateLimitWindowMs);

            if (timestamps.Count >= MaxMessagesPerWindow)
                return true;

            timestamps.Add(now);
        }

        return false;
    }

    private void CleanupBuffers(object? state)
    {
        var threshold = DateTime.UtcNow.AddMilliseconds(-FlushDelayMs * 2);
        foreach (var kvp in _userBuffers)
        {
            if (kvp.Value.LastActivityAt < threshold)
            {
                _logger.LogInformation("[Buffer] Limpieza: buffer huérfano de {User}", kvp.Key);
                if (_userBuffers.TryRemove(kvp.Key, out var buffer))
                {
                    buffer.FlushTimer?.Dispose();
                }
            }
        }
    }

    private class IncomingMessageEvent
    {
        public string ExternalMessageId { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string? FromName { get; set; }
        public string Content { get; set; } = string.Empty;
        public string? MediaType { get; set; }
        public string? MediaUrl { get; set; }
        public Guid TenantId { get; set; }
        public DateTime ReceivedAt { get; set; }
    }

    private class UserMessageBuffer
    {
        public string UserId { get; }
        public List<IncomingMessageEvent> Messages { get; } = new();
        public Timer? FlushTimer { get; set; }
        public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

        public UserMessageBuffer(string userId)
        {
            UserId = userId;
        }
    }
}
