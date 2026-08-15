using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgendaApi.Infrastructure.Repositories;

public class WaitlistEntryRepository : IWaitlistEntryRepository
{
    private readonly AgendaDbContext _context;

    public WaitlistEntryRepository(AgendaDbContext context)
    {
        _context = context;
    }

    public async Task<List<WaitlistEntry>> GetActiveByTenantAsync(Guid tenantId, CancellationToken ct = default)
        => await _context.WaitlistEntries
            .Where(w => w.IdTenant == tenantId && w.Estado == "active")
            .OrderBy(w => w.FechaCreacion)
            .ToListAsync(ct);

    public async Task<List<WaitlistEntry>> GetActiveAsync(CancellationToken ct = default)
        => await _context.WaitlistEntries
            .Where(w => w.Estado == "active")
            .OrderBy(w => w.FechaCreacion)
            .ToListAsync(ct);

    public async Task<WaitlistEntry?> GetActiveByClientAndServiceAsync(
        Guid tenantId, Guid clientId, Guid serviceTypeId, CancellationToken ct = default)
        => await _context.WaitlistEntries
            .FirstOrDefaultAsync(w => w.IdTenant == tenantId
                                      && w.IdClient == clientId
                                      && w.IdServiceType == serviceTypeId
                                      && w.Estado == "active", ct);

    public async Task<int> GetFulfilledByTenantAsync(Guid tenantId, CancellationToken ct = default)
        => await _context.WaitlistEntries
            .CountAsync(w => w.IdTenant == tenantId && w.Estado == "fulfilled", ct);

    public async Task<WaitlistEntry> CreateAsync(WaitlistEntry entry, CancellationToken ct = default)
    {
        await _context.WaitlistEntries.AddAsync(entry, ct);
        return entry;
    }

    public Task UpdateAsync(WaitlistEntry entry, CancellationToken ct = default)
    {
        _context.Entry(entry).State = EntityState.Modified;
        return Task.CompletedTask;
    }
}