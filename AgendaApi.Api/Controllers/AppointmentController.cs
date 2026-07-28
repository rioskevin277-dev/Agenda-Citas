using AgendaApi.Application.DTOs;
using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Ports;
using Microsoft.AspNetCore.Mvc;

namespace AgendaApi.Api.Controllers;

[ApiController]
[Route("api/appointments")]
public class AppointmentController : ControllerBase
{
    private readonly CreateAppointmentUseCase _createUseCase;
    private readonly CancelAppointmentUseCase _cancelUseCase;
    private readonly RescheduleAppointmentUseCase _rescheduleUseCase;
    private readonly CheckAvailabilityUseCase _checkAvailabilityUseCase;
    private readonly IAppointmentRepository _appointmentRepo;
    private readonly ITenantContext _tenantContext;

    public AppointmentController(
        CreateAppointmentUseCase createUseCase,
        CancelAppointmentUseCase cancelUseCase,
        RescheduleAppointmentUseCase rescheduleUseCase,
        CheckAvailabilityUseCase checkAvailabilityUseCase,
        IAppointmentRepository appointmentRepo,
        ITenantContext tenantContext)
    {
        _createUseCase = createUseCase;
        _cancelUseCase = cancelUseCase;
        _rescheduleUseCase = rescheduleUseCase;
        _checkAvailabilityUseCase = checkAvailabilityUseCase;
        _appointmentRepo = appointmentRepo;
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// Consultar disponibilidad del tenant.
    /// </summary>
    [HttpGet("availability")]
    public async Task<IActionResult> GetAvailability(
        [FromQuery] DateOnly fechaInicio,
        [FromQuery] DateOnly fechaFin,
        CancellationToken ct)
    {
        if (!_tenantContext.IsSet)
            return Unauthorized(new { error = "Tenant no configurado" });

        var query = new AvailabilityQueryDto
        {
            TenantId = _tenantContext.TenantId,
            FechaInicio = fechaInicio,
            FechaFin = fechaFin
        };

        var slots = await _checkAvailabilityUseCase.ExecuteAsync(query, ct);
        return Ok(slots);
    }

    /// <summary>
    /// Crear una nueva cita.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AppointmentCreateDto dto, CancellationToken ct)
    {
        try
        {
            var result = await _createUseCase.ExecuteAsync(dto, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancelar una cita.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] AppointmentCancelDto? dto, CancellationToken ct)
    {
        try
        {
            var cancelDto = dto ?? new AppointmentCancelDto();
            cancelDto = cancelDto with { AppointmentId = id };
            var result = await _cancelUseCase.ExecuteAsync(cancelDto, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Reprogramar una cita.
    /// </summary>
    [HttpPut("{id:guid}/reschedule")]
    public async Task<IActionResult> Reschedule(Guid id, [FromBody] AppointmentRescheduleDto dto, CancellationToken ct)
    {
        try
        {
            dto = dto with { AppointmentId = id };
            var result = await _rescheduleUseCase.ExecuteAsync(dto, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Listar citas del tenant.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        if (!_tenantContext.IsSet)
            return Unauthorized(new { error = "Tenant no configurado" });

        var appointments = await _appointmentRepo.GetByTenantIdAsync(_tenantContext.TenantId, from, to, ct);
        var result = appointments.Select(a => new AppointmentResponseDto
        {
            Id = a.IdAppointment,
            TenantId = a.IdTenant,
            ClientId = a.IdClient,
            ServiceTypeId = a.IdServiceType,
            FechaInicio = a.FechaInicio,
            FechaFin = a.FechaFin,
            Status = a.Estado,
            ExternalEventId = a.ExternalEventId,
            Notas = a.Notas
        });

        return Ok(result);
    }

    /// <summary>
    /// Obtener detalle de una cita.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var appointment = await _appointmentRepo.GetByIdAsync(id, ct);
        if (appointment == null)
            return NotFound();

        return Ok(new AppointmentResponseDto
        {
            Id = appointment.IdAppointment,
            TenantId = appointment.IdTenant,
            ClientId = appointment.IdClient,
            ServiceTypeId = appointment.IdServiceType,
            FechaInicio = appointment.FechaInicio,
            FechaFin = appointment.FechaFin,
            Status = appointment.Estado,
            ExternalEventId = appointment.ExternalEventId,
            Notas = appointment.Notas
        });
    }
}
