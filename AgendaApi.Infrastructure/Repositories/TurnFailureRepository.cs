using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgendaApi.Infrastructure.Repositories;

public class TurnFailureRepository : ITurnFailureRepository
{
    private readonly AgendaDbContext _context;

    public TurnFailureRepository(AgendaDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(TurnFailure failure, CancellationToken ct = default)
    {
        await _context.TurnFailures.AddAsync(failure, ct);
    }

    public async Task<List<TurnFailure>> GetLatestAsync(
        int limit,
        CancellationToken ct = default)
        => await _context.TurnFailures
            .OrderByDescending(f => f.FechaCreacion)
            .Take(limit)
            .ToListAsync(ct);
}
