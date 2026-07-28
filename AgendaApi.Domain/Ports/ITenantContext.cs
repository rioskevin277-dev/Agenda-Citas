namespace AgendaApi.Domain.Ports;

/// <summary>
/// Puerto para el contexto de tenant activo durante la ejecución de un request.
/// </summary>
public interface ITenantContext
{
    Guid TenantId { get; }
    string? CalendarProvider { get; }
    string? WhatsAppAccessToken { get; }
    string? PhoneNumberId { get; }
    bool IsSet { get; }

    void SetTenant(Guid tenantId, string? calendarProvider, string? whatsAppAccessToken, string? phoneNumberId);
}
