using AgendaApi.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Application.Services;

/// <summary>
/// Background service que expira handoffs abandonados por inactividad. Barrido periódico
/// (cada 5 min) con la misma estructura que ReminderBackgroundService / WaitlistNotificationBackgroundService.
/// Sin esto, un ticket de handoff abierto y nunca cerrado (p. ej. de una prueba) congelaría al
/// AI permanentemente: el GATE de ChatOrchestratorService deja de responder mientras haya un handoff abierto.
/// </summary>
public class HandoffExpirationBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HandoffExpirationBackgroundService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    public HandoffExpirationBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<HandoffExpirationBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[HandoffBG] Servicio de expiración de handoffs iniciado");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
                if (stoppingToken.IsCancellationRequested) break;

                using var scope = _scopeFactory.CreateScope();
                var useCase = scope.ServiceProvider.GetRequiredService<HandoffExpirationUseCase>();
                var expired = await useCase.ExpireAsync(stoppingToken);
                if (expired > 0)
                {
                    _logger.LogInformation("[HandoffBG] Handoffs expirados por inactividad: {Count}", expired);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HandoffBG] Error en ciclo de expiración de handoffs");
            }
        }
    }
}