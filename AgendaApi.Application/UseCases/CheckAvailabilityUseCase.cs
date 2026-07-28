using AgendaApi.Application.DTOs;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;

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

    public CheckAvailabilityUseCase(
        IAvailabilityRepository availabilityRepo,
        IAppointmentRepository appointmentRepo,
        ICalendarConnectionRepository connectionRepo,
        ICalendarProviderFactory providerFactory,
        IServiceTypeRepository serviceTypeRepo)
    {
        _availabilityRepo = availabilityRepo;
        _appointmentRepo = appointmentRepo;
        _connectionRepo = connectionRepo;
        _providerFactory = providerFactory;
        _serviceTypeRepo = serviceTypeRepo;
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

        // 4. Get existing appointments in the range (blocked times)
        var existingAppointments = await _appointmentRepo.GetByDateRangeAsync(query.TenantId, from, to, ct);
        var existingEvents = existingAppointments
            .Where(a => a.Estado != "cancelled")
            .Select(a => new TimeSlot
            {
                FechaInicio = a.FechaInicio,
                FechaFin = a.FechaFin,
                Disponible = false,
                ExternalEventId = a.ExternalEventId
            })
            .ToList();

        // 5. Merge: remove slots that overlap with existing appointments
        var mergedSlots = MergeSlots(availableSlots, existingEvents);

        // 6. Also check external calendar (if connected)
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
                    mergedSlots = MergeSlots(mergedSlots, externalSlots.Select(s => new TimeSlot
                    {
                        FechaInicio = s.FechaInicio,
                        FechaFin = s.FechaFin,
                        Disponible = s.Disponible,
                        ExternalEventId = s.ExternalEventId
                    }).ToList());
                }
            }
            catch
            {
                // If external calendar is unreachable, rely on local data
            }
        }

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

    private static List<TimeSlot> MergeSlots(List<TimeSlot> available, List<TimeSlot> busy)
    {
        if (busy.Count == 0) return available;

        var result = new List<TimeSlot>();
        foreach (var slot in available)
        {
            var overlapping = busy
                .Where(b => b.FechaInicio < slot.FechaFin && b.FechaFin > slot.FechaInicio)
                .OrderBy(b => b.FechaInicio)
                .ToList();

            if (overlapping.Count == 0)
            {
                result.Add(slot);
                continue;
            }

            var currentStart = slot.FechaInicio;
            foreach (var busySlot in overlapping)
            {
                if (busySlot.FechaInicio > currentStart)
                {
                    result.Add(new TimeSlot
                    {
                        FechaInicio = currentStart,
                        FechaFin = busySlot.FechaInicio,
                        Disponible = true
                    });
                }
                currentStart = busySlot.FechaFin > currentStart ? busySlot.FechaFin : currentStart;
            }

            if (currentStart < slot.FechaFin)
            {
                result.Add(new TimeSlot
                {
                    FechaInicio = currentStart,
                    FechaFin = slot.FechaFin,
                    Disponible = true
                });
            }
        }

        return result;
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
