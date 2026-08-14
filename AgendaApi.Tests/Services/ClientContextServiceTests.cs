using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgendaApi.Tests.Services;

public class ClientContextServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private const string WhatsApp = "573216403049";

    private readonly Mock<IClientRepository> _clientRepo = new();
    private readonly Mock<IAppointmentRepository> _appointmentRepo = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public ClientContextServiceTests()
    {
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
    }

    private ClientContextService CreateService()
        => new(_clientRepo.Object, _appointmentRepo.Object, _unitOfWork.Object,
            NullLogger<ClientContextService>.Instance);

    private static Client ExistingClient(string estado = "nuevo") => new()
    {
        IdClient = Guid.NewGuid(),
        IdTenant = TenantId,
        WhatsApp = WhatsApp,
        Nombre = "Carlos",
        Tags = "frecuente",
        Estado = estado,
        UltimaInteraccion = DateTime.UtcNow
    };

    private static Appointment Appointment(
        string estado, DateTime? inicio = null, string servicio = "Consulta") => new()
    {
        IdAppointment = Guid.NewGuid(),
        IdTenant = TenantId,
        IdClient = Guid.NewGuid(),
        Estado = estado,
        FechaInicio = inicio ?? DateTime.UtcNow,
        FechaFin = (inicio ?? DateTime.UtcNow).AddMinutes(30),
        ServiceType = new ServiceType { Nombre = servicio }
    };

    // --- Primer contacto ---

    [Fact]
    public async Task BuildClientContext_ClientDoesNotExist_CreatesClientAsNuevo()
    {
        _clientRepo.Setup(r => r.GetByWhatsAppAsync(WhatsApp, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Client?)null);

        var context = await CreateService().BuildClientContextAsync(TenantId, WhatsApp, CancellationToken.None);

        _clientRepo.Verify(r => r.CreateAsync(It.Is<Client>(c =>
            c.WhatsApp == WhatsApp && c.Estado == "nuevo" && c.Activo), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        context.Should().Contain("CONTEXTO DEL CLIENTE");
        context.Should().Contain("Cliente nuevo");
    }

    // --- Estado derivado ---

    [Fact]
    public async Task BuildClientContext_FrequentClient_SetsEstadoFrecuente()
    {
        var client = ExistingClient();
        _clientRepo.Setup(r => r.GetByWhatsAppAsync(WhatsApp, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(client.IdClient, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                Appointment("completed"), Appointment("completed"), Appointment("completed"), Appointment("pending")
            });

        var context = await CreateService().BuildClientContextAsync(TenantId, WhatsApp, CancellationToken.None);

        client.Estado.Should().Be("frecuente");
        _clientRepo.Verify(r => r.UpdateAsync(client, It.IsAny<CancellationToken>()), Times.Once);
        context.Should().Contain("3+ citas concretadas");
    }

    [Fact]
    public async Task BuildClientContext_NoShows_SetEstadoNoShow()
    {
        var client = ExistingClient();
        _clientRepo.Setup(r => r.GetByWhatsAppAsync(WhatsApp, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(client.IdClient, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                Appointment("no_show"), Appointment("no_show"), Appointment("completed")
            });

        var context = await CreateService().BuildClientContextAsync(TenantId, WhatsApp, CancellationToken.None);

        client.Estado.Should().Be("no_show");
        context.Should().Contain("inasistencias recurrentes");
    }

    [Fact]
    public async Task BuildClientContext_VipManualState_IsNotOverwritten()
    {
        var client = ExistingClient(estado: "vip");
        _clientRepo.Setup(r => r.GetByWhatsAppAsync(WhatsApp, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(client.IdClient, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());

        var context = await CreateService().BuildClientContextAsync(TenantId, WhatsApp, CancellationToken.None);

        client.Estado.Should().Be("vip");
        context.Should().Contain("VIP");
    }

    // --- Próxima cita ---

    [Fact]
    public async Task BuildClientContext_HasFuturePending_SetsProximaCita()
    {
        var client = ExistingClient();
        _clientRepo.Setup(r => r.GetByWhatsAppAsync(WhatsApp, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        var futura = DateTime.UtcNow.AddDays(5);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(client.IdClient, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment> { Appointment("pending", futura) });

        var context = await CreateService().BuildClientContextAsync(TenantId, WhatsApp, CancellationToken.None);

        client.ProximaCita.Should().NotBeNull();
        client.ProximaCita.Should().BeCloseTo(futura, TimeSpan.FromSeconds(1));
        context.Should().Contain("Próxima cita");
    }
}
