using AgendaApi.Application.DTOs;
using AgendaApi.Domain.Ports;
using AgendaApi.Domain.Services;

namespace AgendaApi.Application.UseCases;

/// <summary>
/// Caso de uso: Reprogramar una cita existente.
/// Calcula NuevaFechaFin automáticamente según la duración del servicio si no se especifica.
/// </summary>
public class RescheduleAppointmentUseCase
{
    private readonly IAppointmentRepository _appointmentRepo;
    private readonly IServiceTypeRepository _serviceTypeRepo;
    private readonly ICalendarConnectionRepository _connectionRepo;
    private readonly ICalendarProviderFactory _providerFactory;
    private readonly IClientRepository _clientRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IBookingPolicy _bookingPolicy;
    private readonly IWaitlistNotifier _waitlistNotifier;

    public RescheduleAppointmentUseCase(
        IAppointmentRepository appointmentRepo,
        IServiceTypeRepository serviceTypeRepo,
        ICalendarConnectionRepository connectionRepo,
        ICalendarProviderFactory providerFactory,
        IClientRepository clientRepo,
        IUnitOfWork unitOfWork,
        IBookingPolicy bookingPolicy,
        IWaitlistNotifier waitlistNotifier)
    {
        _appointmentRepo = appointmentRepo;
        _serviceTypeRepo = serviceTypeRepo;
        _connectionRepo = connectionRepo;
        _providerFactory = providerFactory;
        _clientRepo = clientRepo;
        _unitOfWork = unitOfWork;
        _bookingPolicy = bookingPolicy;
        _waitlistNotifier = waitlistNotifier;
    }

    public async Task<AppointmentResponseDto?> ExecuteAsync(AppointmentRescheduleDto dto, CancellationToken ct = default)
    {
        Domain.Entities.Appointment? appointment = null;
        // True cuando la cita se resolvió por WhatsApp (flujo del cliente). Decide si la
        // reprogramación debe volver a PENDIENTE para que el cliente re-confirme (E3).
        var resolvedByWhatsApp = false;

        if (dto.AppointmentId != Guid.Empty)
        {
            appointment = await _appointmentRepo.GetByIdAsync(dto.AppointmentId, ct);
        }
        else if (!string.IsNullOrWhiteSpace(dto.AppointmentIdentifier))
        {
            // El modelo no siempre tiene el ID real de la cita (tiende a inventar IDs).
            // Por ello se permite identificar la cita por el WhatsApp del cliente,
            // reprogramando la próxima cita pendiente/confirmada FUTURA.
            var client = await _clientRepo.GetByWhatsAppAsync(dto.AppointmentIdentifier, dto.TenantId, ct);
            if (client != null)
            {
                resolvedByWhatsApp = true;
                var clientAppointments = await _appointmentRepo.GetByClientIdAsync(client.IdClient, ct);

                // "Ahora" del negocio en hora local marcada como UTC (misma convención que
                // AppointmentRepository). Sin este filtro, REAGENDAR pega en la cita más
                // antigua aunque ya haya pasado y deja la real sin reprogramar.
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
            else if (Guid.TryParse(dto.AppointmentIdentifier, out var parsedId))
            {
                appointment = await _appointmentRepo.GetByIdAsync(parsedId, ct);
            }
        }

        if (appointment == null)
            throw new InvalidOperationException("Cita no encontrada");
        if (appointment.Estado == "cancelled")
            throw new InvalidOperationException("No se puede reprogramar una cita cancelada");

        if (appointment.Estado is "completed" or "no_show")
            throw new InvalidOperationException("No se puede reprogramar una cita ya finalizada");

        // Resolver el tipo de servicio para restricciones (capacidad) y el fin de la cita
        var serviceType = await _serviceTypeRepo.GetByIdAsync(appointment.IdServiceType, ct);

        // Calcular NuevaFechaFin si no se especificó
        var nuevaFechaFin = dto.NuevaFechaFin != default
            ? dto.NuevaFechaFin
            : serviceType != null
                ? dto.NuevaFechaInicio.AddMinutes(serviceType.DuracionMinutos + serviceType.BufferMinutos)
                : dto.NuevaFechaInicio.AddHours(1); // fallback: 1 hora

        // Validar el nuevo horario contra las reglas del negocio, excluyendo la cita que se reprograma.
        // Si la cita tenía profesional, se respeta su canal en el nuevo horario.
        var validation = await _bookingPolicy.ValidateAsync(
            appointment.IdTenant, dto.NuevaFechaInicio, nuevaFechaFin,
            excludeAppointmentId: appointment.IdAppointment,
            capacidad: serviceType?.CapacidadMaxima ?? 1,
            professionalId: appointment.IdProfessional, ct: ct);
        if (!validation.IsValid)
            throw new InvalidOperationException(validation.Reason);

        // Update in external calendar
        if (!string.IsNullOrEmpty(appointment.ExternalEventId))
        {
            try
            {
                var provider = await _providerFactory.GetProviderAsync(appointment.IdTenant, ct);
                if (provider != null)
                {
                    appointment.FechaInicio = dto.NuevaFechaInicio;
                    appointment.FechaFin = nuevaFechaFin;
                    await provider.UpdateEventAsync(appointment, ct);
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[RescheduleAppointment] Error actualizando evento externo: {ex.Message}");
            }
        }

        // Update local
        appointment.FechaInicio = dto.NuevaFechaInicio;
        appointment.FechaFin = nuevaFechaFin;
        appointment.FechaActualizacion = DateTime.UtcNow;

        // P0/E3: si el CLIENTE (resolución por WhatsApp) reprogramó una cita que estaba
        // confirmada, vuelve a PENDIENTE y limpia ConfirmadoEn: debe re-confirmar con
        // CONFIRMAR. La API/owner reprograma por AppointmentId y mantiene el estado
        // confirmado (no requiere re-confirmación del cliente).
        if (resolvedByWhatsApp && appointment.Estado == "confirmed")
        {
            appointment.Estado = "pending";
            appointment.ConfirmadoEn = null;
        }

        await _appointmentRepo.UpdateAsync(appointment, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        // No se notifica por WhatsApp aquí: la confirmación al cliente la entrega la
        // respuesta del asistente (un único mensaje). El adaptador requiere el token/phone
        // del tenant, que solo está completo en el flujo del webhook; en la API no aplica.

        // CRM: refrescar estado/próxima cita del cliente tras reprogramar su cita.
        await RefreshClientAsync(appointment.IdClient, ct);

        // P1 Lista de espera (fast path): el slot original que quedó libre puede matchear una
        // entrada en espera. Se dispara aquí para no esperar ≤5 min al job. No-fatal.
        try
        {
            await _waitlistNotifier.ScanAndNotifyAsync(ct);
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[RescheduleAppointment] Error notificando lista de espera: {ex.Message}");
        }

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

    /// <summary>Refresca el estado y la próxima cita del cliente a partir de su historial (CRM).</summary>
    private async Task RefreshClientAsync(Guid clientId, CancellationToken ct)
    {
        var client = await _clientRepo.GetByIdAsync(clientId, ct);
        if (client == null) return;
        var citas = await _appointmentRepo.GetByClientIdAsync(client.IdClient, ct);
        ClientStateCalculator.ApplyDerivedState(client, citas, DateTime.UtcNow);
        await _clientRepo.UpdateAsync(client, ct);
        await _unitOfWork.SaveChangesAsync(ct);
    }
}
