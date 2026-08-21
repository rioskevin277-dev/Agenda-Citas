using System.Collections.Concurrent;

namespace AgendaApi.Infrastructure.Services;

/// <summary>
/// Cola de reintentos con backoff, misma política que el <c>MessageRetryService</c> de AdamApi:
/// backoff 30s / 2m / 8m, máximo 3 reintentos y expiración total de 30 min.
///
/// No realiza I/O: solo programa y entrega los candidatos vencidos. El ejecutor (quién
/// vuelve a procesar el mensaje) lo aporta el <see cref="MessageBufferService"/>. Al desacoplarlo
/// se puede probar la política de backoff en aislamiento y se conserva la arquitectura por
/// capas del proyecto base.
///
/// Semántica de <c>Attempt</c>: el flush inicial es el intento 1; cada reintento es un intento
/// nuevo (2, 3, 4...). <c>failedAttempts</c> = cuántos intentos ya fallaron antes de agendar.
/// Se deja de reintentar cuando <c>failedAttempts &gt; MaxRetries</c>.
/// </summary>
public sealed class MessageRetryService
{
    private readonly ConcurrentDictionary<string, RetryItem> _pending = new();

    /// <summary>Máximo de reintentos tras el flush inicial (no cuenta el intento inicial).</summary>
    public int MaxRetries { get; }

    /// <summary>Ventana total de vida del mensaje; más allá se descarta aunque le quede backoff.</summary>
    public TimeSpan TotalExpiration { get; }

    // Backoff por intento fallido ya ocurrido: 1º→30s, 2º→2m, 3º→8m. Mismo esquema que AdamApi.
    private static readonly int[] BackoffDelaysMs = { 30_000, 120_000, 480_000 };

    public MessageRetryService(int maxRetries = 3, TimeSpan? totalExpiration = null)
    {
        MaxRetries = maxRetries;
        TotalExpiration = totalExpiration ?? TimeSpan.FromMinutes(30);
    }

    /// <summary>
    /// Programa un reintento para <paramref name="key"/> (típicamente el número de WhatsApp).
    /// Devuelve true si quedó programado; false si se agotaron los reintentos (el llamador debe
    /// loguearlo: el mensaje ya no se podrá atender).
    /// </summary>
    public bool Schedule(
        string key,
        string content,
        Guid tenantId,
        string? clientName,
        DateTime receivedAt,
        DateTime nowUtc,
        int failedAttempts)
    {
        // failedAttempts es >= 1 aquí (alguien ya falló). > MaxRetries ⇒ se acabó el margen.
        if (failedAttempts <= 0 || failedAttempts > MaxRetries)
            return false;

        var delayMs = BackoffDelaysMs[Math.Min(failedAttempts - 1, BackoffDelaysMs.Length - 1)];
        _pending[key] = new RetryItem
        {
            Key = key,
            Content = content,
            TenantId = tenantId,
            ClientName = clientName,
            // El intento que va a ejecutarse es el siguiente al que ya falló.
            Attempt = failedAttempts + 1,
            DueAt = nowUtc.AddMilliseconds(delayMs),
            ReceivedAt = receivedAt
        };
        return true;
    }

    /// <summary>
    /// Entrega (y elimina) los reintentos cuyo backoff ya venció. Los que excedieron la
    /// ventana total quedan marcados como <see cref="RetryItem.Expired"/>: el llamador debe
    /// descartarlos y loguearlo, no ejecutarlos.
    /// </summary>
    public List<RetryItem> CollectDue(DateTime nowUtc)
    {
        var due = new List<RetryItem>();
        foreach (var kvp in _pending)
        {
            if (nowUtc < kvp.Value.DueAt)
                continue;

            if (_pending.TryRemove(kvp.Key, out var item))
            {
                item.Expired = nowUtc - item.ReceivedAt > TotalExpiration;
                due.Add(item);
            }
        }
        return due;
    }

    /// <summary>Intentos pendientes (para diagnóstico / tests).</summary>
    public ICollection<RetryItem> Pending => _pending.Values;

    public sealed class RetryItem
    {
        public string Key { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public Guid TenantId { get; set; }
        public string? ClientName { get; set; }
        public int Attempt { get; set; }
        public DateTime DueAt { get; set; }
        public DateTime ReceivedAt { get; set; }

        /// <summary>true ⇒ venció el plazo total de 30 min: ya no debe ejecutarse.</summary>
        public bool Expired { get; set; }
    }
}