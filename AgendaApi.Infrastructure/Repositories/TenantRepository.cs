using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgendaApi.Infrastructure.Repositories;

public class TenantRepository : ITenantRepository
{
    private readonly AgendaDbContext _context;

    public TenantRepository(AgendaDbContext context)
    {
        _context = context;
    }

    public async Task<Tenant?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _context.Tenants.FirstOrDefaultAsync(t => t.IdTenant == id, ct);

    public async Task<Tenant?> GetByPhoneNumberIdAsync(string phoneNumberId, CancellationToken ct = default)
        => await _context.Tenants
            .FirstOrDefaultAsync(t => t.WhatsAppPhoneNumberId == phoneNumberId && t.Activo, ct);

    public async Task<Tenant> CreateAsync(Tenant tenant, CancellationToken ct = default)
    {
        await _context.Tenants.AddAsync(tenant, ct);
        return tenant;
    }

    public Task UpdateAsync(Tenant tenant, CancellationToken ct = default)
    {
        _context.Entry(tenant).State = EntityState.Modified;
        return Task.CompletedTask;
    }

    public async Task<List<Tenant>> GetAllActiveAsync(CancellationToken ct = default)
        => await _context.Tenants.Where(t => t.Activo).ToListAsync(ct);
}
