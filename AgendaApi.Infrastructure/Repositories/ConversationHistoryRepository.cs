using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgendaApi.Infrastructure.Repositories;

public class ConversationHistoryRepository : IConversationHistoryRepository
{
    private readonly AgendaDbContext _context;

    public ConversationHistoryRepository(AgendaDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(ConversationMessage message, CancellationToken ct = default)
    {
        await _context.ConversationMessages.AddAsync(message, ct);
    }

    public async Task<List<ConversationMessage>> GetRecentAsync(
        Guid tenantId,
        string phoneCliente,
        int limit = 20,
        CancellationToken ct = default)
        => await _context.ConversationMessages
            .Where(m => m.IdTenant == tenantId && m.PhoneCliente == phoneCliente)
            .OrderByDescending(m => m.FechaCreacion)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<List<ConversationMessage>> GetLatestAsync(
        int limit,
        CancellationToken ct = default)
        => (await _context.ConversationMessages
            .OrderByDescending(m => m.FechaCreacion)
            .Take(limit)
            .ToListAsync(ct))
            .OrderBy(m => m.FechaCreacion)   // ventana cronológica (más antigua primero)
            .ToList();

    public async Task<List<ConversationMessage>> GetSinceAsync(
        DateTime afterUtc,
        int limit,
        CancellationToken ct = default)
        => await _context.ConversationMessages
            .Where(m => m.FechaCreacion >= afterUtc)
            .OrderBy(m => m.FechaCreacion)
            .Take(limit)
            .ToListAsync(ct);
}