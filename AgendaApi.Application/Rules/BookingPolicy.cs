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
        Guid? professionalId = null,
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

        // 2. Horario laboral + excepciones (misma semántica que CheckAvailabilityUseCase).
        //    Con profesional se suman sus reglas personales (precedencia específica sobre negocio).
        var businessRules = await _availabilityRepo.GetByTenantIdAsync(tenantId, ct) ?? new List<AvailabilityRule>();
        var businessExceptions = await _availabilityRepo.GetExceptionsByDateRangeAsync(tenantId, fechaInicio, fechaFin, ct) ?? new List<AvailabilityException>();

        var personalRules = new List<AvailabilityRule>();
        var personalExceptions = new List<AvailabilityException>();
        if (professionalId.HasValue)
        {
            personalRules = await _availabilityRepo.GetByTenantAndProfessionalAsync(tenantId, professionalId.Value, ct) ?? new List<AvailabilityRule>();
            personalExceptions = await _availabilityRepo.GetExceptionsByDateRangeForProfessionalAsync(tenantId, fechaInicio, fechaFin, professionalId.Value, ct) ?? new List<AvailabilityException>();
        }

        if (!IsWithinWorkingHours(fechaInicio, fechaFin, businessRules, businessExceptions, personalRules, personalExceptions))
        {
            return professionalId.HasValue
                ? BookingValidationResult.Fail("El horario solicitado está fuera del horario del profesional")
                : BookingValidationResult.Fail("El horario solicitado está fuera del horario laboral del negocio");
        }

        // 3. Conflicto con citas locales.
        //    Sin profesional: capacidad simultánea del servicio (comportamiento actual).
        //    Con profesional: solo cuentan sus citas + las legadas sin profesional (canal ocupado o libre), capacidad 1.
        var conflicting = professionalId.HasValue
            ? await _appointmentRepo.GetByDateRangeForProfessionalAsync(tenantId, fechaInicio, fechaFin, professionalId.Value, ct)
            : await _appointmentRepo.GetByDateRangeAsync(tenantId, fechaInicio, fechaFin, ct);
        var overlapCount = conflicting.Count(a => a.Estado != "cancelled"
                                                  && (excludeAppointmentId == null || a.IdAppointment != excludeAppointmentId));
        var effectiveCapacity = professionalId.HasValue ? 1 : capacidad;
        if (overlapCount >= effectiveCapacity)
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

                    // Los eventos del propio sistema ya existen como CITAS LOCALES y se contaron
                    // contra el canal/capacidad antes. Excluirlos del busy externo para no
                    // bloquear dos veces el mismo rango (p.ej. la 2ª cita de un servicio con
                    // capacidad N, o el canal de otro profesional en paralelo). El calendario
                    // externo queda como bloqueo duro SOLO para los eventos manuales del dueño
                    // (aquellos sin ExternalEventId local asociado).
                    // NOTA: al validar el canal de un profesional se usa la lista COMPLETA de
                    // citas del rango (no solo las de su canal): la cita de OTRO profesional
                    // también crea su evento en el calendario compartido del tenant y su propio
                    // evento no debe bloquear un canal paralelo.
                    var allInRange = (professionalId.HasValue
                        ? await _appointmentRepo.GetByDateRangeAsync(tenantId, fechaInicio, fechaFin, ct)
                        : conflicting) ?? new List<Appointment>();
                    var ownExternalIds = new HashSet<string>(allInRange
                        .Where(a => a.ExternalEventId != null)
                        .Select(a => a.ExternalEventId!));
                    var externalEventsFiltered = externalEvents
                        .Where(e => e.ExternalEventId == null || !ownExternalIds.Contains(e.ExternalEventId))
                        .ToList();

                    if (externalEventsFiltered.Any(e => e.FechaInicio < fechaFin && e.FechaFin > fechaInicio))
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
    /// Verifica que el intervalo [fechaInicio, fechaFin) quede completamente dentro de las
    /// ventanas activas de su día (misma semántica que CheckAvailabilityUseCase, vía AvailabilityResolver).
    /// </summary>
    private static bool IsWithinWorkingHours(
        DateTime fechaInicio,
        DateTime fechaFin,
        List<AvailabilityRule> businessRules,
        List<AvailabilityException> businessExceptions,
        List<AvailabilityRule> personalRules,
        List<AvailabilityException> personalExceptions)
    {
        // Una reserva no puede cruzar de día
        if (DateOnly.FromDateTime(fechaInicio) != DateOnly.FromDateTime(fechaFin))
            return false;

        var windows = AvailabilityResolver.ResolveDayWindows(
            DateOnly.FromDateTime(fechaInicio), businessRules, businessExceptions, personalRules, personalExceptions);

        return windows.Any(w => fechaInicio.TimeOfDay >= w.HoraInicio && fechaFin.TimeOfDay <= w.HoraFin);
    }
}