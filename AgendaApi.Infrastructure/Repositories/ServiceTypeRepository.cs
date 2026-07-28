using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgendaApi.Infrastructure.Repositories;

public class ServiceTypeRepository : IServiceTypeRepository
{
    private readonly AgendaDbContext _context;

    public ServiceTypeRepository(AgendaDbContext context)
    {
        _context = context;
    }

    public async Task<ServiceType?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _context.ServiceTypes.FindAsync(new object[] { id }, ct);

    public async Task<List<ServiceType>> GetByTenantIdAsync(Guid tenantId, CancellationToken ct = default)
        => await _context.ServiceTypes
            .Where(s => s.IdTenant == tenantId && s.Activo)
            .ToListAsync(ct);

    public async Task<ServiceType> CreateAsync(ServiceType serviceType, CancellationToken ct = default)
    {
        await _context.ServiceTypes.AddAsync(serviceType, ct);
        return serviceType;
    }

    public Task UpdateAsync(ServiceType serviceType, CancellationToken ct = default)
    {
        _context.Entry(serviceType).State = EntityState.Modified;
        return Task.CompletedTask;
    }
}
