using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Application.Rules;

/// <summary>
/// Motor de reglas de agenda: validación de reservas contra las reglas reales del negocio.
/// Reusa la misma semántica que el read path (CheckAvailabilityUseCase):
/// reglas recurrentes + excepciones (feriados/horarios especiales) + citas locales + calendario externo.
/// Además aplica antelación mín/máx del tenant y capacidad simultánea del servicio.
/// </summary>
public class BookingPolicy : IBookingPolicy
{
    private readonly IAvailabilityRepository _availabilityRepo;
    private readonly IAppointmentRepository _appointmentRepo;
    private readonly ICalendarConnectionRepository _connectionRepo;
    private readonly ICalendarProviderFactory _providerFactory;
    private readonly ITenantRepository _tenantRepo;
    private readonly ILogger<BookingPolicy> _logger;

    public BookingPolicy(
        IAvailabilityRepository availabilityRepo,
        IAppointmentRepository appointmentRepo,
        ICalendarConnectionRepository connectionRepo,
        ICalendarProviderFactory providerFactory,
        ITenantRepository tenantRepo,
        ILogger<BookingPolicy> logger)
    {
        _availabilityRepo = availabilityRepo;
        _appointmentRepo = appointmentRepo;
        _connectionRepo = connectionRepo;
        _providerFactory = providerFactory;
        _tenantRepo = tenantRepo;
        _logger = logger;
    }

    public async Task<BookingValidationResult> ValidateAsync(
        Guid tenantId,
        DateTime fechaInicio,
        DateTime fechaFin,
        Guid? excludeAppointmentId = null,
        int capacidad = 1,
        CancellationToken ct = default)
    {
        if (fechaFin <= fechaInicio)
            return BookingValidationResult.Fail("La fecha de fin debe ser posterior a la fecha de inicio");

        // 1. Antelación mínima/máxima (reloj del negocio, misma convención "disfrazada de UTC")
        var tenant = await _tenantRepo.GetByIdAsync(tenantId, ct);
        if (tenant != null)
        {
            var now = GetBusinessNow();
            if (tenant.AntelacionMinimaHoras > 0
                && fechaInicio < now.AddHours(tenant.AntelacionMinimaHoras))
            {
                var limiteMin = tenant.AntelacionMinimaHoras == 1 ? "1 hora" : $"{tenant.AntelacionMinimaHoras} horas";
                return BookingValidationResult.Fail($"Debes agendar con al menos {limiteMin} de anticipación");
            }

            if (tenant.AntelacionMaximaDias > 0
                && fechaInicio > now.AddDays(tenant.AntelacionMaximaDias))
            {
                var limiteMax = tenant.AntelacionMaximaDias == 1 ? "1 día" : $"{tenant.AntelacionMaximaDias} días";
                return BookingValidationResult.Fail($"No se pueden agendar citas con más de {limiteMax} de anticipación");
            }
        }

        // 2. Horario laboral + excepciones (misma semántica que BuildSlotsFromRules)
        var rules = await _availabilityRepo.GetByTenantIdAsync(tenantId, ct) ?? new List<AvailabilityRule>();
        var exceptions = await _availabilityRepo.GetExceptionsByDateRangeAsync(tenantId, fechaInicio, fechaFin, ct) ?? new List<AvailabilityException>();
        if (!IsWithinWorkingHours(fechaInicio, fechaFin, rules, exceptions))
            return BookingValidationResult.Fail("El horario solicitado está fuera del horario laboral del negocio");

        // 3. Conflicto con citas locales, respetando la capacidad simultánea del servicio
        var conflicting = await _appointmentRepo.GetByDateRangeAsync(tenantId, fechaInicio, fechaFin, ct);
        var overlapCount = conflicting.Count(a => a.Estado != "cancelled"
                                                  && (excludeAppointmentId == null || a.IdAppointment != excludeAppointmentId));
        if (overlapCount >= capacidad)
            return BookingValidationResult.Fail("El horario solicitado ya está ocupado");

        // 4. Conflicto con el calendario externo (si está conectado).
        //    El calendario del negocio es bloqueo duro: si el dueño puso un evento, cierra
        //    para todos sin importar la capacidad del servicio.
        var connection = await _connectionRepo.GetByTenantIdAsync(tenantId, ct);
        if (connection?.Activo == true)
        {
            try
            {
                var provider = await _providerFactory.GetProviderAsync(tenantId, ct);
                if (provider != null)
                {
                    var externalEvents = await provider.GetAvailabilityAsync(
                        tenantId,
                        DateOnly.FromDateTime(fechaInicio),
                        DateOnly.FromDateTime(fechaFin),
                        ct);
                    if (externalEvents.Any(e => e.FechaInicio < fechaFin && e.FechaFin > fechaInicio))
                        return BookingValidationResult.Fail("El horario solicitado ya está ocupado en el calendario del negocio");
                }
            }
            catch (Exception ex)
            {
                // Si el calendario externo no responde, no bloquear la reserva
                // (mismo degrade que el read path: validar solo reglas locales).
                _logger.LogWarning(ex,
                    "[BookingPolicy] Calendario externo no disponible para tenant {TenantId}, validando solo reglas locales",
                    tenantId);
            }
        }

        return BookingValidationResult.Ok();
    }

