using AgendaApi.Application.Rules;
using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgendaApi.Tests.UseCases;

/// <summary>
/// Pruebas del notificador de lista de espera (FIFO estricto + expiración 7 días + aviso).
/// Se construye un CheckAvailabilityUseCase REAL (clase concreta) con repos simulados que
/// devuelven disponibilidad controlada.
/// </summary>
public class WaitlistNotificationUseCaseTests
{
    private readonly Mock<IWaitlistEntryRepository> _waitlistRepo = new();
    private readonly Mock<IClientRepository> _clientRepo = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<IServiceTypeRepository> _serviceTypeRepo = new();
    private readonly Mock<ITenantContext> _tenantContext = new();
    private readonly Mock<IMessagingProvider> _messagingProvider = new();
    private readonly Mock<IConversationSessionService> _conversationSession = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ILogger<WaitlistNotificationUseCase>> _logger = new();

    public WaitlistNotificationUseCaseTests()
    {
        // Sin template configurado → el camino de texto libre (requiere sesión activa).
        Environment.SetEnvironmentVariable("WhatsApp__WaitlistTemplate", null);
        Environment.SetEnvironmentVariable("WHATSAPP_WAITLIST_TEMPLATE", null);

        _conversationSession.Setup(c => c.HasActiveSession(It.IsAny<Guid>(), It.IsAny<string>())).Returns(true);
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("wamid");
        _messagingProvider.Setup(m => m.SendTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("wamid");
    }

    private WaitlistNotificationUseCase BuildUseCase(bool available)
        => new(
            _waitlistRepo.Object,
            _clientRepo.Object,
            _tenantRepo.Object,
            _serviceTypeRepo.Object,
            _tenantContext.Object,
            _messagingProvider.Object,
            _conversationSession.Object,
            BuildAvailabilityUseCase(available),
            _unitOfWork.Object,
            _logger.Object);

    /// <summary>CheckAvailabilityUseCase real: disponibilidad controlada por el flag.</summary>
    private CheckAvailabilityUseCase BuildAvailabilityUseCase(bool available)
    {
        var availabilityRepo = new Mock<IAvailabilityRepository>();
        var appointmentRepo = new Mock<IAppointmentRepository>();
        var connectionRepo = new Mock<ICalendarConnectionRepository>();
        var providerFactory = new Mock<ICalendarProviderFactory>();
        var profRepo = new Mock<IProfessionalRepository>();
        var logger = new Mock<ILogger<CheckAvailabilityUseCase>>();

        // Reglas de negocio: todos los días 00:00–23:59 cuando hay disponibilidad, vacío si no.
        if (available)
        {
            var rules = Enumerable.Range(1, 7).Select(d => new AvailabilityRule
            {
                IdTenant = Guid.NewGuid(),
                DiaSemana = d,
                HoraInicio = TimeSpan.FromHours(0),
                HoraFin = TimeSpan.FromHours(23).Add(TimeSpan.FromMinutes(59)),
                Activo = true
            }).ToList();
            availabilityRepo.Setup(r => r.GetByTenantIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(rules);
        }
        else
        {
            availabilityRepo.Setup(r => r.GetByTenantIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<AvailabilityRule>());
        }
        availabilityRepo.Setup(r => r.GetExceptionsByDateRangeAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>());
        appointmentRepo.Setup(r => r.GetByDateRangeAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());
        connectionRepo.Setup(r => r.GetByTenantIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CalendarConnection?)null);

        return new CheckAvailabilityUseCase(
            availabilityRepo.Object,
            appointmentRepo.Object,
            connectionRepo.Object,
            providerFactory.Object,
            _serviceTypeRepo.Object,
            profRepo.Object,
            logger.Object);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static Tenant DefaultTenant() => new()
    {
        IdTenant = Guid.NewGuid(),
        CalendarProvider = "google",
        WhatsAppPhoneNumberId = "111111111111111",
        AntelacionMinimaHoras = 0,
        AntelacionMaximaDias = 7
    };

    private static WaitlistEntry Entry(Guid tenantId, Guid clientId, Guid serviceTypeId, DateTime? created = null) => new()
    {
        IdWaitlistEntry = Guid.NewGuid(),
        IdTenant = tenantId,
        IdClient = clientId,
        IdServiceType = serviceTypeId,
        IdProfessional = null,
        Estado = "active",
        FechaCreacion = created ?? DateTime.UtcNow
    };

    /// <summary>Configura el cliente y el servicio que devuelven los repos.</summary>
    private void SetupClientService(Guid clientId, Guid serviceTypeId, Guid tenantId)
    {
        var client = new Client { IdClient = clientId, IdTenant = tenantId, WhatsApp = "521234567890", Nombre = "Juan" };
        _clientRepo.Setup(r => r.GetByIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);

        var service = new ServiceType { IdServiceType = serviceTypeId, Nombre = "Consulta General", CapacidadMaxima = 1, DuracionMinutos = 30 };
        _serviceTypeRepo.Setup(r => r.GetByIdAsync(serviceTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(service);
    }

    private void SetupTenant(Tenant tenant)
        => _tenantRepo.Setup(r => r.GetByIdAsync(tenant.IdTenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);

    // ─── Casos ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScanAndNotify_CupoLiberado_NotificaAlClienteYGuardaNotified()
    {
        var tenant = DefaultTenant();
        var clientId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();
        var entry = Entry(tenant.IdTenant, clientId, serviceTypeId);
        SetupTenant(tenant);
        SetupClientService(clientId, serviceTypeId, tenant.IdTenant);
        _waitlistRepo.Setup(r => r.GetActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WaitlistEntry> { entry });

        var useCase = BuildUseCase(available: true);

        var count = await useCase.ScanAndNotifyAsync();

        count.Should().Be(1);
        _messagingProvider.Verify(m => m.SendTextAsync("521234567890", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _waitlistRepo.Verify(r => r.UpdateAsync(It.Is<WaitlistEntry>(e => e.Estado == "notified"), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScanAndNotify_SinDisponibilidad_NoNotifica()
    {
        var tenant = DefaultTenant();
        var clientId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();
        var entry = Entry(tenant.IdTenant, clientId, serviceTypeId);
        SetupTenant(tenant);
        SetupClientService(clientId, serviceTypeId, tenant.IdTenant);
        _waitlistRepo.Setup(r => r.GetActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WaitlistEntry> { entry });

        var useCase = BuildUseCase(available: false);

        var count = await useCase.ScanAndNotifyAsync();

        count.Should().Be(0);
        _messagingProvider.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _waitlistRepo.Verify(r => r.UpdateAsync(It.IsAny<WaitlistEntry>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScanAndNotify_FifoMismoServicio_SoloNotificaAlMasAntiguo()
    {
        var tenant = DefaultTenant();
        var clientId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();
        var viejo = Entry(tenant.IdTenant, clientId, serviceTypeId, DateTime.UtcNow.AddHours(-3));
        var reciente = Entry(tenant.IdTenant, Guid.NewGuid(), serviceTypeId, DateTime.UtcNow);
        SetupTenant(tenant);
        SetupClientService(viejo.IdClient, serviceTypeId, tenant.IdTenant);
        _waitlistRepo.Setup(r => r.GetActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WaitlistEntry> { reciente, viejo }); // fuera de orden

        var useCase = BuildUseCase(available: true);

        var count = await useCase.ScanAndNotifyAsync();

        // FIFO estricto: solo el más antiguo del grupo se notifica; el reciente sigue activo.
        count.Should().Be(1);
        _messagingProvider.Verify(m => m.SendTextAsync("521234567890", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _waitlistRepo.Verify(r => r.UpdateAsync(It.Is<WaitlistEntry>(e => e.IdWaitlistEntry == viejo.IdWaitlistEntry && e.Estado == "notified"), It.IsAny<CancellationToken>()), Times.Once);
        _waitlistRepo.Verify(r => r.UpdateAsync(It.Is<WaitlistEntry>(e => e.IdWaitlistEntry == reciente.IdWaitlistEntry), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScanAndNotify_EntradaExpirada7Dias_SeMarcaExpiredSinNotificar()
    {
        var tenant = DefaultTenant();
        var clientId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();
        var entry = Entry(tenant.IdTenant, clientId, serviceTypeId, DateTime.UtcNow.AddDays(-8));
        SetupTenant(tenant);
        SetupClientService(clientId, serviceTypeId, tenant.IdTenant);
        _waitlistRepo.Setup(r => r.GetActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WaitlistEntry> { entry });

        var useCase = BuildUseCase(available: true);

        var count = await useCase.ScanAndNotifyAsync();

        count.Should().Be(0);
        _messagingProvider.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _waitlistRepo.Verify(r => r.UpdateAsync(It.Is<WaitlistEntry>(e => e.IdWaitlistEntry == entry.IdWaitlistEntry && e.Estado == "expired"), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScanAndNotify_SinSesionYTamplate_NoNotificaYDejaActiva()
    {
        var tenant = DefaultTenant();
        var clientId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();
        var entry = Entry(tenant.IdTenant, clientId, serviceTypeId);
        SetupTenant(tenant);
        SetupClientService(clientId, serviceTypeId, tenant.IdTenant);
        _waitlistRepo.Setup(r => r.GetActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WaitlistEntry> { entry });

        // Sin template y sin sesión activa → no se puede entregar; la entrada queda activa
        // para reintentar en el siguiente ciclo (mismo comportamiento honesto que recordatorios).
        _conversationSession.Setup(c => c.HasActiveSession(It.IsAny<Guid>(), It.IsAny<string>())).Returns(false);

        var useCase = BuildUseCase(available: true);

        var count = await useCase.ScanAndNotifyAsync();

        count.Should().Be(0);
        _messagingProvider.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _messagingProvider.Verify(m => m.SendTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScanAndNotify_ServiciosIndependientes_NotificaARelderDeCadaUno()
    {
        var tenant = DefaultTenant();
        var svcA = Guid.NewGuid();
        var svcB = Guid.NewGuid();
        var eA = Entry(tenant.IdTenant, Guid.NewGuid(), svcA);
        var eB = Entry(tenant.IdTenant, Guid.NewGuid(), svcB);
        SetupTenant(tenant);
        SetupClientService(eA.IdClient, svcA, tenant.IdTenant);
        SetupClientService(eB.IdClient, svcB, tenant.IdTenant);
        _waitlistRepo.Setup(r => r.GetActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WaitlistEntry> { eA, eB });

        var useCase = BuildUseCase(available: true);

        var count = await useCase.ScanAndNotifyAsync();

        // Dos colas independientes → líderes de ambas se notifican.
        count.Should().Be(2);
        _messagingProvider.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}