using AgendaApi.Application.DTOs;
using AgendaApi.Domain.Ports;

namespace AgendaApi.Application.UseCases;

/// <summary>
/// Caso de uso: Confirmar una cita (el cliente respondió CONFIRMAR a un recordatorio
/// o pidió confirmar su cita). Solo cambia el estado local a "confirmed" y registra
/// ConfirmadoEn — no toca el calendario externo (el evento ya existe en Google).
/// </summary>
public class ConfirmAppointmentUseCase
{
    private readonly IAppointmentRepository _appointmentRepo;
    private readonly IClientRepository _clientRepo;
    private readonly IUnitOfWork _unitOfWork;

    public ConfirmAppointmentUseCase(
        IAppointmentRepository appointmentRepo,
        IClientRepository clientRepo,
        IUnitOfWork unitOfWork)
    {
        _appointmentRepo = appointmentRepo;
        _clientRepo = clientRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<AppointmentResponseDto?> ExecuteAsync(
        AppointmentCancelDto dto,
        CancellationToken ct = default)
    {
        // Resolve appointment (mismo lookup que cancel_appointment)
        Domain.Entities.Appointment? appointment = null;

        if (dto.AppointmentId.HasValue)
        {
            appointment = await _appointmentRepo.GetByIdAsync(dto.AppointmentId.Value, ct);
        }
        else if (!string.IsNullOrWhiteSpace(dto.AppointmentIdentifier))
        {
            // Try as WhatsApp number first (confirma la próxima cita del cliente)
            var client = await _clientRepo.GetByWhatsAppAsync(dto.AppointmentIdentifier, dto.TenantId, ct);
            if (client != null)
            {
                var clientAppointments = await _appointmentRepo.GetByClientIdAsync(client.IdClient, ct);

                // "Ahora" del negocio en hora local marcada como UTC (misma convención que
                // AppointmentRepository). Sin este filtro, CONFIRMAR pega en la cita más antigua
                // aunque ya haya pasado (las confirmadas viejas se acumulan) y deja la pendiente
                // real sin confirmar.
                var businessNow = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById(
                        Environment.GetEnvironmentVariable("Calendar__TimeZone") ?? "America/Bogota"));
                var now = DateTime.SpecifyKind(businessNow, DateTimeKind.Utc);

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
            throw new InvalidOperationException("La cita está cancelada y no se puede confirmar");

        if (appointment.Estado != "confirmed")
        {
            appointment.Estado = "confirmed";
            appointment.FechaActualizacion = DateTime.UtcNow;
        }
        // Se refresca ConfirmadoEn SIEMPRE que el cliente confirma (aunque ya estuviera
        // confirmada): deja evidencia del último momento de confirmación del cliente.
        appointment.ConfirmadoEn = DateTime.UtcNow;
        await _appointmentRepo.UpdateAsync(appointment, ct);
        await _unitOfWork.SaveChangesAsync(ct);

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
