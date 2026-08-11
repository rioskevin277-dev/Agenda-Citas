using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgendaApi.Infrastructure.Repositories;

public class AppointmentRepository : IAppointmentRepository
{
    private readonly AgendaDbContext _context;

    public AppointmentRepository(AgendaDbContext context)
    {
        _context = context;
    }

    public async Task<Appointment?> GetByIdAsync(Guid id, CancellationToken ct = default)
        // Include Professional para exponer el nombre del profesional en el detalle de la cita.
        => await _context.Appointments
            .Include(a => a.Professional)
            .FirstOrDefaultAsync(a => a.IdAppointment == id, ct);

    public async Task<List<Appointment>> GetByTenantIdAsync(Guid tenantId, DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        var query = _context.Appointments.Where(a => a.IdTenant == tenantId);
        if (from.HasValue) query = query.Where(a => a.FechaInicio >= from.Value);
        if (to.HasValue) query = query.Where(a => a.FechaFin <= to.Value);
        return await query.OrderBy(a => a.FechaInicio).ToListAsync(ct);
    }

    public async Task<List<Appointment>> GetByClientIdAsync(Guid clientId, CancellationToken ct = default)
        => await _context.Appointments
            .Where(a => a.IdClient == clientId)
            .OrderByDescending(a => a.FechaInicio)
            .ToListAsync(ct);

    public async Task<List<Appointment>> GetByDateRangeAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct = default)
        => await _context.Appointments
            .Where(a => a.IdTenant == tenantId && a.FechaInicio < to && a.FechaFin > from)
            .ToListAsync(ct);

    /// <summary>
    /// Citas que ocupan el canal de un profesional: sus propias citas (IdProfessional == professionalId)
    /// más las legadas sin profesional (IdProfessional == null), que por compatibilidad bloquean a cualquiera.
    /// </summary>
    public async Task<List<Appointment>> GetByDateRangeForProfessionalAsync(Guid tenantId, DateTime from, DateTime to, Guid professionalId, CancellationToken ct = default)
        => await _context.Appointments
            .Where(a => a.IdTenant == tenantId
                        && a.FechaInicio < to && a.FechaFin > from
                        && (a.IdProfessional == professionalId || a.IdProfessional == null))
            .ToListAsync(ct);

    public async Task<Appointment> CreateAsync(Appointment appointment, CancellationToken ct = default)
    {
        await _context.Appointments.AddAsync(appointment, ct);
        return appointment;
    }

    public Task UpdateAsync(Appointment appointment, CancellationToken ct = default)
    {
        _context.Entry(appointment).State = EntityState.Modified;
        return Task.CompletedTask;
    }

    public async Task<List<Appointment>> GetPendingRemindersAsync(CancellationToken ct = default)
    {
        // La app guarda las citas en hora local del negocio "disfrazada de UTC"
        // (un evento de las 14:00 en Colombia se almacena como 14:00Z). Por eso el "ahora"
        // también se debe convertir a la zona del negocio y marcar como UTC, igual que hace
        // GoogleCalendarAdapter, para que la ventana de 4 horas sea real y no se desfase 5h.
        var colombiaNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById(
                Environment.GetEnvironmentVariable("Calendar__TimeZone") ?? "America/Bogota"));
        var now = DateTime.SpecifyKind(colombiaNow, DateTimeKind.Utc);

        // Recordatorio ~4 horas antes de la cita (antes estaba en 24h, lo que disparaba
        // avisos apenas se agendaba la cita).
        var in4Hours = now.AddHours(4);

        return await _context.Appointments
            .Where(a => (a.Estado == "pending" || a.Estado == "confirmed")
                        && a.FechaInicio > now
                        && a.FechaInicio <= in4Hours
                        && a.RecordatorioEnviadoEn == null)
            .ToListAsync(ct);
    }

    public async Task<Appointment?> GetByExternalEventIdAsync(string externalEventId, CancellationToken ct = default)
        => await _context.Appointments
            .FirstOrDefaultAsync(a => a.ExternalEventId == externalEventId, ct);

    /// <summary>
    /// Citas futuras no canceladas cuyo evento externo falta (ExternalEventId == null).
    /// Reparan la sincronización local → externo. Misma conversión de zona horaria que
    /// GetPendingRemindersAsync ("ahora" del negocio marcado como UTC).
    /// </summary>
    public async Task<List<Appointment>> GetMissingExternalEventsAsync(CancellationToken ct = default)
    {
        var colombiaNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById(
                Environment.GetEnvironmentVariable("Calendar__TimeZone") ?? "America/Bogota"));
        var now = DateTime.SpecifyKind(colombiaNow, DateTimeKind.Utc);

        return await _context.Appointments
            .Where(a => a.Estado != "cancelled"
                        && a.ExternalEventId == null
                        && a.FechaInicio >= now)
            .OrderBy(a => a.FechaInicio)
            .ToListAsync(ct);
    }
}
