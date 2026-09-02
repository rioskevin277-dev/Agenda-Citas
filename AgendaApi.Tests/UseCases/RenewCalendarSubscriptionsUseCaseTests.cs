using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgendaApi.Tests.UseCases;

public class RenewCalendarSubscriptionsUseCaseTests
{
    private readonly Mock<ICalendarConnectionRepository> _connectionRepo = new();
    private readonly Mock<ICalendarProviderFactory> _providerFactory = new();
    private readonly Mock<ICalendarProvider> _calendarProvider = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly RenewCalendarSubscriptionsUseCase _useCase;

    public RenewCalendarSubscriptionsUseCaseTests()
    {
        _useCase = new RenewCalendarSubscriptionsUseCase(
            _connectionRepo.Object,
            _providerFactory.Object,
            new Mock<ILogger<RenewCalendarSubscriptionsUseCase>>().Object,
            _unitOfWork.Object);
    }

    private static CalendarConnection ActiveConnection(Guid tenantId) => new()
    {
        IdTenant = tenantId,
        Activo = true
    };

    [Fact]
    public async Task EnsureSubscriptionAsync_WhenNoChannel_CreatesAndSeedsToken()
    {
        var tenantId = Guid.NewGuid();
        var connection = ActiveConnection(tenantId);
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);
        _providerFactory.Setup(f => f.GetProviderAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _calendarProvider.Setup(p => p.SubscribeToChangesAsync(tenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("channel_1", "resource_1", DateTime.UtcNow.AddDays(3)));
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var result = await _useCase.EnsureSubscriptionAsync(tenantId, "https://api.example.com");

        result.Should().Be(1);
        _calendarProvider.Verify(p => p.SubscribeToChangesAsync(
            tenantId, It.Is<string>(u => u.StartsWith("https://api.example.com")), It.IsAny<CancellationToken>()), Times.Once);
        // Sin SyncToken, se hace un delta inicial (bootstrap) para seedear el token.
        _calendarProvider.Verify(p => p.GetChangesAsync(tenantId, string.Empty, It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureSubscriptionAsync_WhenChannelValidAndTokenSeeded_SkipsBoth()
    {
        var tenantId = Guid.NewGuid();
        var connection = ActiveConnection(tenantId);
        connection.SyncChannelId = "ch-valid";
        connection.SyncChannelExpiresAt = DateTime.UtcNow.AddDays(3);
        connection.SyncToken = "tok";
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);
        _providerFactory.Setup(f => f.GetProviderAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);

        var result = await _useCase.EnsureSubscriptionAsync(tenantId, "https://api.example.com");

        result.Should().Be(0);
        _calendarProvider.Verify(p => p.SubscribeToChangesAsync(tenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _calendarProvider.Verify(p => p.GetChangesAsync(tenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureSubscriptionAsync_WhenExpiring_RenewsSubscription()
    {
        var tenantId = Guid.NewGuid();
        var connection = ActiveConnection(tenantId);
        connection.SyncChannelId = "ch-exp";
        connection.SyncChannelExpiresAt = DateTime.UtcNow.AddHours(2); // expira < 24h
        connection.SyncToken = "tok";
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);
        _providerFactory.Setup(f => f.GetProviderAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var result = await _useCase.EnsureSubscriptionAsync(tenantId, "https://api.example.com");

        result.Should().Be(1);
        _calendarProvider.Verify(p => p.SubscribeToChangesAsync(tenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureSubscriptionAsync_WhenConnectionInactive_Skips()
    {
        var tenantId = Guid.NewGuid();
        var connection = ActiveConnection(tenantId);
        connection.Activo = false;
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);

        var result = await _useCase.EnsureSubscriptionAsync(tenantId, "https://api.example.com");

        result.Should().Be(0);
        _providerFactory.Verify(f => f.GetProviderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RenewAllAsync_WithoutBaseUrl_ReturnsZero()
    {
        var result = await _useCase.RenewAllAsync("   ");

        result.Should().Be(0);
        _connectionRepo.Verify(r => r.GetAllActiveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RenewAllAsync_OnlySubscribesConnectionsThatNeed()
    {
        var tenantNew = Guid.NewGuid();
        var tenantOk = Guid.NewGuid();
        var cNew = ActiveConnection(tenantNew);            // sin canal -> hay que suscribir
        var cOk = ActiveConnection(tenantOk);
        cOk.SyncChannelId = "ch";
        cOk.SyncChannelExpiresAt = DateTime.UtcNow.AddDays(5);
        cOk.SyncToken = "tok";

        _connectionRepo.Setup(r => r.GetAllActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CalendarConnection> { cOk, cNew });
        _providerFactory.Setup(f => f.GetProviderAsync(tenantNew, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _providerFactory.Setup(f => f.GetProviderAsync(tenantOk, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var result = await _useCase.RenewAllAsync("https://api.example.com");

        result.Should().Be(1);
        _calendarProvider.Verify(p => p.SubscribeToChangesAsync(tenantNew, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _calendarProvider.Verify(p => p.SubscribeToChangesAsync(tenantOk, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // Ambos tenants resuelven provider -> EnsureSubscriptionForConnectionAsync se ejecuta 2 veces,
        // persistiendo en cada una (aunque el segundo no modifique nada).
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RenewAllAsync_ProviderNotResolvable_ContinuesOthers()
    {
        var tenantNew = Guid.NewGuid();
        var tenantNoProvider = Guid.NewGuid();
        var cNew = ActiveConnection(tenantNew);
        var cNoProvider = ActiveConnection(tenantNoProvider);

        _connectionRepo.Setup(r => r.GetAllActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CalendarConnection> { cNoProvider, cNew });
        _providerFactory.Setup(f => f.GetProviderAsync(tenantNew, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _providerFactory.Setup(f => f.GetProviderAsync(tenantNoProvider, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ICalendarProvider?)null);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var result = await _useCase.RenewAllAsync("https://api.example.com");

        result.Should().Be(1);
        _calendarProvider.Verify(p => p.SubscribeToChangesAsync(tenantNew, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        // Solo tenantNew resuelve provider -> una sola persistencia.
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}