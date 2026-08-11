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
    private readonly IUnitOfWork _unitOfWork;

    public CancelAppointmentUseCase(
        IAppointmentRepository appointmentRepo,
        ICalendarConnectionRepository connectionRepo,
        ICalendarProviderFactory providerFactory,
        IClientRepository clientRepo,
        IUnitOfWork unitOfWork)
    {
        _appointmentRepo = appointmentRepo;
        _connectionRepo = connectionRepo;
        _providerFactory = providerFactory;
        _clientRepo = clientRepo;
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

                // "Ahora" del negocio en hora local marcada como UTC (misma convención que
                // AppointmentRepository). Sin este filtro, CANCELAR pega en la cita más antigua
                // aunque ya haya pasado (las confirmadas viejas se acumulan) y deja la real sin cancelar.
                var businessNow = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById(
                        Environment.GetEnvironmentVariable("Calendar__TimeZone") ?? "America/Bogota"));
                var now = DateTime.SpecifyKind(businessNow, DateTimeKind.Utc);

                // Get the next upcoming (future) appointment
                appointment = clientAppointments
                    .Where(a => a.FechaInicio >= now
                                && (a.Estado == "pending" || a.Estado == "confirmed"))
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

        if (appointment.Estado is "completed" or "no_show")
            throw new InvalidOperationException("La cita ya finalizó y no se puede cancelar");

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

        // No se notifica por WhatsApp aquí: la confirmación al cliente la entrega la
        // respuesta del asistente (un único mensaje). El adaptador requiere el token/phone
        // del tenant, que solo está completo en el flujo del webhook; en la API no aplica.

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
