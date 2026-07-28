using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Moq;

namespace AgendaApi.Tests.UseCases;

public class SyncExternalChangesUseCaseTests
{
    private readonly Mock<IAppointmentRepository> _appointmentRepo = new();
    private readonly Mock<ICalendarConnectionRepository> _connectionRepo = new();
    private readonly Mock<ICalendarProviderFactory> _providerFactory = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ICalendarProvider> _calendarProvider = new();

    private readonly SyncExternalChangesUseCase _useCase;

    public SyncExternalChangesUseCaseTests()
    {
        _useCase = new SyncExternalChangesUseCase(
            _appointmentRepo.Object,
            _connectionRepo.Object,
            _providerFactory.Object,
            _unitOfWork.Object);
    }

    [Fact]
    public async Task ExecuteAsync_WithDeletedEvent_CancelsAppointment()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var connection = new CalendarConnection
        {
            IdTenant = tenantId,
            Activo = true,
            SyncToken = "delta_token_123",
            SyncChannelId = "channel_1"
        };

        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);
        _providerFactory.Setup(f => f.GetProviderAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _calendarProvider.Setup(p => p.GetChangesAsync(tenantId, "delta_token_123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalCalendarChange>
            {
                new() { ExternalEventId = "ext_deleted", Tipo = "deleted", Summary = "Evento cancelado" }
            });

        var existingAppointment = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = tenantId,
            ExternalEventId = "ext_deleted",
            Estado = "confirmed"
        };

        _appointmentRepo.Setup(r => r.GetByExternalEventIdAsync("ext_deleted", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingAppointment);

        // Act
        var count = await _useCase.ExecuteAsync(tenantId);

        // Assert
        count.Should().Be(1);
        _appointmentRepo.Verify(r => r.UpdateAsync(
            It.Is<Appointment>(a => a.Estado == "cancelled" && a.MotivoCancelacion == "Cancelado desde calendario externo"),
            It.IsAny<CancellationToken>()));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ExecuteAsync_WithUpdatedEvent_UpdatesAppointmentTimes()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var newStart = new DateTime(2026, 8, 10, 11, 0, 0, DateTimeKind.Utc);
        var newEnd = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        var connection = new CalendarConnection
        {
            IdTenant = tenantId,
            Activo = true,
            SyncToken = "delta_tok"
        };

        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);
        _providerFactory.Setup(f => f.GetProviderAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _calendarProvider.Setup(p => p.GetChangesAsync(tenantId, "delta_tok", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalCalendarChange>
            {
                new()
                {
                    ExternalEventId = "ext_updated",
                    Tipo = "updated",
                    FechaInicio = newStart,
                    FechaFin = newEnd,
                    Summary = "Cambio de horario"
                }
            });

        var appointment = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            ExternalEventId = "ext_updated",
            FechaInicio = DateTime.UtcNow.AddDays(1),
            FechaFin = DateTime.UtcNow.AddDays(1).AddHours(1)
        };

        _appointmentRepo.Setup(r => r.GetByExternalEventIdAsync("ext_updated", It.IsAny<CancellationToken>()))
            .ReturnsAsync(appointment);

        // Act
        var count = await _useCase.ExecuteAsync(tenantId);

        // Assert
        count.Should().Be(1);
        appointment.FechaInicio.Should().Be(newStart);
        appointment.FechaFin.Should().Be(newEnd);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoSyncToken_ReturnsZero()
    {
        // Arrange
        var connection = new CalendarConnection { IdTenant = Guid.NewGuid(), Activo = true, SyncToken = null };
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);

        // Act
        var count = await _useCase.ExecuteAsync(Guid.NewGuid());

        // Assert
        count.Should().Be(0);
        _providerFactory.Verify(f => f.GetProviderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenConnectionInactive_ReturnsZero()
    {
        // Arrange
        var connection = new CalendarConnection { IdTenant = Guid.NewGuid(), Activo = false };
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);

        // Act
        var count = await _useCase.ExecuteAsync(Guid.NewGuid());

        // Assert
        count.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WithCreatedEvent_SkipsProcessing()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var connection = new CalendarConnection { IdTenant = tenantId, Activo = true, SyncToken = "tok" };

        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);
        _providerFactory.Setup(f => f.GetProviderAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _calendarProvider.Setup(p => p.GetChangesAsync(tenantId, "tok", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalCalendarChange>
            {
                new() { ExternalEventId = "new_ext", Tipo = "created" }
            });

        // Act
        var count = await _useCase.ExecuteAsync(tenantId);

        // Assert
        count.Should().Be(0); // "created" events are skipped by design
        _appointmentRepo.Verify(r => r.UpdateAsync(It.IsAny<Appointment>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
