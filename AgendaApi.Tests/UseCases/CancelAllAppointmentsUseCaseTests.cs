using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgendaApi.Tests.UseCases;

public class CancelAllAppointmentsUseCaseTests
{
    private readonly Mock<IAppointmentRepository> _appointmentRepo = new();
    private readonly Mock<ICalendarProviderFactory> _providerFactory = new();
    private readonly Mock<IClientRepository> _clientRepo = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ICalendarProvider> _calendarProvider = new();
    private readonly Mock<IWaitlistNotifier> _waitlistNotifier = new();
    private readonly Mock<ILogger<CancelAllAppointmentsUseCase>> _logger = new();

    private readonly CancelAllAppointmentsUseCase _useCase;

    private const string WhatsApp = "521234567890";
    private static readonly Guid TenantId = Guid.NewGuid();

    public CancelAllAppointmentsUseCaseTests()
    {
        _useCase = new CancelAllAppointmentsUseCase(
            _appointmentRepo.Object,
            _clientRepo.Object,
            _providerFactory.Object,
            _unitOfWork.Object,
            _waitlistNotifier.Object,
            _logger.Object);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoActiveAppointments_ReturnsZero_NeverSaves()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var client = new Client { IdClient = clientId, WhatsApp = WhatsApp };

        _clientRepo.Setup(r => r.GetByWhatsAppAsync(WhatsApp, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                // Solo historial: una cancelada futura no cuenta como activa
                new()
                {
                    IdAppointment = Guid.NewGuid(),
                    IdTenant = TenantId,
                    IdClient = clientId,
                    Estado = "cancelled",
                    FechaInicio = DateTime.UtcNow.AddDays(1),
                    FechaFin = DateTime.UtcNow.AddDays(1).AddHours(1)
                }
            });

        // Act
        var result = await _useCase.ExecuteAsync(WhatsApp, TenantId);

        // Assert
        result.Should().NotBeNull();
        result!.CancelledCount.Should().Be(0);
        result.Message.Should().NotBeNullOrWhiteSpace();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _providerFactory.Verify(f => f.GetProviderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WithThreeActiveAppointments_CancelsAll_InSingleCommit()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var client = new Client { IdClient = clientId, WhatsApp = WhatsApp };
        var actives = new List<Appointment>
        {
            NewAppointment(clientId, "pending", DateTime.UtcNow.AddDays(1), "ext_1"),
            NewAppointment(clientId, "confirmed", DateTime.UtcNow.AddDays(2), "ext_2"),
            NewAppointment(clientId, "pending", DateTime.UtcNow.AddDays(3), null)
        };

        _clientRepo.Setup(r => r.GetByWhatsAppAsync(WhatsApp, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>(actives)
            {
                // Historial que NO debe tocarse
                NewAppointment(clientId, "cancelled", DateTime.UtcNow.AddDays(-5), "ext_old")
            });
        _providerFactory.Setup(f => f.GetProviderAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _calendarProvider.Setup(p => p.CancelEventAsync(TenantId, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _useCase.ExecuteAsync(WhatsApp, TenantId);

        // Assert
        result.Should().NotBeNull();
        result!.CancelledCount.Should().Be(3);
        result.CalendarFailures.Should().BeEmpty();

        actives.Should().OnlyContain(a => a.Estado == "cancelled");
        actives.Should().OnlyContain(a => a.FechaActualizacion != default);

        _appointmentRepo.Verify(
            r => r.UpdateAsync(It.Is<Appointment>(a => a.Estado == "cancelled"), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCalendarThrowsForOneAppointment_LocalCancellationStillSucceeds_AndReportsFailure()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var client = new Client { IdClient = clientId, WhatsApp = WhatsApp };
        var actives = new List<Appointment>
        {
            NewAppointment(clientId, "pending", DateTime.UtcNow.AddDays(1), "ext_1"),
            NewAppointment(clientId, "confirmed", DateTime.UtcNow.AddDays(2), "ext_2"),
            NewAppointment(clientId, "pending", DateTime.UtcNow.AddDays(3), "ext_3")
        };

        _clientRepo.Setup(r => r.GetByWhatsAppAsync(WhatsApp, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(actives);
        _providerFactory.Setup(f => f.GetProviderAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _calendarProvider.Setup(p => p.CancelEventAsync(TenantId, "ext_2", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Google API down"));
        _calendarProvider.Setup(p => p.CancelEventAsync(TenantId, "ext_1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _calendarProvider.Setup(p => p.CancelEventAsync(TenantId, "ext_3", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _useCase.ExecuteAsync(WhatsApp, TenantId);

        // Assert: la cancelación local es exitosa para las 3 y el fallo de calendario se reporta
        result.Should().NotBeNull();
        result!.CancelledCount.Should().Be(3);
        actives.Should().OnlyContain(a => a.Estado == "cancelled");
        result.CalendarFailures.Should().ContainSingle()
            .Which.Should().Contain("ext_2");
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenClientNotFound_ReturnsZero_NeverSaves()
    {
        // Arrange
        _clientRepo.Setup(r => r.GetByWhatsAppAsync(WhatsApp, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Client?)null);

        // Act
        var result = await _useCase.ExecuteAsync(WhatsApp, TenantId);

        // Assert
        result.Should().NotBeNull();
        result!.CancelledCount.Should().Be(0);
        result.Message.Should().NotBeNullOrWhiteSpace();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLocalSaveThrows_Propagates_AndLogsErrorWithContext()
    {
        // Arrange: el peor caso de divergencia — el calendario externo YA falló para una cita
        // y el commit local también revienta. Sin log, la lista de fallos muere con la excepción.
        var clientId = Guid.NewGuid();
        var client = new Client { IdClient = clientId, WhatsApp = WhatsApp };
        var actives = new List<Appointment>
        {
            NewAppointment(clientId, "pending", DateTime.UtcNow.AddDays(1), "ext_1")
        };

        _clientRepo.Setup(r => r.GetByWhatsAppAsync(WhatsApp, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(actives);
        _providerFactory.Setup(f => f.GetProviderAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _calendarProvider.Setup(p => p.CancelEventAsync(TenantId, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Google API down"));

        var dbError = new InvalidOperationException("db down");
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(dbError);

        // Act
        Func<Task> act = () => _useCase.ExecuteAsync(WhatsApp, TenantId);

        // Assert: la excepción original se propaga (no se traga)...
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("db down");

        // ...y la divergencia cross-system queda visible en el log de errores (conteo +
        // eventos externos que no se pudieron quitar del calendario).
        _logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains("ext_1")),
                dbError,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private static Appointment NewAppointment(Guid clientId, string estado, DateTime fechaInicio, string? externalEventId)
    {
        return new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = TenantId,
            IdClient = clientId,
            Estado = estado,
            ExternalEventId = externalEventId,
            FechaInicio = fechaInicio,
            FechaFin = fechaInicio.AddHours(1)
        };
    }
}
