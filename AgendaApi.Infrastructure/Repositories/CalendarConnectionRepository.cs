using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgendaApi.Infrastructure.Repositories;

public class CalendarConnectionRepository : ICalendarConnectionRepository
{
    private readonly AgendaDbContext _context;

    public CalendarConnectionRepository(AgendaDbContext context)
    {
        _context = context;
    }

    public async Task<CalendarConnection?> GetByTenantIdAsync(Guid tenantId, CancellationToken ct = default)
        => await _context.CalendarConnections
            .FirstOrDefaultAsync(c => c.IdTenant == tenantId && c.Activo, ct);

    public async Task<CalendarConnection?> GetByChannelIdAsync(string channelId, CancellationToken ct = default)
        => await _context.CalendarConnections
            .FirstOrDefaultAsync(c => c.SyncChannelId == channelId && c.Activo, ct);

    public async Task<CalendarConnection> CreateAsync(CalendarConnection connection, CancellationToken ct = default)
    {
        await _context.CalendarConnections.AddAsync(connection, ct);
        return connection;
    }

    public Task UpdateAsync(CalendarConnection connection, CancellationToken ct = default)
    {
        _context.Entry(connection).State = EntityState.Modified;
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.CalendarConnections.FindAsync(new object[] { id }, ct)
            ?? throw new InvalidOperationException("CalendarConnection no encontrada");
        _context.CalendarConnections.Remove(entity);
    }
}
