using AgendaApi.Application.DTOs;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

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
    private readonly IMessagingProvider _messagingProvider;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<RescheduleAppointmentUseCase> _logger;
    private readonly IBookingPolicy _bookingPolicy;

    public RescheduleAppointmentUseCase(
        IAppointmentRepository appointmentRepo,
        IServiceTypeRepository serviceTypeRepo,
        ICalendarConnectionRepository connectionRepo,
        ICalendarProviderFactory providerFactory,
        IClientRepository clientRepo,
        IMessagingProvider messagingProvider,
        IUnitOfWork unitOfWork,
        ILogger<RescheduleAppointmentUseCase> logger,
        IBookingPolicy bookingPolicy)
    {
        _appointmentRepo = appointmentRepo;
        _serviceTypeRepo = serviceTypeRepo;
        _connectionRepo = connectionRepo;
        _providerFactory = providerFactory;
        _clientRepo = clientRepo;
        _messagingProvider = messagingProvider;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _bookingPolicy = bookingPolicy;
    }

    public async Task<AppointmentResponseDto?> ExecuteAsync(AppointmentRescheduleDto dto, CancellationToken ct = default)
    {
        Domain.Entities.Appointment? appointment = null;

        if (dto.AppointmentId != Guid.Empty)
        {
            appointment = await _appointmentRepo.GetByIdAsync(dto.AppointmentId, ct);
        }
        else if (!string.IsNullOrWhiteSpace(dto.AppointmentIdentifier))
        {
            // El modelo no siempre tiene el ID real de la cita (tiende a inventar IDs).
            // Por ello se permite identificar la cita por el WhatsApp del cliente,
            // reprogrando la próxima cita pendiente/confirmada.
            var client = await _clientRepo.GetByWhatsAppAsync(dto.AppointmentIdentifier, dto.TenantId, ct);
            if (client != null)
            {
                var clientAppointments = await _appointmentRepo.GetByClientIdAsync(client.IdClient, ct);
                appointment = clientAppointments
                    .Where(a => a.Estado == "pending" || a.Estado == "confirmed")
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
        await _appointmentRepo.UpdateAsync(appointment, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        // Notify
        try
        {
            var client = await _clientRepo.GetByIdAsync(appointment.IdClient, ct);
            if (client != null)
            {
                var fechaStr = appointment.FechaInicio.ToString("dd/MM/yyyy 'a las' HH:mm");
                await _messagingProvider.SendTextAsync(
                    client.WhatsApp,
                    $"🔄 Tu cita ha sido reprogramada para el {fechaStr}.",
                    ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RescheduleAppointment] No se pudo notificar al cliente por WhatsApp: {Message}", ex.Message);
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
}
