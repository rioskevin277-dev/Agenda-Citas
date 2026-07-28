using AgendaApi.Domain.Ports;

namespace AgendaApi.Infrastructure.Data;

public class UnitOfWork : IUnitOfWork
{
    private readonly AgendaDbContext _context;

    public UnitOfWork(AgendaDbContext context)
    {
        _context = context;
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _context.SaveChangesAsync(ct);
}
