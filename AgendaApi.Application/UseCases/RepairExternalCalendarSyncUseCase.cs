using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Application.UseCases;

/// <summary>
/// Repara la sincronización local → externa: recrea en el calendario externo el evento
/// faltante (ExternalEventId == null) de citas futuras no canceladas. Corre como job de
/// background porque los adaptadores de calendario se auto-autentican (resuelven su token
/// de la conexión, no dependen de ITenantContext como el envío de WhatsApp).
/// </summary>
public class RepairExternalCalendarSyncUseCase
{
    private readonly IAppointmentRepository _appointmentRepo;
    private readonly ICalendarConnectionRepository _connectionRepo;
    private readonly ICalendarProviderFactory _providerFactory;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<RepairExternalCalendarSyncUseCase> _logger;

    public RepairExternalCalendarSyncUseCase(
        IAppointmentRepository appointmentRepo,
        ICalendarConnectionRepository connectionRepo,
        ICalendarProviderFactory providerFactory,
        IUnitOfWork unitOfWork,
        ILogger<RepairExternalCalendarSyncUseCase> logger)
    {
        _appointmentRepo = appointmentRepo;
        _connectionRepo = connectionRepo;
        _providerFactory = providerFactory;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        var missing = await _appointmentRepo.GetMissingExternalEventsAsync(ct);
        int repaired = 0;

        // Se agrupa por tenant para resolver el proveedor de calendario una sola vez.
        foreach (var group in missing.GroupBy(a => a.IdTenant))
        {
            var tenantId = group.Key;
            var connection = await _connectionRepo.GetByTenantIdAsync(tenantId, ct);
            if (connection?.Activo != true)
                continue;

            ICalendarProvider? provider = null;
            try
            {
                provider = await _providerFactory.GetProviderAsync(tenantId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RepairSync] No se pudo resolver el proveedor de {Tenant}", tenantId);
            }

            if (provider == null)
                continue;

            foreach (var appointment in group)
            {
                try
                {
                    var externalId = await provider.CreateEventAsync(appointment, ct);
                    appointment.ExternalEventId = externalId;
                    appointment.FechaActualizacion = DateTime.UtcNow;
                    await _appointmentRepo.UpdateAsync(appointment, ct);
                    repaired++;
                    _logger.LogInformation("[RepairSync] Cita {AppointmentId} reparada (evento {EventId})",
                        appointment.IdAppointment, externalId);
                }
                catch (Exception ex)
                {
                    // Una cita fallida no debe abortar el resto (token vencido, red, etc.)
                    _logger.LogWarning(ex, "[RepairSync] Error reparando cita {AppointmentId} del tenant {Tenant}",
                        appointment.IdAppointment, tenantId);
                }
            }
        }

        if (repaired > 0)
            await _unitOfWork.SaveChangesAsync(ct);

        return repaired;
    }
}