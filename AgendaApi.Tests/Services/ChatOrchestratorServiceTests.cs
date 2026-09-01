using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.AiProviders;
using AgendaApi.Infrastructure.Services;
using FluentAssertions;
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
    private static Sut BuildSut(Exception transportError)
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

        var provider = services.BuildServiceProvider();

        var sut = new ChatOrchestratorService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ConversationMemoryService>(),
            provider.GetRequiredService<ConversationStateService>(),
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
}
