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

    public async Task<Client?> GetByWhatsAppAsync(string whatsapp, Guid tenantId, CancellationToken ct = default)
        => await _context.Clients
            .FirstOrDefaultAsync(c => c.WhatsApp == whatsapp && c.IdTenant == tenantId, ct);

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
}