    /// <summary>
    /// "Ahora" en la zona horaria del negocio, marcado como UTC. Igual convención que
    /// AppointmentRepository.GetPendingRemindersAsync: las citas se guardan en hora local
    /// del negocio "disfrazada de UTC", así que el reloj también se convierte a esa zona.
    /// </summary>
    private static DateTime GetBusinessNow()
    {
        var tzId = Environment.GetEnvironmentVariable("Calendar__TimeZone") ?? "America/Bogota";
        var businessNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tzId));
        return DateTime.SpecifyKind(businessNow, DateTimeKind.Utc);
    }

    /// <summary>
    /// Verifica que el intervalo [fechaInicio, fechaFin) quede completamente dentro del
    /// horario laboral de su día (reglas recurrentes o excepción del día).
    /// </summary>
    private static bool IsWithinWorkingHours(
        DateTime fechaInicio,
        DateTime fechaFin,
        List<AvailabilityRule> rules,
        List<AvailabilityException> exceptions)
    {
        // Una reserva no puede cruzar de día
        if (DateOnly.FromDateTime(fechaInicio) != DateOnly.FromDateTime(fechaFin))
            return false;

        var date = DateOnly.FromDateTime(fechaInicio);
        var dayExceptions = exceptions
            .Where(e => DateOnly.FromDateTime(e.Fecha) == date)
            .ToList();

        // Si el día tiene excepción, ésta reemplaza las reglas (mismo comportamiento que BuildSlotsFromRules)
        if (dayExceptions.Count > 0)
        {
            if (dayExceptions.Any(e => e.DiaCompleto))
                return false;

            return dayExceptions
                .Where(e => !e.DiaCompleto && e.HoraInicio.HasValue && e.HoraFin.HasValue)
                .Any(e => IsContained(fechaInicio, fechaFin, e.HoraInicio!.Value, e.HoraFin!.Value));
        }

        // Día normal: reglas recurrentes para ese día de la semana (1=Lunes ... 7=Domingo)
        var dayOfWeek = ((int)date.DayOfWeek == 0) ? 7 : (int)date.DayOfWeek;
        return rules
            .Where(r => r.DiaSemana == dayOfWeek && r.Activo)
            .Any(r => IsContained(fechaInicio, fechaFin, r.HoraInicio, r.HoraFin));
    }

    private static bool IsContained(DateTime fechaInicio, DateTime fechaFin, TimeSpan horaInicio, TimeSpan horaFin)
        => fechaInicio.TimeOfDay >= horaInicio && fechaFin.TimeOfDay <= horaFin;
}