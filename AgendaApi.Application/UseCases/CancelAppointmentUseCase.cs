using AgendaApi.Application.DTOs;
using AgendaApi.Domain.Ports;

namespace AgendaApi.Application.UseCases;

/// <summary>
/// Caso de uso: Cancelar una cita (local + calendario externo).
/// Soporta cancelación por AppointmentId o por AppointmentIdentifier (WhatsApp del cliente).
/// </summary>
public class CancelAppointmentUseCase
{
    private readonly IAppointmentRepository _appointmentRepo;
    private readonly ICalendarConnectionRepository _connectionRepo;
    private readonly ICalendarProviderFactory _providerFactory;
    private readonly IClientRepository _clientRepo;
    private readonly IMessagingProvider _messagingProvider;
    private readonly IUnitOfWork _unitOfWork;

    public CancelAppointmentUseCase(
        IAppointmentRepository appointmentRepo,
        ICalendarConnectionRepository connectionRepo,
        ICalendarProviderFactory providerFactory,
        IClientRepository clientRepo,
        IMessagingProvider messagingProvider,
        IUnitOfWork unitOfWork)
    {
        _appointmentRepo = appointmentRepo;
        _connectionRepo = connectionRepo;
        _providerFactory = providerFactory;
        _clientRepo = clientRepo;
        _messagingProvider = messagingProvider;
        _unitOfWork = unitOfWork;
    }

    public async Task<AppointmentResponseDto?> ExecuteAsync(AppointmentCancelDto dto, CancellationToken ct = default)
    {
        // Resolve appointment
        Domain.Entities.Appointment? appointment = null;

        if (dto.AppointmentId.HasValue)
        {
            appointment = await _appointmentRepo.GetByIdAsync(dto.AppointmentId.Value, ct);
        }
        else if (!string.IsNullOrWhiteSpace(dto.AppointmentIdentifier))
        {
            // Try as WhatsApp number first
            var client = await _clientRepo.GetByWhatsAppAsync(dto.AppointmentIdentifier, dto.TenantId, ct);
            if (client != null)
            {
                var clientAppointments = await _appointmentRepo.GetByClientIdAsync(client.IdClient, ct);
                // Get the next upcoming appointment
                appointment = clientAppointments
                    .Where(a => a.Estado == "pending" || a.Estado == "confirmed")
                    .OrderBy(a => a.FechaInicio)
                    .FirstOrDefault();
            }
            else
            {
                // Try as appointment ID string
                if (Guid.TryParse(dto.AppointmentIdentifier, out var appointmentId))
                    appointment = await _appointmentRepo.GetByIdAsync(appointmentId, ct);
            }
        }

        if (appointment == null)
            throw new InvalidOperationException("Cita no encontrada");

        if (appointment.Estado == "cancelled")
            throw new InvalidOperationException("La cita ya está cancelada");

        // Cancel in external calendar
        if (!string.IsNullOrEmpty(appointment.ExternalEventId))
        {
            try
            {
                var provider = await _providerFactory.GetProviderAsync(appointment.IdTenant, ct);
                if (provider != null)
                {
                    await provider.CancelEventAsync(appointment.IdTenant, appointment.ExternalEventId, dto.Motivo, ct);
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[CancelAppointment] Error cancelando evento externo: {ex.Message}");
            }
        }

        // Update local
        appointment.Estado = "cancelled";
        appointment.MotivoCancelacion = dto.Motivo;
        appointment.FechaActualizacion = DateTime.UtcNow;
        await _appointmentRepo.UpdateAsync(appointment, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        // Notify client
        try
        {
            var client = await _clientRepo.GetByIdAsync(appointment.IdClient, ct);
            if (client != null)
            {
                await _messagingProvider.SendTextAsync(
                    client.WhatsApp,
                    "❌ Tu cita ha sido cancelada correctamente. Si necesitas reagendar, escríbenos.",
                    ct);
            }
        }
        catch { }

        return new AppointmentResponseDto
        {
            Id = appointment.IdAppointment,
            TenantId = appointment.IdTenant,
            ClientId = appointment.IdClient,
            FechaInicio = appointment.FechaInicio,
            FechaFin = appointment.FechaFin,
            Status = appointment.Estado
        };
    }
}
