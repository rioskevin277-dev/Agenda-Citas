using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgendaApi.Infrastructure.Repositories;

public class ReminderLogRepository : IReminderLogRepository
{
    private readonly AgendaDbContext _context;

    public ReminderLogRepository(AgendaDbContext context)
    {
        _context = context;
    }

    public async Task<List<ReminderLog>> GetByAppointmentIdsAsync(IEnumerable<Guid> appointmentIds, CancellationToken ct = default)
    {
        var ids = appointmentIds.ToList();
        if (ids.Count == 0) return new List<ReminderLog>();

        return await _context.ReminderLogs
            .Where(r => ids.Contains(r.IdAppointment))
            .ToListAsync(ct);
    }

    public async Task<ReminderLog?> GetByWamIdAsync(string wamId, CancellationToken ct = default)
        => await _context.ReminderLogs
            .FirstOrDefaultAsync(r => r.WamId == wamId, ct);

    public async Task AddAsync(ReminderLog log, CancellationToken ct = default)
    {
        await _context.ReminderLogs.AddAsync(log, ct);
    }

    public Task UpdateAsync(ReminderLog log, CancellationToken ct = default)
    {
        _context.Entry(log).State = EntityState.Modified;
        return Task.CompletedTask;
    }
}
