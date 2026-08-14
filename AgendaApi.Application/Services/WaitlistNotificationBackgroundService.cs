using AgendaApi.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Application.Services;

/// <summary>
/// Background service que detecta cupos liberados y notifica a la lista de espera (FIFO).
/// Barrido periódico (cada 5 min) con la misma estructura que ReminderBackgroundService.
/// </summary>
public class WaitlistNotificationBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WaitlistNotificationBackgroundService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    public WaitlistNotificationBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<WaitlistNotificationBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[WaitlistBG] Servicio de lista de espera iniciado");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
                if (stoppingToken.IsCancellationRequested) break;

                using var scope = _scopeFactory.CreateScope();
                var useCase = scope.ServiceProvider.GetRequiredService<WaitlistNotificationUseCase>();
                var notified = await useCase.ExecuteAsync(stoppingToken);
                if (notified > 0)
                {
                    _logger.LogInformation("[WaitlistBG] Cupos liberados notificados: {Count}", notified);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WaitlistBG] Error en ciclo de lista de espera");
            }
        }
    }
}