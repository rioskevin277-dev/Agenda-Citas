using AgendaApi.Application.DTOs;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;

namespace AgendaApi.Application.UseCases;

/// <summary>
/// Caso de uso: Crear una cita (validando disponibilidad real contra calendario externo).
/// Soporta tanto lookup por IDs como por nombre/WhatsApp (para el flujo AI).
/// </summary>
public class CreateAppointmentUseCase
{
    private readonly IAppointmentRepository _appointmentRepo;
    private readonly IClientRepository _clientRepo;
    private readonly IServiceTypeRepository _serviceTypeRepo;
    private readonly ICalendarConnectionRepository _connectionRepo;
    private readonly ICalendarProviderFactory _providerFactory;
    private readonly IMessagingProvider _messagingProvider;
    private readonly IUnitOfWork _unitOfWork;

    public CreateAppointmentUseCase(
        IAppointmentRepository appointmentRepo,
        IClientRepository clientRepo,
        IServiceTypeRepository serviceTypeRepo,
        ICalendarConnectionRepository connectionRepo,
        ICalendarProviderFactory providerFactory,
        IMessagingProvider messagingProvider,
        IUnitOfWork unitOfWork)
    {
        _appointmentRepo = appointmentRepo;
        _clientRepo = clientRepo;
        _serviceTypeRepo = serviceTypeRepo;
        _connectionRepo = connectionRepo;
        _providerFactory = providerFactory;
        _messagingProvider = messagingProvider;
        _unitOfWork = unitOfWork;
    }

    public async Task<AppointmentResponseDto?> ExecuteAsync(AppointmentCreateDto dto, CancellationToken ct = default)
    {
        // Resolve client by ID or WhatsApp
        Client? client = null;
        if (dto.ClientId.HasValue)
        {
            client = await _clientRepo.GetByIdAsync(dto.ClientId.Value, ct);
        }
        else if (!string.IsNullOrWhiteSpace(dto.ClientWhatsApp))
        {
            client = await _clientRepo.GetByWhatsAppAsync(dto.ClientWhatsApp, dto.TenantId, ct);

            // Create client if not exists
            if (client == null)
            {
                client = new Client
                {
                    IdClient = Guid.NewGuid(),
                    IdTenant = dto.TenantId,
                    WhatsApp = dto.ClientWhatsApp,
                    Nombre = dto.ClientName ?? dto.ClientWhatsApp,
                    Activo = true,
                    FechaCreacion = DateTime.UtcNow
                };
                client = await _clientRepo.CreateAsync(client, ct);
            }
            else if (!string.IsNullOrWhiteSpace(dto.ClientName) && client.Nombre != dto.ClientName)
            {
                client.Nombre = dto.ClientName;
                await _clientRepo.UpdateAsync(client, ct);
            }
        }
        else
        {
            throw new InvalidOperationException("Debe proporcionar ClientId o ClientWhatsApp");
        }

        if (client == null || !client.Activo)
            throw new InvalidOperationException("Cliente no encontrado o inactivo");

        // Resolve service type by ID or Name
        ServiceType? serviceType = null;
        if (dto.ServiceTypeId.HasValue)
        {
            serviceType = await _serviceTypeRepo.GetByIdAsync(dto.ServiceTypeId.Value, ct);
        }
        else if (!string.IsNullOrWhiteSpace(dto.ServiceTypeName))
        {
            var services = await _serviceTypeRepo.GetByTenantIdAsync(dto.TenantId, ct);
            serviceType = services.FirstOrDefault(s =>
                s.Nombre.Contains(dto.ServiceTypeName, StringComparison.OrdinalIgnoreCase));
        }

        if (serviceType == null || !serviceType.Activo)
            throw new InvalidOperationException($"Tipo de servicio '{dto.ServiceTypeName}' no encontrado o inactivo");

        // Calculate end time based on service duration if FechaFin not set
        var fechaFin = dto.FechaFin != default
            ? dto.FechaFin
            : dto.FechaInicio.AddMinutes(serviceType.DuracionMinutos + serviceType.BufferMinutos);

        // Validate no overlap
        var existingInRange = await _appointmentRepo.GetByDateRangeAsync(
            dto.TenantId, dto.FechaInicio, fechaFin, ct);
        if (existingInRange.Any(a => a.Estado != "cancelled"))
            throw new InvalidOperationException("El horario solicitado ya está ocupado");

        // Create appointment locally
        var appointment = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = dto.TenantId,
            IdClient = client.IdClient,
            IdServiceType = serviceType.IdServiceType,
            FechaInicio = dto.FechaInicio,
            FechaFin = fechaFin,
            Estado = "confirmed",
            Notas = dto.Notas,
            FechaCreacion = DateTime.UtcNow,
            FechaActualizacion = DateTime.UtcNow
        };

        // Try to create event in external calendar
        var connection = await _connectionRepo.GetByTenantIdAsync(dto.TenantId, ct);
        if (connection?.Activo == true)
        {
            try
            {
                var provider = await _providerFactory.GetProviderAsync(dto.TenantId, ct);
                if (provider != null)
                {
                    var eventId = await provider.CreateEventAsync(appointment, ct);
                    appointment.ExternalEventId = eventId;
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[CreateAppointment] Error creando evento externo: {ex.Message}");
            }
        }

        appointment = await _appointmentRepo.CreateAsync(appointment, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        // Send confirmation via WhatsApp
        try
        {
            var fechaStr = appointment.FechaInicio.ToString("dd/MM/yyyy 'a las' HH:mm");
            await _messagingProvider.SendTextAsync(
                client.WhatsApp,
                $"✅ Cita confirmada: {serviceType.Nombre}\n📅 {fechaStr}\nGracias por agendar. Si necesitas cambiar o cancelar, avísame.",
                ct);
        }
        catch
        {
            // Notification failure shouldn't break the flow
        }

        return MapToDto(appointment, client.Nombre, serviceType.Nombre);
    }

    private static AppointmentResponseDto MapToDto(Appointment a, string? clientName, string? serviceName)
    {
        return new AppointmentResponseDto
        {
            Id = a.IdAppointment,
            TenantId = a.IdTenant,
            ClientId = a.IdClient,
            ClientName = clientName,
            ServiceTypeId = a.IdServiceType,
            ServiceTypeName = serviceName,
            FechaInicio = a.FechaInicio,
            FechaFin = a.FechaFin,
            Status = a.Estado,
            ExternalEventId = a.ExternalEventId,
            Notas = a.Notas
        };
    }
}
