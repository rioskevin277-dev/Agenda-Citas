using AgendaApi.Application.DTOs;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Application.UseCases;

/// <summary>
/// Caso de uso: Consultar disponibilidad de un tenant para un rango de fechas.
/// Cruza reglas de disponibilidad local con eventos reales del calendario externo.
/// Soporta filtrado por ServiceTypeName (para el flujo AI).
/// </summary>
public class CheckAvailabilityUseCase
{
    private readonly IAvailabilityRepository _availabilityRepo;
    private readonly IAppointmentRepository _appointmentRepo;
    private readonly ICalendarConnectionRepository _connectionRepo;
    private readonly ICalendarProviderFactory _providerFactory;
    private readonly IServiceTypeRepository _serviceTypeRepo;
    private readonly ILogger<CheckAvailabilityUseCase> _logger;

    public CheckAvailabilityUseCase(
        IAvailabilityRepository availabilityRepo,
        IAppointmentRepository appointmentRepo,
        ICalendarConnectionRepository connectionRepo,
        ICalendarProviderFactory providerFactory,
        IServiceTypeRepository serviceTypeRepo,
        ILogger<CheckAvailabilityUseCase> logger)
    {
        _availabilityRepo = availabilityRepo;
        _appointmentRepo = appointmentRepo;
        _connectionRepo = connectionRepo;
        _providerFactory = providerFactory;
        _serviceTypeRepo = serviceTypeRepo;
        _logger = logger;
    }

    public async Task<List<TimeSlotDto>> ExecuteAsync(AvailabilityQueryDto query, CancellationToken ct = default)
    {
        var from = query.FechaInicio.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = query.FechaFin.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        // Resolve service type by name if provided
        ServiceType? filterServiceType = null;
        if (!string.IsNullOrWhiteSpace(query.ServiceTypeName))
        {
            var services = await _serviceTypeRepo.GetByTenantIdAsync(query.TenantId, ct);
            filterServiceType = services.FirstOrDefault(s =>
                s.Nombre.Contains(query.ServiceTypeName, StringComparison.OrdinalIgnoreCase));
        }
        else if (query.ServiceTypeId.HasValue)
        {
            filterServiceType = await _serviceTypeRepo.GetByIdAsync(query.ServiceTypeId.Value, ct);
        }

        // 1. Get recurring availability rules
        var rules = await _availabilityRepo.GetByTenantIdAsync(query.TenantId, ct);

        // 2. Get exceptions (holidays, special hours)
        var exceptions = await _availabilityRepo.GetExceptionsByDateRangeAsync(query.TenantId, from, to, ct);

        // 3. Build base available slots from rules (minus exceptions)
        var availableSlots = BuildSlotsFromRules(rules, exceptions, query.FechaInicio, query.FechaFin);

        if (availableSlots.Count == 0)
            return new List<TimeSlotDto>();

        // 4. Citas locales existentes en el rango: consumen la capacidad del servicio
        var existingAppointments = await _appointmentRepo.GetByDateRangeAsync(query.TenantId, from, to, ct);
        var localAppointments = existingAppointments
            .Where(a => a.Estado != "cancelled")
            .Select(a => new TimeSlot
            {
                FechaInicio = a.FechaInicio,
                FechaFin = a.FechaFin,
                Disponible = false,
                ExternalEventId = a.ExternalEventId
            })
            .ToList();

        // 5. Calendario externo: bloqueo duro, independiente de la capacidad (mismo criterio que BookingPolicy)
        var externalBusy = new List<TimeSlot>();
        var connection = await _connectionRepo.GetByTenantIdAsync(query.TenantId, ct);
        if (connection?.Activo == true)
        {
            try
            {
                var provider = await _providerFactory.GetProviderAsync(query.TenantId, ct);
                if (provider != null)
                {
                    var externalSlots = await provider.GetAvailabilityAsync(
                        query.TenantId, query.FechaInicio, query.FechaFin, ct);
                    externalBusy = externalSlots.Select(s => new TimeSlot
                    {
                        FechaInicio = s.FechaInicio,
                        FechaFin = s.FechaFin,
                        Disponible = s.Disponible,
                        ExternalEventId = s.ExternalEventId
                    }).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CheckAvailability] Calendario externo no disponible para tenant {TenantId}, usando datos locales", query.TenantId);
            }
        }

        // 6. Fusionar: recortar intervalos donde la ocupación alcanza la capacidad.
        //    Sin filtro de servicio la capacidad es 1 (cualquier cita bloquea = comportamiento actual).
        var capacidad = filterServiceType?.CapacidadMaxima ?? 1;
        var mergedSlots = MergeSlotsWithCapacity(availableSlots, localAppointments, externalBusy, capacidad);

        var result = mergedSlots
            .Where(s => s.Disponible)
            .Select(s => new TimeSlotDto
            {
                Start = s.FechaInicio,
                End = s.FechaFin,
                Disponible = s.Disponible,
                ServiceTypeName = filterServiceType?.Nombre
            })
            .OrderBy(s => s.Start)
            .ToList();

        return result;
    }

