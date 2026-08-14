using AgendaApi.Application.DTOs;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Domain.Services;

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
    private readonly IProfessionalRepository _professionalRepo;
    private readonly ICalendarConnectionRepository _connectionRepo;
    private readonly ICalendarProviderFactory _providerFactory;
    private readonly IMessagingProvider _messagingProvider;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IBookingPolicy _bookingPolicy;

    public CreateAppointmentUseCase(
        IAppointmentRepository appointmentRepo,
        IClientRepository clientRepo,
        IServiceTypeRepository serviceTypeRepo,
        IProfessionalRepository professionalRepo,
        ICalendarConnectionRepository connectionRepo,
        ICalendarProviderFactory providerFactory,
        IMessagingProvider messagingProvider,
        IUnitOfWork unitOfWork,
        IBookingPolicy bookingPolicy)
    {
        _appointmentRepo = appointmentRepo;
        _clientRepo = clientRepo;
        _serviceTypeRepo = serviceTypeRepo;
        _professionalRepo = professionalRepo;
        _connectionRepo = connectionRepo;
        _providerFactory = providerFactory;
        _messagingProvider = messagingProvider;
        _unitOfWork = unitOfWork;
        _bookingPolicy = bookingPolicy;
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

        // Resolver profesional por ID o nombre (flujo AI: "Dra. María", "Dr. Carlos")
        Professional? professional = null;
        if (dto.ProfessionalId.HasValue)
        {
            professional = await _professionalRepo.GetByIdAsync(dto.ProfessionalId.Value, ct);
        }
        else if (!string.IsNullOrWhiteSpace(dto.ProfessionalName))
        {
            professional = await _professionalRepo.GetActiveByTenantAndNameAsync(dto.TenantId, dto.ProfessionalName, ct);
        }

        if (dto.ProfessionalId.HasValue || !string.IsNullOrWhiteSpace(dto.ProfessionalName))
        {
            if (professional == null || !professional.Activo)
                throw new InvalidOperationException($"Profesional '{dto.ProfessionalName}' no encontrado");

            // Relación Service → Profesional: sólo puede agendar servicios de su cartera
            var provides = await _professionalRepo.ProvidesServiceAsync(professional.IdProfessional, serviceType.IdServiceType, ct);
            if (!provides)
                throw new InvalidOperationException($"El profesional {professional.Nombre} no realiza el servicio {serviceType.Nombre}");
        }

        // Calculate end time based on service duration if FechaFin not set
        var fechaFin = dto.FechaFin != default
            ? dto.FechaFin
            : dto.FechaInicio.AddMinutes(serviceType.DuracionMinutos + serviceType.BufferMinutos);

        // Validar la reserva contra las reglas del negocio
        // (antelación, horario laboral, feriados/excepciones, capacidad, calendario externo).
        // Con profesional se valida el canal de ESE profesional (sus citas + legadas).
        var validation = await _bookingPolicy.ValidateAsync(
            dto.TenantId, dto.FechaInicio, fechaFin,
            capacidad: serviceType.CapacidadMaxima,
            professionalId: professional?.IdProfessional, ct: ct);
        if (!validation.IsValid)
            throw new InvalidOperationException(validation.Reason);

        // Create appointment locally
        var appointment = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = dto.TenantId,
            IdClient = client.IdClient,
            IdServiceType = serviceType.IdServiceType,
            IdProfessional = professional?.IdProfessional,
            FechaInicio = dto.FechaInicio,
            FechaFin = fechaFin,
            Estado = dto.ConfirmarAlCrear ? "confirmed" : "pending",
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

        // CRM: refrescar estado/próxima cita del cliente tras agendar su cita.
        await RefreshClientAsync(client.IdClient, ct);

        // Send confirmation via WhatsApp
        try
        {
            var fechaStr = appointment.FechaInicio.ToString("dd/MM/yyyy 'a las' HH:mm");
            var profStr = professional != null ? $"\n👤 Con: {professional.Nombre}" : "";
            var mensaje = appointment.Estado == "confirmed"
                ? $"✅ Cita confirmada: {serviceType.Nombre}{profStr}\n📅 {fechaStr}\nGracias por agendar. Si necesitas cambiar o cancelar, avísame."
                : $"📅 Cita agendada: {serviceType.Nombre}{profStr}\n📅 {fechaStr} — PENDIENTE de confirmación.\nResponde CONFIRMAR para confirmarla, o escríbenos para cambiar o cancelar.";
            await _messagingProvider.SendTextAsync(client.WhatsApp, mensaje, ct);
        }
        catch
        {
            // Notification failure shouldn't break the flow
        }

        return MapToDto(appointment, client.Nombre, serviceType.Nombre, professional?.Nombre);
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

    private static AppointmentResponseDto MapToDto(Appointment a, string? clientName, string? serviceName, string? professionalName = null)
    {
        return new AppointmentResponseDto
        {
            Id = a.IdAppointment,
            TenantId = a.IdTenant,
            ClientId = a.IdClient,
            ClientName = clientName,
            ServiceTypeId = a.IdServiceType,
            ServiceTypeName = serviceName,
            ProfessionalId = a.IdProfessional,
            ProfessionalName = professionalName,
            FechaInicio = a.FechaInicio,
            FechaFin = a.FechaFin,
            Status = a.Estado,
            ExternalEventId = a.ExternalEventId,
            Notas = a.Notas
        };
    }
}
