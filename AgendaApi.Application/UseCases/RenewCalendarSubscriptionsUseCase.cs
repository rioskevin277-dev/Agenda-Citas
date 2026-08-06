using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Application.UseCases;

/// <summary>
/// Crea y renueva las suscripciones webhook de los calendarios externos (Google/Microsoft).
/// Una conexión activa sin canal debe suscribirse; las suscripciones expiran (Google ~7 días,
/// MS Graph ~3 días) y hay que renovarlas antes de que expiren. Además, la primera vez que se
/// suscribe una conexión con SyncToken vacío se hace un delta inicial para seedear el token
/// incremental (así el sync push funciona desde el primer cambio).
/// </summary>
public class RenewCalendarSubscriptionsUseCase
{
    private const string CalendarWebhookPath = "/api/v1/webhook/calendar";
    private static readonly TimeSpan RenewalLead = TimeSpan.FromHours(24);

    private readonly ICalendarConnectionRepository _connectionRepo;
    private readonly ICalendarProviderFactory _providerFactory;
    private readonly ILogger<RenewCalendarSubscriptionsUseCase> _logger;

    public RenewCalendarSubscriptionsUseCase(
        ICalendarConnectionRepository connectionRepo,
        ICalendarProviderFactory providerFactory,
        ILogger<RenewCalendarSubscriptionsUseCase> logger)
    {
        _connectionRepo = connectionRepo;
        _providerFactory = providerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Crea/renueva la suscripción webhook de un tenant concreto. Útil para disparar justo
    /// después del OAuth de conexión. Devuelve 1 si se suscribió, 0 si no aplica.
    /// </summary>
    public async Task<int> EnsureSubscriptionAsync(Guid tenantId, string webhookBaseUrl, CancellationToken ct = default)
    {
        var connection = await _connectionRepo.GetByTenantIdAsync(tenantId, ct);
        if (connection?.Activo != true)
            return 0;

        var provider = await _providerFactory.GetProviderAsync(tenantId, ct);
        if (provider == null)
            return 0;

        return await EnsureSubscriptionForConnectionAsync(connection, provider, webhookBaseUrl, ct) ? 1 : 0;
    }

    /// <summary>
    /// Itera todas las conexiones activas y crea o renueva las que lo necesiten.
    /// Un fallo por tenant no rompe el resto.
    /// </summary>
    public async Task<int> RenewAllAsync(string webhookBaseUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(webhookBaseUrl))
        {
            _logger.LogWarning("[RenewSubs] PUBLIC_BASE_URL no configurada, omitiendo renovación de suscripciones");
            return 0;
        }

        var connections = await _connectionRepo.GetAllActiveAsync(ct);
        int renewed = 0;

        foreach (var connection in connections)
        {
            try
            {
                var provider = await _providerFactory.GetProviderAsync(connection.IdTenant, ct);
                if (provider == null)
                    continue;

                if (await EnsureSubscriptionForConnectionAsync(connection, provider, webhookBaseUrl, ct))
                    renewed++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RenewSubs] Error procesando tenant {TenantId}", connection.IdTenant);
            }
        }

        return renewed;
    }

    // ─── Private ────────────────────────────────────────────────

    private async Task<bool> EnsureSubscriptionForConnectionAsync(
        CalendarConnection connection,
        ICalendarProvider provider,
        string webhookBaseUrl,
        CancellationToken ct)
    {
        var tenantId = connection.IdTenant;

        // Crear si no hay canal, o renovar si está por expirar.
        var needsSubscription = string.IsNullOrEmpty(connection.SyncChannelId)
                                || connection.SyncChannelExpiresAt == null
                                || connection.SyncChannelExpiresAt < DateTime.UtcNow.Add(RenewalLead);

        if (needsSubscription)
        {
            await provider.SubscribeToChangesAsync(
                tenantId, BuildNotificationUrl(webhookBaseUrl), ct);
            _logger.LogInformation("[RenewSubs] Tenant {TenantId} suscrito/renovado", tenantId);
        }

        // Seedear el delta token la primera vez (chicken-egg) para que el sync funcione.
        if (string.IsNullOrEmpty(connection.SyncToken))
        {
            try
            {
                await provider.GetChangesAsync(tenantId, string.Empty, ct);
                _logger.LogInformation("[RenewSubs] SyncToken inicial seedeado para tenant {TenantId}", tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RenewSubs] No se pudo seedear SyncToken del tenant {TenantId}", tenantId);
            }
        }

        return needsSubscription;
    }

    private static string BuildNotificationUrl(string webhookBaseUrl)
        => webhookBaseUrl.TrimEnd('/') + CalendarWebhookPath;
}