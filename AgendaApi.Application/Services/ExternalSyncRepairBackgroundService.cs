using AgendaApi.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Application.Services;

/// <summary>
/// Background service que recrea periódicamente los eventos externos faltantes de citas
/// futuras no canceladas (reparación local → externo). Corre cada 5 minutos como los
/// recordatorios; es idempotente porque solo procesa citas con ExternalEventId == null.
/// </summary>
public class ExternalSyncRepairBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExternalSyncRepairBackgroundService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    public ExternalSyncRepairBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<ExternalSyncRepairBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[RepairSyncBG] Job de reparación de sincronización iniciado");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
                if (stoppingToken.IsCancellationRequested) break;

                using var scope = _scopeFactory.CreateScope();
                var useCase = scope.ServiceProvider.GetRequiredService<RepairExternalCalendarSyncUseCase>();
                var repaired = await useCase.ExecuteAsync(stoppingToken);
                if (repaired > 0)
                {
                    _logger.LogInformation("[RepairSyncBG] Eventos externos recreados: {Count}", repaired);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RepairSyncBG] Error en ciclo de reparación de sincronización");
            }
        }
    }
}