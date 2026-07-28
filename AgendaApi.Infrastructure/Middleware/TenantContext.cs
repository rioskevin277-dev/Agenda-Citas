using AgendaApi.Domain.Ports;

namespace AgendaApi.Infrastructure.Middleware;

/// <summary>
/// Implementación del contexto de tenant (scoped), mismo patrón que AdamApi.
/// </summary>
public class TenantContext : ITenantContext
{
    private Guid _tenantId;
    private string? _calendarProvider;
    private string? _whatsAppAccessToken;
    private string? _phoneNumberId;
    private bool _isSet;

    public Guid TenantId => _isSet ? _tenantId : throw new InvalidOperationException("Tenant no establecido");
    public string? CalendarProvider => _isSet ? _calendarProvider : throw new InvalidOperationException("Tenant no establecido");
    public string? WhatsAppAccessToken => _isSet ? _whatsAppAccessToken : throw new InvalidOperationException("Tenant no establecido");
    public string? PhoneNumberId => _isSet ? _phoneNumberId : throw new InvalidOperationException("Tenant no establecido");
    public bool IsSet => _isSet;

    public void SetTenant(Guid tenantId, string? calendarProvider, string? whatsAppAccessToken, string? phoneNumberId)
    {
        _tenantId = tenantId;
        _calendarProvider = calendarProvider;
        _whatsAppAccessToken = whatsAppAccessToken;
        _phoneNumberId = phoneNumberId;
        _isSet = true;

        Console.WriteLine($"[TenantContext] Tenant establecido: {tenantId} ({calendarProvider})");
    }
}
