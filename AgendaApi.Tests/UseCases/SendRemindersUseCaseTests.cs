using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgendaApi.Tests.UseCases;

public class SendRemindersUseCaseTests
{
    private readonly Mock<IAppointmentRepository> _appointmentRepo = new();
    private readonly Mock<IClientRepository> _clientRepo = new();
    private readonly Mock<ICalendarProviderFactory> _providerFactory = new();
    private readonly Mock<ICalendarConnectionRepository> _connectionRepo = new();
    private readonly Mock<IMessagingProvider> _messagingProvider = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ILogger<SendRemindersUseCase>> _logger = new();

    private readonly SendRemindersUseCase _useCase;

    public SendRemindersUseCaseTests()
    {
        _useCase = new SendRemindersUseCase(
            _appointmentRepo.Object,
            _clientRepo.Object,
            _providerFactory.Object,
            _connectionRepo.Object,
            _messagingProvider.Object,
            _unitOfWork.Object,
            _logger.Object);
    }

    [Fact]
    public async Task ExecuteAsync_SendsReminderAndUpdatesAppointment()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var appointment = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdClient = clientId,
            FechaInicio = DateTime.UtcNow.AddDays(1),
            Estado = "confirmed"
        };

        _appointmentRepo.Setup(r => r.GetPendingRemindersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment> { appointment });
        _clientRepo.Setup(r => r.GetByIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Client { IdClient = clientId, WhatsApp = "521234567890", Nombre = "Juan" });
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert
        count.Should().Be(1);
        appointment.RecordatorioEnviadoEn.Should().NotBeNull();
        _appointmentRepo.Verify(r => r.UpdateAsync(appointment, It.IsAny<CancellationToken>()));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ExecuteAsync_NoPendingReminders_ReturnsZero()
    {
        // Arrange
        _appointmentRepo.Setup(r => r.GetPendingRemindersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert
        count.Should().Be(0);
        _messagingProvider.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SendsMultipleReminders()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var appointments = Enumerable.Range(0, 3).Select(i => new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdClient = clientId,
            FechaInicio = DateTime.UtcNow.AddDays(i + 1),
            Estado = "confirmed"
        }).ToList();

        _appointmentRepo.Setup(r => r.GetPendingRemindersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(appointments);
        _clientRepo.Setup(r => r.GetByIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Client { IdClient = clientId, WhatsApp = "521234567890", Nombre = "Juan" });
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert
        count.Should().Be(3);
        _messagingProvider.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        _appointmentRepo.Verify(r => r.UpdateAsync(It.IsAny<Appointment>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task ExecuteAsync_WhenClientNotFound_SkipsAppointment()
    {
        // Arrange
        var appointment = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdClient = Guid.NewGuid(),
            FechaInicio = DateTime.UtcNow.AddDays(1),
            Estado = "confirmed"
        };

        _appointmentRepo.Setup(r => r.GetPendingRemindersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment> { appointment });
        _clientRepo.Setup(r => r.GetByIdAsync(appointment.IdClient, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Client?)null);

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert
        count.Should().Be(0);
        _messagingProvider.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSendFails_SkipsThatAppointment()
    {
        // Arrange
        var goodClientId = Guid.NewGuid();
        var badClientId = Guid.NewGuid();
        var goodAppointment = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdClient = goodClientId,
            FechaInicio = DateTime.UtcNow.AddDays(1),
            Estado = "confirmed"
        };
        var badAppointment = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdClient = badClientId,
            FechaInicio = DateTime.UtcNow.AddDays(2),
            Estado = "confirmed"
        };

        _appointmentRepo.Setup(r => r.GetPendingRemindersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment> { goodAppointment, badAppointment });

        _clientRepo.Setup(r => r.GetByIdAsync(goodClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Client { IdClient = goodClientId, WhatsApp = "5211111111", Nombre = "Ana" });
        _clientRepo.Setup(r => r.GetByIdAsync(badClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Client { IdClient = badClientId, WhatsApp = "5212222222", Nombre = "Luis" });

        _messagingProvider.Setup(m => m.SendTextAsync("5211111111", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _messagingProvider.Setup(m => m.SendTextAsync("5212222222", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Error de red"));

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert
        count.Should().Be(1); // Only one succeeds
        _appointmentRepo.Verify(r => r.UpdateAsync(goodAppointment, It.IsAny<CancellationToken>()), Times.Once);
        _appointmentRepo.Verify(r => r.UpdateAsync(badAppointment, It.IsAny<CancellationToken>()), Times.Never);
    }
}
