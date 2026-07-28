using AgendaApi.Application.DTOs;
using AgendaApi.Domain.Ports;

namespace AgendaApi.Application.UseCases;

/// <summary>
/// Caso de uso: Listar citas de un cliente por WhatsApp con filtro de estado.
/// </summary>
public class ListAppointmentsUseCase
{
    private readonly IAppointmentRepository _appointmentRepo;
    private readonly IClientRepository _clientRepo;
    private readonly IServiceTypeRepository _serviceTypeRepo;

    public ListAppointmentsUseCase(
        IAppointmentRepository appointmentRepo,
        IClientRepository clientRepo,
        IServiceTypeRepository serviceTypeRepo)
    {
        _appointmentRepo = appointmentRepo;
        _clientRepo = clientRepo;
        _serviceTypeRepo = serviceTypeRepo;
    }

    public async Task<List<AppointmentResponseDto>> ExecuteAsync(
        string clientWhatsApp,
        Guid tenantId,
        string? estado = "upcoming",
        CancellationToken ct = default)
    {
        var client = await _clientRepo.GetByWhatsAppAsync(clientWhatsApp, tenantId, ct);
        if (client == null)
            return new List<AppointmentResponseDto>();

        var appointments = await _appointmentRepo.GetByClientIdAsync(client.IdClient, ct);

        // Filter by status
        IEnumerable<Domain.Entities.Appointment> filtered = estado?.ToLower() switch
        {
            "pending" => appointments.Where(a => a.Estado == "pending"),
            "confirmed" => appointments.Where(a => a.Estado == "confirmed"),
            "cancelled" => appointments.Where(a => a.Estado == "cancelled"),
            "completed" => appointments.Where(a => a.Estado == "completed"),
            "upcoming" => appointments.Where(a => a.Estado == "pending" || a.Estado == "confirmed"),
            _ => appointments
        };

        // Build service type name cache
        var serviceTypes = await _serviceTypeRepo.GetByTenantIdAsync(tenantId, ct);
        var serviceTypeNames = serviceTypes.ToDictionary(s => s.IdServiceType, s => s.Nombre);

        return filtered
            .OrderByDescending(a => a.FechaInicio)
            .Select(a => new AppointmentResponseDto
            {
                Id = a.IdAppointment,
                TenantId = a.IdTenant,
                ClientId = a.IdClient,
                ClientName = client.Nombre,
                ServiceTypeId = a.IdServiceType,
                ServiceTypeName = serviceTypeNames.GetValueOrDefault(a.IdServiceType),
                FechaInicio = a.FechaInicio,
                FechaFin = a.FechaFin,
                Status = a.Estado,
                ExternalEventId = a.ExternalEventId,
                Notas = a.Notas
            })
            .ToList();
    }
}
