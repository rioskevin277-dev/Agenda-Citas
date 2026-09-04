using AgendaApi.Api.Controllers;
using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgendaApi.Tests.Controllers;

/// <summary>
/// Gate del wiring RF3 que conecta la notificación de calendario externo con el dirty flag
/// por-tenant: cuando el webhook recibe una notificación y dispara la sync de cambios externos,
/// el tenant debe quedar marcado como "sucio" para que el orquestador fuerce un re-check de
/// disponibilidad en su próximo turno (sin acoplarse a la lógica interna de la sync).
/// </summary>
public class WebhookControllerTests
{
    private readonly Mock<ITenantContext> _tenantContext = new();

    /// <summary>
    /// Scope mínimo que devuelve una instancia real de <see cref="SyncExternalChangesUseCase"/>
    /// con repos mockeados (sin conexión → sync no-op, rápida). Aísla el wiring del flag dirty del
    /// comportamiento de la sync en sí.
    /// </summary>
    private sealed class TestScope(ServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = provider;
        public void Dispose() { }
    }

    private static MessageBufferService BuildMessageBuffer(IServiceScopeFactory scopeFactory)
        => new(NullLogger<MessageBufferService>.Instance, scopeFactory);

    private WebhookController CreateController(ConversationStateService state, IServiceScopeFactory scopeFactory)
    {
        var controller = new WebhookController(
            Mock.Of<IMessagingProvider>(),
            BuildMessageBuffer(scopeFactory),
            Mock.Of<ITenantRepository>(),
            _tenantContext.Object,
            scopeFactory,
            state,
            NullLogger<WebhookController>.Instance);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static IServiceScopeFactory BuildScopeFactory(SyncExternalChangesUseCase useCase)
    {
        var services = new ServiceCollection();
        services.AddSingleton(useCase);
        var provider = services.BuildServiceProvider();

        var scope = new TestScope(provider);
        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope);
        return factory.Object;
    }

    private static SyncExternalChangesUseCase BuildNoopSync()
        => new(
            new Mock<IAppointmentRepository>().Object,
            new Mock<ICalendarConnectionRepository>().Object,
            new Mock<ICalendarProviderFactory>().Object,
            new Mock<IUnitOfWork>().Object);

    [Fact]
    public async Task CalendarNotification_AfterSync_MarksTenantDirty()
    {
        // Tenant registrado en el payload como token del channel (resolución directa sin repo).
        var tenantId = Guid.NewGuid();
        var state = new ConversationStateService(NullLogger<ConversationStateService>.Instance);
        var factory = BuildScopeFactory(BuildNoopSync());
        var controller = CreateController(state, factory);

        controller.Request.Headers["X-Goog-Channel-ID"] = "channel-1";
        controller.Request.Headers["X-Goog-Resource-State"] = "updated";
        controller.Request.Headers["X-Goog-Channel-Token"] = tenantId.ToString();

        await controller.CalendarNotification();

        // La sync corre fire-and-forget: esperar hasta que el flag quede marcado (o timeout).
        bool becameDirty = false;
        for (var i = 0; i < 50 && !becameDirty; i++)
        {
            await Task.Delay(100);
            becameDirty = state.ConsumeTenantDirty(tenantId);
        }

        becameDirty.Should().BeTrue("tras la notificación de calendario el tenant debe quedar marcado sucio");
    }

    [Fact]
    public async Task CalendarNotification_NonSyncResourceState_DoesNotMarkDirty()
    {
        // Triangulation: cuando el estado del recurso no es "exists" ni "updated",
        // no se dispara la sync y el flag dirty no se marca.
        var tenantId = Guid.NewGuid();
        var state = new ConversationStateService(NullLogger<ConversationStateService>.Instance);
        var factory = BuildScopeFactory(BuildNoopSync());
        var controller = CreateController(state, factory);

        controller.Request.Headers["X-Goog-Channel-ID"] = "channel-2";
        controller.Request.Headers["X-Goog-Resource-State"] = "sync";
        controller.Request.Headers["X-Goog-Channel-Token"] = tenantId.ToString();

        await controller.CalendarNotification();

        // Espera POR ESTADO (no un sleep ciego de 500ms): la sync corre fire-and-forget, así que
        // se observa una ventana acotada cubriendo su completado. Si en CUALQUIER punto el flag se
        // marca, el assert falla de inmediato — sin falso-pass por carga extrema (follow-up SDD).
        for (var i = 0; i < 50; i++)
        {
            state.ConsumeTenantDirty(tenantId).Should().BeFalse("resource-state 'sync' no debe disparar dirty");
            await Task.Delay(100);
        }
    }
}