using AgendaApi.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Application.Services;

/// <summary>
/// Background service que renueva periódicamente las suscripciones webhook de los
/// calendarios externos (Google/Microsoft). Las suscripciones expiran (Google ~7 días,
/// MS Graph ~3 días), así que cada hora se revisan y se crean/renuevan las que falten.
/// También seedea el delta token inicial de las conexiones recién creadas.
/// </summary>
public class SubscriptionRenewalService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SubscriptionRenewalService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    public SubscriptionRenewalService(
        IServiceScopeFactory scopeFactory,
        ILogger<SubscriptionRenewalService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[RenewSubs] Servicio de renovación de suscripciones iniciado");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
                if (stoppingToken.IsCancellationRequested) break;

                using var scope = _scopeFactory.CreateScope();
                var useCase = scope.ServiceProvider.GetRequiredService<RenewCalendarSubscriptionsUseCase>();
                var webhookBaseUrl = Environment.GetEnvironmentVariable("PUBLIC_BASE_URL") ?? string.Empty;
                var renewed = await useCase.RenewAllAsync(webhookBaseUrl, stoppingToken);
                if (renewed > 0)
                {
                    _logger.LogInformation("[RenewSubs] Suscripciones renovadas/creadas: {Count}", renewed);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RenewSubs] Error en ciclo de renovación");
            }
        }
    }
}
