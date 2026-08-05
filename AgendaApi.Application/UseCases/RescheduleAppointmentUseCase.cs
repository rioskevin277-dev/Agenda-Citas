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

    public RescheduleAppointmentUseCase(
        IAppointmentRepository appointmentRepo,
        IServiceTypeRepository serviceTypeRepo,
        ICalendarConnectionRepository connectionRepo,
        ICalendarProviderFactory providerFactory,
        IClientRepository clientRepo,
        IMessagingProvider messagingProvider,
        IUnitOfWork unitOfWork,
        ILogger<RescheduleAppointmentUseCase> logger)
    {
        _appointmentRepo = appointmentRepo;
        _serviceTypeRepo = serviceTypeRepo;
        _connectionRepo = connectionRepo;
        _providerFactory = providerFactory;
        _clientRepo = clientRepo;
        _messagingProvider = messagingProvider;
        _unitOfWork = unitOfWork;
        _logger = logger;
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

        // Calcular NuevaFechaFin si no se especificó
        var nuevaFechaFin = dto.NuevaFechaFin != default
            ? dto.NuevaFechaFin
            : await CalculateEndTimeAsync(appointment.IdServiceType, dto.NuevaFechaInicio, ct);

        // Check no overlap for new time
        var existingInRange = await _appointmentRepo.GetByDateRangeAsync(
            appointment.IdTenant, dto.NuevaFechaInicio, nuevaFechaFin, ct);
        if (existingInRange.Any(a => a.IdAppointment != dto.AppointmentId && a.Estado != "cancelled"))
            throw new InvalidOperationException("El nuevo horario solicitado ya esta ocupado");

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

    private async Task<DateTime> CalculateEndTimeAsync(Guid serviceTypeId, DateTime startTime, CancellationToken ct)
    {
        var serviceType = await _serviceTypeRepo.GetByIdAsync(serviceTypeId, ct);
        if (serviceType == null)
            return startTime.AddHours(1); // fallback: 1 hora

        return startTime.AddMinutes(serviceType.DuracionMinutos + serviceType.BufferMinutos);
    }
}
