using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgendaApi.Tests.UseCases;

/// <summary>
/// Pruebas de la auto-expiración de handoffs: un ticket abierto (HumanPending/HumanActive)
/// sin actividad reciente debe volver a AiResumed (el control al AI), mientras que un ticket
/// activo (con actividad reciente) o cerrado no se toca.
/// </summary>
public class HandoffExpirationUseCaseTests
{
    private readonly Mock<IHandoffRepository> _repo = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private HandoffExpirationUseCase BuildUseCase()
        => new(_repo.Object, _unitOfWork.Object, NullLogger<HandoffExpirationUseCase>.Instance);

    private static Handoff OpenHandoff(HandoffState estado, DateTime ultimaActividad) => new()
    {
        IdHandoff = Guid.NewGuid(),
        IdTenant = Guid.NewGuid(),
        PhoneCliente = "573000000000",
        Estado = estado,
        FechaActualizacion = ultimaActividad,
        FechaCreacion = ultimaActividad.AddHours(-2)
    };

    [Fact]
    public async Task ExpireAsync_WithStaleOpenHandoffs_MarksThemAiResumed()
    {
        var stalePending = OpenHandoff(HandoffState.HumanPending, DateTime.UtcNow.AddHours(-30));
        var staleActive = OpenHandoff(HandoffState.HumanActive, DateTime.UtcNow.AddHours(-26));
        _repo.Setup(r => r.GetStaleOpenAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Handoff> { stalePending, staleActive });

        var expired = await BuildUseCase().ExpireAsync();

        expired.Should().Be(2);
        stalePending.Estado.Should().Be(HandoffState.AiResumed);
        staleActive.Estado.Should().Be(HandoffState.AiResumed);
        _repo.Verify(r => r.UpdateAsync(stalePending, It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.UpdateAsync(staleActive, It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExpireAsync_ComputesCutoffOf24Hours_AndFreshensUpdatedAt()
    {
        var stale = OpenHandoff(HandoffState.HumanPending, DateTime.UtcNow.AddHours(-30));
        _repo.Setup(r => r.GetStaleOpenAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Handoff> { stale });

        await BuildUseCase().ExpireAsync();

        _repo.Verify(r => r.GetStaleOpenAsync(
            It.Is<DateTime>(cut => DateTime.UtcNow.Subtract(cut).TotalHours > 24 && DateTime.UtcNow.Subtract(cut).TotalHours < 24.1),
            It.IsAny<CancellationToken>()), Times.Once);
        stale.FechaActualizacion.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ExpireAsync_NoStaleHandoffs_DoesNotSave()
    {
        _repo.Setup(r => r.GetStaleOpenAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Handoff>());

        var expired = await BuildUseCase().ExpireAsync();

        expired.Should().Be(0);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExpireAsync_RecentActivity_IsNotExpired()
    {
        // El gate consulta por fecha de actualización: un ticket con actividad reciente
        // (p. ej. el asesor respondió hace minutos) NO es "stale" y no debe tocarse.
        _repo.Setup(r => r.GetStaleOpenAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Handoff>());

        var expired = await BuildUseCase().ExpireAsync();

        expired.Should().Be(0);
        _repo.Verify(r => r.UpdateAsync(It.IsAny<Handoff>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}