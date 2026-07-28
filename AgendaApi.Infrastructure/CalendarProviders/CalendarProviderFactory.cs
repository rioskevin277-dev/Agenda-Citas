using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace AgendaApi.Infrastructure.CalendarProviders;

/// <summary>
/// Fábrica que resuelve qué adaptador de calendario usar según el tenant.
/// Patrón Factory + Strategy.
/// </summary>
public class CalendarProviderFactory : ICalendarProviderFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ITenantRepository _tenantRepo;
    private readonly Dictionary<string, Type> _providers;

    public CalendarProviderFactory(
        IServiceProvider serviceProvider,
        ITenantRepository tenantRepo)
    {
        _serviceProvider = serviceProvider;
        _tenantRepo = tenantRepo;
        _providers = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            { "google", typeof(GoogleCalendarAdapter) },
            { "microsoft", typeof(MicrosoftGraphCalendarAdapter) }
        };
    }

    public async Task<ICalendarProvider?> GetProviderAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await _tenantRepo.GetByIdAsync(tenantId, ct);
        if (tenant == null || !tenant.Activo)
            return null;

        return GetProviderByName(tenant.CalendarProvider);
    }

    public ICalendarProvider? GetProviderByName(string providerName)
    {
        if (string.IsNullOrEmpty(providerName))
            return null;

        if (_providers.TryGetValue(providerName, out var type))
        {
            return _serviceProvider.GetRequiredService(type) as ICalendarProvider;
        }

        return null;
    }
}
