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

        var context = await CreateService().BuildClientContextAsync(TenantId, "", CancellationToken.None, phone: WhatsApp);

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

        var context = await CreateService().BuildClientContextAsync(TenantId, "", CancellationToken.None, phone: WhatsApp);

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

        var context = await CreateService().BuildClientContextAsync(TenantId, "", CancellationToken.None, phone: WhatsApp);

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

        var context = await CreateService().BuildClientContextAsync(TenantId, "", CancellationToken.None, phone: WhatsApp);

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

        var context = await CreateService().BuildClientContextAsync(TenantId, "", CancellationToken.None, phone: WhatsApp);

        client.ProximaCita.Should().NotBeNull();
        client.ProximaCita.Should().BeCloseTo(futura, TimeSpan.FromSeconds(1));
        context.Should().Contain("Próxima cita");
    }

    // --- Identidad BSUID (sin teléfono en el webhook) ---

    /// <summary>
    /// Con global usernames el webhook puede llegar SÓLO con BSUID (contacts[].user_id) y sin
    /// teléfono. El cliente debe crearse anclado al user_id, dejando el teléfono vacío (null-safe
    /// frente a los índices únicos filtrados), no fallando por falta de número.
    /// </summary>
    [Fact]
    public async Task BuildClientContext_NoPhoneOnlyBsId_CreatesClientWithUserId()
    {
        const string bsId = "US.13491208655302741918";
        _clientRepo.Setup(r => r.GetByUserIdAsync(bsId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Client?)null);

        var context = await CreateService().BuildClientContextAsync(TenantId, bsId, CancellationToken.None, username: "juan.perez");

        _clientRepo.Verify(r => r.CreateAsync(It.Is<Client>(c =>
            c.UserId == bsId && c.WhatsApp == "" && c.Username == "juan.perez" && c.Estado == "nuevo"), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        context.Should().Contain("CONTEXTO DEL CLIENTE");
        context.Should().Contain(bsId);
    }

    /// <summary>
    /// Migración teléfono→BSUID: el mismo usuario ya existía como cliente legacy (por teléfono).
    /// Al escribir por primera vez con BSUID, se le VINCULA el user_id en vez de duplicarlo.
    /// </summary>
    [Fact]
    public async Task BuildClientContext_MergeLegacyPhoneClient_LinksUserId()
    {
        const string bsId = "US.13491208655302741918";
        const string phone = "573223697115";
        var legacy = new Client
        {
            IdClient = Guid.NewGuid(),
            IdTenant = TenantId,
            WhatsApp = phone,
            Nombre = "Carlos",
            Estado = "nuevo"
        };
        _clientRepo.Setup(r => r.GetByUserIdAsync(bsId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Client?)null);
        _clientRepo.Setup(r => r.GetByWhatsAppAsync(phone, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(legacy);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(legacy.IdClient, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());

        await CreateService().BuildClientContextAsync(TenantId, bsId, CancellationToken.None, phone: phone);

        legacy.UserId.Should().Be(bsId);
        // Se persiste 2 veces: una al vincular el user_id (dentro de ResolveOrCreateAsync) y otra
        // al recalcular el estado/última interacción al final de BuildClientContextAsync.
        _clientRepo.Verify(r => r.UpdateAsync(legacy, It.IsAny<CancellationToken>()), Times.Exactly(2));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// System message user_changed_number / user_changed_user_id: el usuario cambió su BSUID
    /// (previous_user_id → user_id). El client debe reasignarse para no perder historial.
    /// </summary>
    [Fact]
    public async Task HandleUserChangedId_UpdateRecord()
    {
        var client = new Client
        {
            IdClient = Guid.NewGuid(),
            IdTenant = TenantId,
            UserId = "US.OLD",
            WhatsApp = "",
            Nombre = "Ana"
        };
        _clientRepo.Setup(r => r.GetByUserIdAsync("US.OLD", TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);

        await CreateService().HandleUserChangedIdAsync(TenantId, "US.NEW", "US.OLD");

        client.UserId.Should().Be("US.NEW");
        _clientRepo.Verify(r => r.UpdateAsync(client, It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Respuesta al botón request_contact_info (webhook type=="contacts"): el teléfono compartido
    /// se guarda en el client para futuros envíos.
    /// </summary>
    [Fact]
    public async Task StoreSharedPhone_SetsWhatsApp()
    {
        const string bsId = "US.13491208655302741918";
        const string phone = "573223697115";
        var client = new Client
        {
            IdClient = Guid.NewGuid(),
            IdTenant = TenantId,
            UserId = bsId,
            WhatsApp = "",
            Nombre = "Ana"
        };
        _clientRepo.Setup(r => r.GetByUserIdAsync(bsId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);

        await CreateService().StoreSharedPhoneAsync(TenantId, bsId, phone);

        client.WhatsApp.Should().Be(phone);
        _clientRepo.Verify(r => r.UpdateAsync(client, It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
