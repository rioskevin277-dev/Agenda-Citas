using AgendaApi.Application.DTOs;
using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.AiProviders;
using AgendaApi.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgendaApi.Tests.Services;

/// <summary>
/// Observabilidad de turnos fallidos: cuando la cadena de proveedores de IA falla completa o el
/// turno vence por timeout, el cliente recibe el genérico "Lo siento..." Y la CAUSA queda
/// persistida como TurnFailure (motivo + detalle de los proveedores probados).
/// Los proveedores se ejercitan con HttpMessageHandlers que lanzan al instante (sin red real):
/// mismo camino de excepción de transporte que una API caída / connection refused en producción.
/// </summary>
public class ChatOrchestratorServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private const string UserId = "US.1234567890abcdef";
    private const string Phone = "+573001112233";
    private const string GenericReply = "Lo siento, tuve un problema. Por favor intenta mas tarde.";

    private sealed class FailingHttpHandler : HttpMessageHandler
    {
        private readonly Exception _error;

        public FailingHttpHandler(Exception error)
        {
            _error = error;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_error);
    }

    private sealed record Sut(
        ChatOrchestratorService Service,
        ServiceProvider Provider,
        Mock<IMessagingProvider> Messaging,
        Mock<ITurnFailureRepository> TurnFailures,
        Mock<IUnitOfWork> UnitOfWork,
        IReadOnlyList<string> PersistenceCalls);

    private const string FailureAdded = "turn-failure-added";
    private const string ChangesSaved = "changes-saved";

    /// <summary>
    /// Contenedor mínimo con TODO lo que ProcessMessageAsync resuelve del scope antes y durante
    /// la cadena de proveedores. ClientContextService NO se registra a propósito: su resolución
    /// está protegida por try/catch en el orquestador (igual que una falla del CRM en producción).
    /// </summary>
    private static Sut BuildSut(Exception transportError, bool freshnessEnabled = true)
    {
        var messaging = new Mock<IMessagingProvider>();
        messaging
            .Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var turnFailures = new Mock<ITurnFailureRepository>();

        // Bitácora ordenada de persistencia: prueba que el registro de fallo se COMITEA a la
        // base (SaveChanges DESPUÉS del Add), no solo agregado al DbContext en memoria. Un
        // conteo plano de SaveChangesAsync no sirve acá: el historial de mensajes también
        // guarda (PersistMessageAsync) y ese conteo es ajeno a esta garantía.
        var persistenceCalls = new List<string>();
        turnFailures
            .Setup(r => r.AddAsync(It.IsAny<TurnFailure>(), It.IsAny<CancellationToken>()))
            .Callback(() => persistenceCalls.Add(FailureAdded))
            .Returns(Task.CompletedTask);

        var tenantRepo = new Mock<ITenantRepository>();
        tenantRepo
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tenant?)null);

        var serviceTypeRepo = new Mock<IServiceTypeRepository>();
        serviceTypeRepo
            .Setup(r => r.GetByTenantIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceType>());

        var professionalRepo = new Mock<IProfessionalRepository>();
        professionalRepo
            .Setup(r => r.GetActiveByTenantIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Professional>());

        var historyRepo = new Mock<IConversationHistoryRepository>();

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork
            .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => persistenceCalls.Add(ChangesSaved))
            .ReturnsAsync(1);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(messaging.Object);
        services.AddSingleton(turnFailures.Object);
        services.AddSingleton(new Mock<ITenantContext>().Object);
        services.AddSingleton(tenantRepo.Object);
        services.AddSingleton(serviceTypeRepo.Object);
        services.AddSingleton(professionalRepo.Object);
        services.AddSingleton(historyRepo.Object);
        services.AddSingleton(unitOfWork.Object);
        // Proveedores REALES (no mockeables: métodos no virtuales) con un handler que revienta
        // sin tocar la red: cada intento termina en excepción, igual que un proveedor caído.
        foreach (var clientName in new[] { "groq-api", "openrouter-api", "anthropic-api", "openai-api" })
        {
            services.AddHttpClient(clientName)
                .ConfigurePrimaryHttpMessageHandler(() => new FailingHttpHandler(transportError));
        }
        services.AddScoped<GroqProvider>();
        services.AddScoped<OpenRouterProvider>();
        services.AddScoped<AnthropicProvider>();
        services.AddScoped<OpenAIProvider>();

        services.AddSingleton<ConversationMemoryService>();
        services.AddSingleton<ConversationStateService>();

        // Feature flag de frescura: habilitado por defecto en las pruebas de fallo (no afecta
        // la ruta de fallo de proveedores, pero el ctor del orquestador lo lee).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Availability:FreshnessCheck"] = freshnessEnabled ? "true" : "false"
            })
            .Build();
        services.AddSingleton<IConfiguration>(configuration);

        var provider = services.BuildServiceProvider();

        var sut = new ChatOrchestratorService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ConversationMemoryService>(),
            provider.GetRequiredService<ConversationStateService>(),
            configuration,
            NullLogger<ChatOrchestratorService>.Instance);

        return new Sut(sut, provider, messaging, turnFailures, unitOfWork, persistenceCalls);
    }

    [Fact]
    public async Task AllProvidersFail_PersistsTurnFailureWithMotivo_AndRepliesGeneric()
    {
        var sut = BuildSut(new HttpRequestException("simulated outage"));

        await sut.Service.ProcessMessageAsync(UserId, "hola", TenantId, CancellationToken.None, phone: Phone);

        sut.TurnFailures.Verify(
            r => r.AddAsync(
                It.Is<TurnFailure>(f =>
                    f.Motivo == "all_providers_failed"
                    && f.IdTenant == TenantId
                    && f.PhoneCliente == UserId
                    && f.Detalle.Contains("OpenRouter")
                    && f.Detalle.Contains("Groq")
                    && f.Detalle.Contains("Anthropic")
                    && f.Detalle.Contains("OpenAI")),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // El registro de fallo se COMITEA: hubo SaveChangesAsync DESPUÉS del AddAsync.
        // Sin esta prueba, una regresión que suelte el commit pasaría desapercibida.
        sut.PersistenceCalls.Should().ContainInOrder(FailureAdded, ChangesSaved);

        // El cliente IGUAL recibe el genérico: la observabilidad no altera la respuesta.
        sut.Messaging.Verify(
            m => m.SendTextAsync(Phone, GenericReply, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CancelledTurn_PersistsTimeoutMotivo_AndStillReplies()
    {
        var sut = BuildSut(new HttpRequestException("simulated outage"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await sut.Service.ProcessMessageAsync(UserId, "hola", TenantId, cts.Token, phone: Phone);

        sut.TurnFailures.Verify(
            r => r.AddAsync(
                It.Is<TurnFailure>(f =>
                    f.Motivo == "timeout"
                    && f.IdTenant == TenantId
                    && f.PhoneCliente == UserId),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // El registro de timeout también se COMITEA (SaveChangesAsync después del AddAsync).
        sut.PersistenceCalls.Should().ContainInOrder(FailureAdded, ChangesSaved);

        sut.Messaging.Verify(
            m => m.SendTextAsync(Phone, GenericReply, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ==== Phase 2: re-check determinístico de frescura de cupos (RF1/RF3/RF5) ====

    /// <summary>
    /// CheckAvailabilityUseCase REAL (no mockeable) con repos mockeados que NO definen reglas:
    /// el re-check devuelve 0 cupos. Aísla la decisión de re-check + el hecho de frescura.
    /// </summary>
    private static CheckAvailabilityUseCase BuildEmptyAvailability()
    {
        var availability = new Mock<IAvailabilityRepository>();
        availability
            .Setup(r => r.GetByTenantIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityRule>());
        availability
            .Setup(r => r.GetExceptionsByDateRangeAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>());
        availability
            .Setup(r => r.GetByTenantAndProfessionalAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityRule>());
        availability
            .Setup(r => r.GetExceptionsByDateRangeForProfessionalAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>());

        var appointments = new Mock<IAppointmentRepository>();
        appointments
            .Setup(r => r.GetByDateRangeAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());
        appointments
            .Setup(r => r.GetByDateRangeForProfessionalAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());

        var connection = new Mock<ICalendarConnectionRepository>();
        connection
            .Setup(r => r.GetByTenantIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CalendarConnection?)null);

        var providerFactory = new Mock<ICalendarProviderFactory>();

        var svcTypes = new Mock<IServiceTypeRepository>();
        svcTypes
            .Setup(r => r.GetByTenantIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceType>());

        var professionals = new Mock<IProfessionalRepository>();
        professionals
            .Setup(r => r.GetActiveByTenantAndNameAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Professional?)null);

        return new CheckAvailabilityUseCase(
            availability.Object, appointments.Object, connection.Object,
            providerFactory.Object, svcTypes.Object, professionals.Object,
            NullLogger<CheckAvailabilityUseCase>.Instance);
    }

    [Fact]
    public async Task BuildAvailabilityFreshnessContext_NoTrigger_ReturnsNull_AndDoesNotQuery()
    {
        var sut = BuildSut(new HttpRequestException("simulated outage"));

        // Scope SIN CheckAvailabilityUseCase registrado: si el re-check decidiera ejecutarse,
        // reventaría al resolver el use case. Como no hay disparador, devuelve null sin tocarlo.
        var services = new ServiceCollection();
        var scopeProvider = services.BuildServiceProvider();

        // Sin PendingBooking, sin dirty, mensaje neutro (saludo): NO hay re-check.
        var result = await sut.Service.BuildAvailabilityFreshnessContextAsync(
            scopeProvider,
            pendingBooking: null,
            messageContent: "hola",
            tenantDirty: false,
            tenantId: TenantId,
            CancellationToken.None);

        result.Should().BeNull("sin disparador no debe ejecutarse el re-check");
    }

    [Fact]
    public async Task BuildAvailabilityFreshnessContext_PendingBooking_ReturnsNoSlotsFact()
    {
        var sut = BuildSut(new HttpRequestException("simulated outage"));

        var services = new ServiceCollection();
        services.AddSingleton(BuildEmptyAvailability());
        var scopeProvider = services.BuildServiceProvider();

        var pending = new PendingBooking(
            "Corte de cabello", null, DateOnly.FromDateTime(DateTime.Today.AddDays(1)));

        var result = await sut.Service.BuildAvailabilityFreshnessContextAsync(
            scopeProvider,
            pendingBooking: pending,
            messageContent: "si",
            tenantDirty: false,
            tenantId: TenantId,
            CancellationToken.None);

        result.Should().NotBeNull("hay PendingBooking: fuerza el re-check determinístico");
        result!.Value.Context.Should().Contain("NO hay cupos");
        result.Value.Context.Should().Contain("lista de espera");
        result.Value.HasSlots.Should().BeFalse();
    }

    [Fact]
    public async Task BuildAvailabilityFreshnessContext_DirtyTenant_TriggersRecheck()
    {
        var sut = BuildSut(new HttpRequestException("simulated outage"));

        var services = new ServiceCollection();
        services.AddSingleton(BuildEmptyAvailability());
        var scopeProvider = services.BuildServiceProvider();

        // Dirty flag (webhook RF3): dispara el re-check aunque no haya PendingBooking.
        var result = await sut.Service.BuildAvailabilityFreshnessContextAsync(
            scopeProvider,
            pendingBooking: null,
            messageContent: "hola",
            tenantDirty: true,
            tenantId: TenantId,
            CancellationToken.None);

        result.Should().NotBeNull("tenant sucio fuerza re-check aun sin solicitud de fecha");
    }

    // ==== Phase 4: feature flag de rollback (Availability:FreshnessCheck) ====

    [Fact]
    public async Task BuildAvailabilityFreshnessContext_FlagDisabled_ReturnsNull_DespiteTrigger()
    {
        // Rollback sin revert: con Availability:FreshnessCheck=false el re-check determinístico se
        // desactiva por completo, incluso con un disparador fuerte (PendingBooking).
        var sut = BuildSut(new HttpRequestException("simulated outage"), freshnessEnabled: false);

        var services = new ServiceCollection();
        services.AddSingleton(BuildEmptyAvailability());
        var scopeProvider = services.BuildServiceProvider();

        var pending = new PendingBooking(
            "Corte de cabello", null, DateOnly.FromDateTime(DateTime.Today.AddDays(1)));

        var result = await sut.Service.BuildAvailabilityFreshnessContextAsync(
            scopeProvider,
            pendingBooking: pending,
            messageContent: "quiero un turno",
            tenantDirty: true,
            tenantId: TenantId,
            CancellationToken.None);

        result.Should().BeNull("con el feature flag apagado no debe ejecutarse el re-check");
    }

    // ==== Phase 2: re-valida booking → slot prometido ya no libre => stale_availability (RF2/RF4) ====

    [Fact]
    public async Task CreateAppointment_WhenSlotReOccupied_PersistsStaleAvailability()
    {
        var sut = BuildSut(new HttpRequestException("simulated outage"));

        // BookingPolicy que rechaza: el horario ya está ocupado (cupo re-ocupado entre el
        // re-check determinístico y la materialización de la cita).
        var bookingPolicy = new Mock<IBookingPolicy>();
        bookingPolicy
            .Setup(p => p.ValidateAsync(
                It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BookingValidationResult.Fail("El horario solicitado ya está ocupado"));

        var client = new Client { IdClient = Guid.NewGuid(), IdTenant = TenantId, WhatsApp = Phone, Nombre = "Cliente", Activo = true };
        var clientRepo = new Mock<IClientRepository>();
        clientRepo
            .Setup(r => r.GetByWhatsAppAsync(Phone, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);

        var svcType = new ServiceType { IdServiceType = Guid.NewGuid(), IdTenant = TenantId, Nombre = "Corte", Activo = true, DuracionMinutos = 30 };
        var svcRepo = new Mock<IServiceTypeRepository>();
        svcRepo
            .Setup(r => r.GetByTenantIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceType> { svcType });

        var professionalRepo = new Mock<IProfessionalRepository>();

        var create = new CreateAppointmentUseCase(
            new Mock<IAppointmentRepository>().Object,
            clientRepo.Object,
            svcRepo.Object,
            professionalRepo.Object,
            new Mock<ICalendarConnectionRepository>().Object,
            new Mock<ICalendarProviderFactory>().Object,
            new Mock<IMessagingProvider>().Object,
            new Mock<IUnitOfWork>().Object,
            bookingPolicy.Object);

        // Proveedor de servicios del scope: el orquestador resuelve el use case Y los repos que
        // PersistTurnFailureAsync necesita (ITurnFailureRepository/IUnitOfWork) — los mismos mocks
        // que el Sut usa, para que la verificación posterior vea el AddAsync de stale_availability.
        var services = new ServiceCollection();
        services.AddSingleton(create);
        services.AddSingleton(sut.TurnFailures.Object);
        services.AddSingleton(sut.UnitOfWork.Object);
        var scopeProvider = services.BuildServiceProvider();

        using var doc = System.Text.Json.JsonDocument.Parse(
            "{\"service_type_name\":\"Corte\",\"client_whatsapp\":\"" + Phone + "\",\"fecha_inicio\":\"2026-09-05T10:00:00\"}");
        var args = doc.RootElement;

        var result = await sut.Service.CreateAppointmentAsync(
            args, scopeProvider, TenantId, Phone, "Cliente", availabilityConfirmed: true, CancellationToken.None);

        result.Should().Contain("\"stale\":true");
        sut.TurnFailures.Verify(
            r => r.AddAsync(
                It.Is<TurnFailure>(f =>
                    f.Motivo == "stale_availability"
                    && f.IdTenant == TenantId
                    && f.Detalle.Contains("prometido")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
