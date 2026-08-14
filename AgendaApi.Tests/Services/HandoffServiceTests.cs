using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgendaApi.Tests.Services;

public class HandoffServiceTests
{
    private const string OwnerNumber = "573223697115";
    private const string ClientNumber = "573216403049";
    private static readonly Guid TenantId = Guid.NewGuid();

    private readonly Mock<IHandoffRepository> _repo = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IMessagingProvider> _messaging = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<ITenantContext> _tenantContext = new();

    public HandoffServiceTests()
    {
        _tenantRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tenant { Nombre = "Test", NombreComercial = "Comercial Test", CalendarProvider = "google", WhatsAppPhoneNumberId = "phone-id" });
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
    }

    private HandoffService CreateService()
        => new(_repo.Object, _unitOfWork.Object, _messaging.Object, _tenantRepo.Object, _tenantContext.Object,
            NullLogger<HandoffService>.Instance);

    private static Handoff OpenHandoff(HandoffState estado = HandoffState.HumanPending) => new()
    {
        IdHandoff = Guid.NewGuid(),
        IdTenant = TenantId,
        PhoneCliente = ClientNumber,
        Motivo = "test",
        Estado = estado
    };

    // --- EscalateAsync ---

    [Fact]
    public async Task EscalateAsync_NoOpenHandoff_CreatesTicketAndNotifiesOwner()
    {
        Environment.SetEnvironmentVariable("Notificaciones__WhatsAppDueno", OwnerNumber);
        _repo.Setup(r => r.GetOpenByPhoneAsync(TenantId, ClientNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Handoff?)null);

        var result = await CreateService().EscalateAsync(
            TenantId, ClientNumber, "Carlos", "Quiere hablar con una persona", "Consultó disponibilidad: 2 horario(s).", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Estado.Should().Be(HandoffState.HumanPending);
        result.PhoneCliente.Should().Be(ClientNumber);
        _repo.Verify(r => r.AddAsync(result, It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _messaging.Verify(m => m.SendTextAsync(OwnerNumber,
            It.Is<string>(s => s.Contains("Escalado a asesor humano")
                               && s.Contains("Quiere hablar con una persona")
                               && s.Contains("Carlos (573216403049)")
                               && s.Contains("Consultó disponibilidad: 2 horario(s).")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EscalateAsync_OpenHandoff_ReturnsNullAndDoesNotRepeat()
    {
        Environment.SetEnvironmentVariable("Notificaciones__WhatsAppDueno", OwnerNumber);
        _repo.Setup(r => r.GetOpenByPhoneAsync(TenantId, ClientNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpenHandoff());

        var result = await CreateService().EscalateAsync(TenantId, ClientNumber, null, "motivo", null, CancellationToken.None);

        result.Should().BeNull();
        _repo.Verify(r => r.AddAsync(It.IsAny<Handoff>(), It.IsAny<CancellationToken>()), Times.Never);
        _messaging.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EscalateAsync_WithoutOwnerNumber_CreatesTicketButNoNotify()
    {
        Environment.SetEnvironmentVariable("Notificaciones__WhatsAppDueno", null);
        _repo.Setup(r => r.GetOpenByPhoneAsync(TenantId, ClientNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Handoff?)null);

        var result = await CreateService().EscalateAsync(TenantId, ClientNumber, null, "motivo", null, CancellationToken.None);

        result.Should().NotBeNull();
        _repo.Verify(r => r.AddAsync(It.IsAny<Handoff>(), It.IsAny<CancellationToken>()), Times.Once);
        _messaging.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- HandleOwnerReplyAsync ---

    [Fact]
    public async Task HandleOwnerReplyAsync_NotOwner_ReturnsNotOwner()
    {
        Environment.SetEnvironmentVariable("Notificaciones__WhatsAppDueno", OwnerNumber);

        var result = await CreateService().HandleOwnerReplyAsync(TenantId, "573999999999", "hola", CancellationToken.None);

        result.Should().Be(HandoffService.OwnerReplyResult.NotOwner);
        _messaging.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleOwnerReplyAsync_OwnerWithoutOpenHandoff_ReturnsNoOpenHandoff()
    {
        Environment.SetEnvironmentVariable("Notificaciones__WhatsAppDueno", OwnerNumber);
        _repo.Setup(r => r.GetOpenByTenantAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Handoff>());

        var result = await CreateService().HandleOwnerReplyAsync(TenantId, OwnerNumber, "hola", CancellationToken.None);

        result.Should().Be(HandoffService.OwnerReplyResult.NoOpenHandoff);
        _messaging.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleOwnerReplyAsync_Reply_ForwardsToClientAndActivates()
    {
        Environment.SetEnvironmentVariable("Notificaciones__WhatsAppDueno", OwnerNumber);
        var handoff = OpenHandoff();
        _repo.Setup(r => r.GetOpenByTenantAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Handoff> { handoff });

        var result = await CreateService().HandleOwnerReplyAsync(TenantId, OwnerNumber, "Sí, te reservo el horario", CancellationToken.None);

        result.Should().Be(HandoffService.OwnerReplyResult.Forwarded);
        handoff.Estado.Should().Be(HandoffState.HumanActive);
        handoff.UltimoMensajeHumano.Should().Be("Sí, te reservo el horario");
        _repo.Verify(r => r.UpdateAsync(handoff, It.IsAny<CancellationToken>()), Times.Once);
        _messaging.Verify(m => m.SendTextAsync(ClientNumber, "Sí, te reservo el horario", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleOwnerReplyAsync_Fin_ClosesAndNotifiesClient()
    {
        Environment.SetEnvironmentVariable("Notificaciones__WhatsAppDueno", OwnerNumber);
        var handoff = OpenHandoff(HandoffState.HumanActive);
        _repo.Setup(r => r.GetOpenByTenantAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Handoff> { handoff });

        var result = await CreateService().HandleOwnerReplyAsync(TenantId, OwnerNumber, "FIN", CancellationToken.None);

        result.Should().Be(HandoffService.OwnerReplyResult.ChatClosed);
        handoff.Estado.Should().Be(HandoffState.AiResumed);
        _repo.Verify(r => r.UpdateAsync(handoff, It.IsAny<CancellationToken>()), Times.Once);
        _messaging.Verify(m => m.SendTextAsync(ClientNumber,
            It.Is<string>(s => s.Contains("asistente virtual quedó disponible")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Helpers estáticos ---

    [Fact]
    public void IsOwner_MatchesConfiguredNumber()
    {
        Environment.SetEnvironmentVariable("Notificaciones__WhatsAppDueno", "+57 322 369 7115");

        HandoffService.IsOwner("573223697115").Should().BeTrue();
        HandoffService.IsOwner("573216403049").Should().BeFalse();
    }

    [Fact]
    public void IsCloseCommand_AcceptsFinAndSlashFin()
    {
        HandoffService.IsCloseCommand("FIN").Should().BeTrue();
        HandoffService.IsCloseCommand("/fin").Should().BeTrue();
        HandoffService.IsCloseCommand("  fin  ").Should().BeTrue();
        HandoffService.IsCloseCommand("responder").Should().BeFalse();
    }
}