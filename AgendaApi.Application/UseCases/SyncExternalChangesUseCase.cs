using AgendaApi.Domain.Ports;

namespace AgendaApi.Application.UseCases;

/// <summary>
/// Caso de uso: Sincronizar cambios hechos manualmente en el calendario externo
/// (webhook de Google/Microsoft o polling).
/// </summary>
public class SyncExternalChangesUseCase
{
    private readonly IAppointmentRepository _appointmentRepo;
    private readonly ICalendarConnectionRepository _connectionRepo;
    private readonly ICalendarProviderFactory _providerFactory;
    private readonly IUnitOfWork _unitOfWork;

    public SyncExternalChangesUseCase(
        IAppointmentRepository appointmentRepo,
        ICalendarConnectionRepository connectionRepo,
        ICalendarProviderFactory providerFactory,
        IUnitOfWork unitOfWork)
    {
        _appointmentRepo = appointmentRepo;
        _connectionRepo = connectionRepo;
        _providerFactory = providerFactory;
        _unitOfWork = unitOfWork;
    }

    public async Task<int> ExecuteAsync(Guid tenantId, CancellationToken ct = default)
    {
        var connection = await _connectionRepo.GetByTenantIdAsync(tenantId, ct);
        if (connection?.Activo != true || string.IsNullOrEmpty(connection.SyncToken))
            return 0;

        var provider = await _providerFactory.GetProviderAsync(tenantId, ct);
        if (provider == null) return 0;

        // Get changes from external calendar using delta sync token
        var changes = await provider.GetChangesAsync(tenantId, connection.SyncToken, ct);
        int processedCount = 0;

        foreach (var change in changes)
        {
            var existingAppointment = await _appointmentRepo.GetByExternalEventIdAsync(change.ExternalEventId, ct);

            switch (change.Tipo)
            {
                case "deleted":
                    if (existingAppointment != null)
                    {
                        existingAppointment.Estado = "cancelled";
                        existingAppointment.FechaActualizacion = DateTime.UtcNow;
                        existingAppointment.MotivoCancelacion = "Cancelado desde calendario externo";
                        await _appointmentRepo.UpdateAsync(existingAppointment, ct);
                        processedCount++;
                    }
                    break;

                case "updated":
                    if (existingAppointment != null && change.FechaInicio.HasValue && change.FechaFin.HasValue)
                    {
                        existingAppointment.FechaInicio = change.FechaInicio.Value;
                        existingAppointment.FechaFin = change.FechaFin.Value;
                        existingAppointment.FechaActualizacion = DateTime.UtcNow;
                        await _appointmentRepo.UpdateAsync(existingAppointment, ct);
                        processedCount++;
                    }
                    break;

                case "created":
                    // New event created externally — could be imported if policy allows
                    break;
            }
        }

        await _unitOfWork.SaveChangesAsync(ct);
        return processedCount;
    }
}
