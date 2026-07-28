using AgendaApi.Domain.Entities;

namespace AgendaApi.Domain.Ports;

/// <summary>
/// Puerto para el proveedor de calendario externo.
/// Cada adaptador (Google, Microsoft) implementa esta interfaz.
/// </summary>
public interface ICalendarProvider
{
    string ProviderName { get; }

    /// <summary>
    /// Obtiene los slots ocupados en un rango de fechas desde el calendario externo.
    /// </summary>
    Task<List<TimeSlot>> GetAvailabilityAsync(
        Guid tenantId,
        DateOnly fechaInicio,
        DateOnly fechaFin,
        CancellationToken ct = default);

    /// <summary>
    /// Crea un evento en el calendario externo y devuelve el ID del evento.
    /// </summary>
    Task<string> CreateEventAsync(
        Appointment appointment,
        CancellationToken ct = default);

    /// <summary>
    /// Actualiza un evento existente en el calendario externo.
    /// </summary>
    Task UpdateEventAsync(
        Appointment appointment,
        CancellationToken ct = default);

    /// <summary>
    /// Cancela/elimina un evento del calendario externo.
    /// </summary>
    Task CancelEventAsync(
        Guid tenantId,
        string externalEventId,
        string? motivo,
        CancellationToken ct = default);

    /// <summary>
    /// Obtiene cambios ocurridos en el calendario externo desde la última sincronización
    /// usando el syncToken almacenado en CalendarConnection.
    /// </summary>
    Task<List<ExternalCalendarChange>> GetChangesAsync(
        Guid tenantId,
        string syncToken,
        CancellationToken ct = default);

    /// <summary>
    /// Suscribe a notificaciones de cambios en el calendario (webhook/push).
    /// Almacena el channel ID y resource ID en CalendarConnection.
    /// Devuelve el channel ID y su expiración.
    /// </summary>
    Task<(string ChannelId, string? ResourceId, DateTime ExpiresAt)> SubscribeToChangesAsync(
        Guid tenantId,
        string webhookUrl,
        CancellationToken ct = default);

    /// <summary>
    /// Renueva el token de acceso OAuth cuando expira.
    /// </summary>
    Task<string> RefreshAccessTokenAsync(
        Guid tenantId,
        string refreshToken,
        CancellationToken ct = default);
}

/// <summary>
/// Slot de tiempo en el calendario externo.
/// </summary>
public class TimeSlot
{
    public DateTime FechaInicio { get; set; }
    public DateTime FechaFin { get; set; }
    public bool Disponible { get; set; } = true;
    public string? ExternalEventId { get; set; }
}

/// <summary>
/// Cambio detectado en el calendario externo.
/// </summary>
public class ExternalCalendarChange
{
    public string ExternalEventId { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty; // "created", "updated", "deleted"
    public DateTime? FechaInicio { get; set; }
    public DateTime? FechaFin { get; set; }
    public string? Summary { get; set; }
}
