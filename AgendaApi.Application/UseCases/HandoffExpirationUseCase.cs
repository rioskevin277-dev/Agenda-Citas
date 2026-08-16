using AgendaApi.Application.Services;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Application.UseCases;

/// <summary>
/// Caso de uso: auto-expiración de handoffs. Devuelve a <see cref="HandoffState.AiResumed"/>
/// los tickets abiertos (HumanPending/HumanActive) que llevan más de <see cref="IdleExpiration"/>
/// sin actividad. Un handoff "activo" se actualiza (FechaActualizacion) en cada respuesta del
/// asesor, así que un ticket abierto y sin tocar desde hace mucho es un ticket abandonado (p. ej.
/// de una prueba) que de otro modo congelaría al AI indefinidamente (el GATE de ChatOrchestratorService
/// no responde mientras haya un handoff abierto). Al volver a AiResumed, el control del cliente
/// regresa al asistente virtual.
/// </summary>
public class HandoffExpirationUseCase
{
    /// <summary>Horas de inactividad antes de considerar un handoff abandonado (24h).</summary>
    public const int IdleExpirationHours = 24;

    private readonly IHandoffRepository _handoffRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<HandoffExpirationUseCase> _logger;

    public HandoffExpirationUseCase(
        IHandoffRepository handoffRepo,
        IUnitOfWork unitOfWork,
        ILogger<HandoffExpirationUseCase> logger)
    {
        _handoffRepo = handoffRepo;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<int> ExpireAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddHours(-IdleExpirationHours);
        var stale = await _handoffRepo.GetStaleOpenAsync(cutoff, ct);
        if (stale.Count == 0)
            return 0;

        int expired = 0;
        foreach (var handoff in stale)
        {
            handoff.Estado = HandoffState.AiResumed;
            handoff.FechaActualizacion = DateTime.UtcNow;
            await _handoffRepo.UpdateAsync(handoff, ct);
            expired++;
            _logger.LogInformation("[Handoff] Ticket {Id} expirado por inactividad (control vuelve al AI)",
                handoff.IdHandoff);
        }

        await _unitOfWork.SaveChangesAsync(ct);
        return expired;
    }
}