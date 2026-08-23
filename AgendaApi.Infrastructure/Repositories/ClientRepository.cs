using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgendaApi.Infrastructure.Repositories;

public class ClientRepository : IClientRepository
{
    private readonly AgendaDbContext _context;

    public ClientRepository(AgendaDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Busca un cliente por su identificador canónico: BSUID (UserId) o teléfono (WhatsApp).
    /// El mismo método sirve para clientes legacy (identificador = número) y nuevos (identificador
    /// = user_id de Meta); el valor que llega es una u otra forma.
    /// </summary>
    public async Task<Client?> GetByWhatsAppAsync(string identifier, Guid tenantId, CancellationToken ct = default)
        => await _context.Clients
            .FirstOrDefaultAsync(c => c.UserId == identifier || c.WhatsApp == identifier && c.IdTenant == tenantId, ct);

    public async Task<Client?> GetByUserIdAsync(string userId, Guid tenantId, CancellationToken ct = default)
        => await _context.Clients
            .FirstOrDefaultAsync(c => c.UserId == userId && c.IdTenant == tenantId, ct);

    public async Task<Client?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _context.Clients.FindAsync(new object[] { id }, ct);

    public async Task<Client> CreateAsync(Client client, CancellationToken ct = default)
    {
        await _context.Clients.AddAsync(client, ct);
        return client;
    }

    public Task UpdateAsync(Client client, CancellationToken ct = default)
    {
        _context.Entry(client).State = EntityState.Modified;
        return Task.CompletedTask;
    }

    public async Task<List<Client>> GetByTenantIdAsync(Guid tenantId, string? query = null, CancellationToken ct = default)
    {
        var q = _context.Clients.Where(c => c.IdTenant == tenantId);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var like = query.Trim();
            q = q.Where(c => c.Nombre != null && c.Nombre.Contains(like) || c.WhatsApp.Contains(like));
        }
        return await q.OrderBy(c => c.Nombre ?? c.WhatsApp).ToListAsync(ct);
    }
}
