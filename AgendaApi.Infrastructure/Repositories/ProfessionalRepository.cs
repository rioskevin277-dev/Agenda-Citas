using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgendaApi.Infrastructure.Repositories;

public class ProfessionalRepository : IProfessionalRepository
{
    private readonly AgendaDbContext _context;

    public ProfessionalRepository(AgendaDbContext context)
    {
        _context = context;
    }

    public async Task<Professional?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _context.Professionals.FindAsync(new object[] { id }, ct);

    public async Task<List<Professional>> GetActiveByTenantIdAsync(Guid tenantId, CancellationToken ct = default)
        => await _context.Professionals
            .Where(p => p.IdTenant == tenantId && p.Activo)
            .OrderBy(p => p.Nombre)
            .ToListAsync(ct);

    public async Task<Professional?> GetActiveByTenantAndNameAsync(Guid tenantId, string nombre, CancellationToken ct = default)
        => await _context.Professionals
            .Where(p => p.IdTenant == tenantId && p.Activo
                        && (p.Nombre.Contains(nombre) || nombre.Contains(p.Nombre)) )
            .OrderBy(p => p.Nombre.Length)
            .FirstOrDefaultAsync(ct);

    public async Task<Professional> CreateAsync(Professional professional, CancellationToken ct = default)
    {
        await _context.Professionals.AddAsync(professional, ct);
        return professional;
    }

    public Task UpdateAsync(Professional professional, CancellationToken ct = default)
    {
        _context.Entry(professional).State = EntityState.Modified;
        return Task.CompletedTask;
    }

    public async Task<bool> ProvidesServiceAsync(Guid professionalId, Guid serviceTypeId, CancellationToken ct = default)
        => await _context.ProfessionalServices
            .AnyAsync(ps => ps.IdProfessional == professionalId
                            && ps.IdServiceType == serviceTypeId
                            && ps.Activo, ct);

    public async Task<ProfessionalService> AddServiceAsync(ProfessionalService ps, CancellationToken ct = default)
    {
        await _context.ProfessionalServices.AddAsync(ps, ct);
        return ps;
    }
}