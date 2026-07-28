using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgendaApi.Infrastructure.Repositories;

public class AvailabilityRepository : IAvailabilityRepository
{
    private readonly AgendaDbContext _context;

    public AvailabilityRepository(AgendaDbContext context)
    {
        _context = context;
    }

    public async Task<List<AvailabilityRule>> GetByTenantIdAsync(Guid tenantId, CancellationToken ct = default)
        => await _context.AvailabilityRules
            .Where(r => r.IdTenant == tenantId && r.Activo)
            .OrderBy(r => r.DiaSemana)
            .ThenBy(r => r.HoraInicio)
            .ToListAsync(ct);

    public async Task<List<AvailabilityException>> GetExceptionsByDateRangeAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct = default)
        => await _context.AvailabilityExceptions
            .Where(e => e.IdTenant == tenantId && e.Fecha >= from && e.Fecha <= to)
            .ToListAsync(ct);

    public async Task<AvailabilityRule> CreateRuleAsync(AvailabilityRule rule, CancellationToken ct = default)
    {
        await _context.AvailabilityRules.AddAsync(rule, ct);
        return rule;
    }

    public async Task<AvailabilityException> CreateExceptionAsync(AvailabilityException exception, CancellationToken ct = default)
    {
        await _context.AvailabilityExceptions.AddAsync(exception, ct);
        return exception;
    }

    public async Task DeleteRuleAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.AvailabilityRules.FindAsync(new object[] { id }, ct)
            ?? throw new InvalidOperationException("Regla de disponibilidad no encontrada");
        _context.AvailabilityRules.Remove(entity);
    }

    public async Task DeleteExceptionAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.AvailabilityExceptions.FindAsync(new object[] { id }, ct)
            ?? throw new InvalidOperationException("Excepción de disponibilidad no encontrada");
        _context.AvailabilityExceptions.Remove(entity);
    }
}
