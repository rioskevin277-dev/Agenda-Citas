using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgendaApi.Infrastructure.Repositories;

public class HandoffRepository : IHandoffRepository
{
    private readonly AgendaDbContext _context;

    public HandoffRepository(AgendaDbContext context)
    {
        _context = context;
    }

    private static string Normalize(string phone)
        => new(phone.Where(char.IsDigit).ToArray());

    /// <summary>Handoff abierto (HumanPending o HumanActive) de la conversación, si existe.</summary>
    public async Task<Handoff?> GetOpenByPhoneAsync(Guid tenantId, string phoneCliente, CancellationToken ct = default)
    {
        var phone = Normalize(phoneCliente);
        return await _context.Handoffs
            .Where(h => h.IdTenant == tenantId && h.PhoneCliente == phone
                        && (h.Estado == HandoffState.HumanPending || h.Estado == HandoffState.HumanActive))
            .OrderByDescending(h => h.FechaCreacion)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Último ticket de la conversación, esté abierto o cerrado.</summary>
    public async Task<Handoff?> GetLatestByPhoneAsync(Guid tenantId, string phoneCliente, CancellationToken ct = default)
    {
        var phone = Normalize(phoneCliente);
        return await _context.Handoffs
            .Where(h => h.IdTenant == tenantId && h.PhoneCliente == phone)
            .OrderByDescending(h => h.FechaCreacion)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Cola de handoffs abiertos del tenant, los más antiguos primero.</summary>
    public async Task<List<Handoff>> GetOpenByTenantAsync(Guid tenantId, CancellationToken ct = default)
        => await _context.Handoffs
            .Where(h => h.IdTenant == tenantId
                        && (h.Estado == HandoffState.HumanPending || h.Estado == HandoffState.HumanActive))
            .OrderBy(h => h.FechaCreacion)
            .ToListAsync(ct);

    /// <summary>
    /// Tickets abiertos (HumanPending o HumanActive) sin actividad desde hace más que el corte
    /// dado. Un handoff "activo" de verdad se actualiza (FechaActualizacion) en cada respuesta
    /// del asesor, así que un ticket abierto y sin tocar desde hace mucho es un ticket quedado
    /// (p. ej. de una prueba) que congela al AI; el uso case los devuelve a AiResumed.
    /// </summary>
    public async Task<List<Handoff>> GetStaleOpenAsync(DateTime cutoffUtc, CancellationToken ct = default)
        => await _context.Handoffs
            .Where(h =>
                (h.Estado == HandoffState.HumanPending || h.Estado == HandoffState.HumanActive)
                && h.FechaActualizacion < cutoffUtc)
            .OrderBy(h => h.FechaActualizacion)
            .ToListAsync(ct);

    public async Task AddAsync(Handoff handoff, CancellationToken ct = default)
        => await _context.Handoffs.AddAsync(handoff, ct);

    public Task UpdateAsync(Handoff handoff, CancellationToken ct = default)
    {
        _context.Entry(handoff).State = EntityState.Modified;
        return Task.CompletedTask;
    }
}