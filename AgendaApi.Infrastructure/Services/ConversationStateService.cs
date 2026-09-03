using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Infrastructure.Services;

/// <summary>
/// Estado estructurado por conversación (misma clave tenantId:phone que la memoria de
/// conversación), separado del historial. En memoria con expiración de 24h. Guarda:
/// - la reserva que el cliente dejó a medio agendar (para retomarla, P3)
/// </summary>
public class ConversationStateService
{
    private readonly ConcurrentDictionary<string, Entry> _store = new();
    private readonly ILogger<ConversationStateService> _logger;
    private static readonly TimeSpan Expiry = TimeSpan.FromHours(24);

    // Dirty flag por-tenant (RF3): el webhook de cancelación externa lo marca para que el
    // orquestador fuerce un re-check de disponibilidad en el siguiente turno, aun sin
    // PendingBooking ni pedido de fecha/hora. Volátil: se pierde en restart, pero el re-check
    // determinístico (RF1) protege la corrección (solo se pierde agresividad). One-shot por
    // consumo. Key = tenantId (el webhook fire-and-forget solo conoce tenantId, no el phone).
    private readonly ConcurrentDictionary<Guid, DateTime> _tenantDirty = new();

    public ConversationStateService(ILogger<ConversationStateService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Marca al tenant como "sucio" para forzar un re-check de disponibilidad en su próximo turno.
    /// Idempotente: marcar varias veces mantiene el flag hasta que se consume una vez.
    /// </summary>
    public void MarkTenantDirty(Guid tenantId)
        => _tenantDirty[tenantId] = DateTime.UtcNow;

    /// <summary>
    /// Consume (one-shot) el flag de suciedad del tenant. Devuelve true y limpia el flag si estaba
    /// marcado; false si nunca se marcó o ya se consumió.
    /// </summary>
    public bool ConsumeTenantDirty(Guid tenantId)
        => _tenantDirty.TryRemove(tenantId, out _);

    public static string GetKey(Guid tenantId, string userPhone)
        => ConversationMemoryService.GetKey(tenantId, userPhone);

    public PendingBooking? GetPendingBooking(string key)
        => GetFresh(key)?.PendingBooking;

    public void SetPendingBooking(string key, PendingBooking? booking)
    {
        var entry = GetFresh(key) ?? new Entry();
        entry.PendingBooking = booking;
        Store(key, entry);
    }

    private Entry? GetFresh(string key)
    {
        if (!_store.TryGetValue(key, out var entry)) return null;
        if (DateTime.UtcNow - entry.LastActivity > Expiry)
        {
            _store.TryRemove(key, out _);
            _logger.LogInformation("[ConvState] Estado de {Key} expirado por inactividad", key);
            return null;
        }
        return entry;
    }

    private void Store(string key, Entry entry)
    {
        entry.LastActivity = DateTime.UtcNow;
        _store[key] = entry;
    }

    private class Entry
    {
        public PendingBooking? PendingBooking { get; set; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    }
}

/// <summary>Reserva que un cliente dejó a medio agendar, para retomarla en el siguiente turno.</summary>
public record PendingBooking(string? ServiceTypeName, string? ProfessionalName, DateOnly? Fecha);