    private static List<TimeSlot> BuildSlotsFromRules(
        List<AvailabilityRule> rules,
        List<AvailabilityException> exceptions,
        DateOnly from, DateOnly to)
    {
        var slots = new List<TimeSlot>();
        var exceptionDays = exceptions
            .GroupBy(e => DateOnly.FromDateTime(e.Fecha))
            .ToDictionary(g => g.Key, g => g.ToList());

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var dayOfWeek = (int)date.DayOfWeek == 0 ? 7 : (int)date.DayOfWeek;

            if (exceptionDays.TryGetValue(date, out var dayExceptions))
            {
                var fullDayOff = dayExceptions.FirstOrDefault(e => e.DiaCompleto);
                if (fullDayOff != null)
                    continue;

                foreach (var ex in dayExceptions.Where(e => !e.DiaCompleto && e.HoraInicio.HasValue && e.HoraFin.HasValue))
                {
                    slots.Add(new TimeSlot
                    {
                        FechaInicio = date.ToDateTime(TimeOnly.FromTimeSpan(ex.HoraInicio!.Value), DateTimeKind.Utc),
                        FechaFin = date.ToDateTime(TimeOnly.FromTimeSpan(ex.HoraFin!.Value), DateTimeKind.Utc),
                        Disponible = true
                    });
                }
                continue;
            }

            var dayRules = rules.Where(r => r.DiaSemana == dayOfWeek && r.Activo).ToList();
            foreach (var rule in dayRules)
            {
                slots.Add(new TimeSlot
                {
                    FechaInicio = date.ToDateTime(TimeOnly.FromTimeSpan(rule.HoraInicio), DateTimeKind.Utc),
                    FechaFin = date.ToDateTime(TimeOnly.FromTimeSpan(rule.HoraFin), DateTimeKind.Utc),
                    Disponible = true
                });
            }
        }

        return slots;
    }

    /// <summary>
    /// Recorta los intervalos disponibles donde la ocupación alcanza la capacidad del servicio.
    /// Las citas locales cuentan hacia la capacidad; el calendario externo es bloqueo duro.
    /// Con capacidad = 1 el resultado es idéntico al recorte binario anterior (cualquier cita bloquea).
    /// </summary>
    private static List<TimeSlot> MergeSlotsWithCapacity(
        List<TimeSlot> available,
        List<TimeSlot> localAppointments,
        List<TimeSlot> externalBusy,
        int capacidad)
    {
        var result = new List<TimeSlot>();
        foreach (var slot in available)
        {
            var local = localAppointments.Where(b => Overlaps(b, slot)).ToList();
            var external = externalBusy.Where(b => Overlaps(b, slot)).ToList();

            if (local.Count == 0 && external.Count == 0)
            {
                result.Add(slot);
                continue;
            }

            // Puntos donde cambia la ocupación: inicios y fines de citas dentro del slot
            var boundaries = new SortedSet<DateTime> { slot.FechaInicio, slot.FechaFin };
            foreach (var a in local) AddBoundary(boundaries, a, slot);
            foreach (var e in external) AddBoundary(boundaries, e, slot);

            var points = boundaries.ToList();
            TimeSlot? previous = null; // para fusionar segmentos libres adyacentes (capacidad > 1)
            for (var i = 0; i < points.Count - 1; i++)
            {
                var segmentStart = points[i];
                var segmentEnd = points[i + 1];

                var localCount = local.Count(x => x.FechaInicio < segmentEnd && x.FechaFin > segmentStart);
                var blocked = external.Any(x => x.FechaInicio < segmentEnd && x.FechaFin > segmentStart);

                if (!blocked && localCount < capacidad)
                {
                    if (previous != null && previous.FechaFin == segmentStart)
                    {
                        previous.FechaFin = segmentEnd;
                    }
                    else
                    {
                        previous = new TimeSlot
                        {
                            FechaInicio = segmentStart,
                            FechaFin = segmentEnd,
                            Disponible = true
                        };
                        result.Add(previous);
                    }
                }
                else
                {
                    // Un segmento lleno (o bloqueado por el calendario externo) corta la fusión
                    previous = null;
                }
            }
        }

        return result;
    }

    private static bool Overlaps(TimeSlot a, TimeSlot b)
        => a.FechaInicio < b.FechaFin && a.FechaFin > b.FechaInicio;

    private static void AddBoundary(SortedSet<DateTime> boundaries, TimeSlot item, TimeSlot slot)
    {
        if (item.FechaInicio > slot.FechaInicio && item.FechaInicio < slot.FechaFin)
            boundaries.Add(item.FechaInicio);
        if (item.FechaFin > slot.FechaInicio && item.FechaFin < slot.FechaFin)
            boundaries.Add(item.FechaFin);
    }

    // Internal class for merging logic
    private class TimeSlot
    {
        public DateTime FechaInicio { get; set; }
        public DateTime FechaFin { get; set; }
        public bool Disponible { get; set; }
        public string? ExternalEventId { get; set; }
    }
}
