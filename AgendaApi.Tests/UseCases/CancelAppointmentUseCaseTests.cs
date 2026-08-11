using AgendaApi.Application.DTOs;
using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Moq;

namespace AgendaApi.Tests.UseCases;

public class CancelAppointmentUseCaseTests
{
    private readonly Mock<IAppointmentRepository> _appointmentRepo = new();
    private readonly Mock<ICalendarConnectionRepository> _connectionRepo = new();
    private readonly Mock<ICalendarProviderFactory> _providerFactory = new();
    private readonly Mock<IClientRepository> _clientRepo = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ICalendarProvider> _calendarProvider = new();

    private readonly CancelAppointmentUseCase _useCase;

    public CancelAppointmentUseCaseTests()
    {
        _useCase = new CancelAppointmentUseCase(
            _appointmentRepo.Object,
            _connectionRepo.Object,
            _providerFactory.Object,
            _clientRepo.Object,
            _unitOfWork.Object);
    }

    [Fact]
    public async Task ExecuteAsync_WithValidAppointmentId_CancelsSuccessfully()
    {
        // Arrange
        var appointmentId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var appointment = new Appointment
        {
            IdAppointment = appointmentId,
            IdTenant = tenantId,
            IdClient = Guid.NewGuid(),
            Estado = "confirmed",
            ExternalEventId = "ext_123",
            FechaInicio = DateTime.UtcNow.AddDays(1),
            FechaFin = DateTime.UtcNow.AddDays(1).AddHours(1)
        };

        _appointmentRepo.Setup(r => r.GetByIdAsync(appointmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(appointment);
        _providerFactory.Setup(f => f.GetProviderAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _calendarProvider.Setup(p => p.CancelEventAsync(tenantId, "ext_123", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var dto = new AppointmentCancelDto
        {
            AppointmentId = appointmentId,
            Motivo = "Cliente canceló"
        };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(appointmentId);
        result.Status.Should().Be("cancelled");
        _appointmentRepo.Verify(r => r.UpdateAsync(It.Is<Appointment>(a => a.Estado == "cancelled"), It.IsAny<CancellationToken>()));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ExecuteAsync_WithWhatsAppIdentifier_FindsNextUpcoming()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var client = new Client { IdClient = clientId, WhatsApp = "521234567890" };
        var futureAppointment = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = tenantId,
            IdClient = clientId,
            Estado = "pending",
            FechaInicio = DateTime.UtcNow.AddDays(2),
            FechaFin = DateTime.UtcNow.AddDays(2).AddHours(1),
            ExternalEventId = "ext_456"
        };

        _clientRepo.Setup(r => r.GetByWhatsAppAsync("521234567890", tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                futureAppointment,
                new() { IdAppointment = Guid.NewGuid(), IdClient = clientId, Estado = "cancelled" }
            });
        _providerFactory.Setup(f => f.GetProviderAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _calendarProvider.Setup(p => p.CancelEventAsync(tenantId, "ext_456", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var dto = new AppointmentCancelDto
        {
            AppointmentIdentifier = "521234567890",
            TenantId = tenantId
        };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert
        result.Should().NotBeNull();
        result!.Status.Should().Be("cancelled");
    }

    [Fact]
    public async Task ExecuteAsync_WithWhatsAppIdentifier_CancelsNextFuturePending()
    {
        // Arrange: el cliente tiene una confirmada pasada en historial (que se acumula)
        // y una pendiente futura. CANCELAR debe apuntar a la FUTURA, no a la vieja
        // (mismo fix que CONFIRMAR: filtro de reloj de negocio).
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var client = new Client { IdClient = clientId, WhatsApp = "521234567890" };
        var pastConfirmed = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = tenantId,
            IdClient = clientId,
            Estado = "confirmed",
            FechaInicio = DateTime.UtcNow.AddDays(-5),
            FechaFin = DateTime.UtcNow.AddDays(-5).AddHours(1),
            ExternalEventId = "ext_past"
        };
        var futurePending = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = tenantId,
            IdClient = clientId,
            Estado = "pending",
            FechaInicio = DateTime.UtcNow.AddDays(2),
            FechaFin = DateTime.UtcNow.AddDays(2).AddHours(1),
            ExternalEventId = "ext_future"
        };

        _clientRepo.Setup(r => r.GetByWhatsAppAsync("521234567890", tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment> { pastConfirmed, futurePending });
        _providerFactory.Setup(f => f.GetProviderAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _calendarProvider.Setup(p => p.CancelEventAsync(tenantId, "ext_future", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var dto = new AppointmentCancelDto
        {
            AppointmentIdentifier = "521234567890",
            TenantId = tenantId
        };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert: cancela la pendiente futura, no la confirmada pasada
        result.Should().NotBeNull();
        result!.Id.Should().Be(futurePending.IdAppointment);
        result.Status.Should().Be("cancelled");
        futurePending.Estado.Should().Be("cancelled");
        pastConfirmed.Estado.Should().Be("confirmed");
        _calendarProvider.Verify(p => p.CancelEventAsync(tenantId, "ext_future", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithWhatsAppIdentifier_NoFutureAppointments_ThrowsNotFound()
    {
        // Arrange: solo citas pasadas — no hay nada futura que cancelar.
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var client = new Client { IdClient = clientId, WhatsApp = "521234567890" };
        var pastPending = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = tenantId,
            IdClient = clientId,
            Estado = "pending",
            FechaInicio = DateTime.UtcNow.AddDays(-1),
            FechaFin = DateTime.UtcNow.AddDays(-1).AddHours(1)
        };

        _clientRepo.Setup(r => r.GetByWhatsAppAsync("521234567890", tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment> { pastPending });

        var dto = new AppointmentCancelDto
        {
            AppointmentIdentifier = "521234567890",
            TenantId = tenantId
        };

        // Act
        var act = async () => await _useCase.ExecuteAsync(dto);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Cita no encontrada");
    }

    [Fact]
    public async Task ExecuteAsync_WhenAlreadyCancelled_ThrowsInvalidOperation()
    {
        // Arrange
        var appointmentId = Guid.NewGuid();
        var appointment = new Appointment
        {
            IdAppointment = appointmentId,
            IdTenant = Guid.NewGuid(),
            Estado = "cancelled"
        };

        _appointmentRepo.Setup(r => r.GetByIdAsync(appointmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(appointment);

        var dto = new AppointmentCancelDto { AppointmentId = appointmentId };

        // Act
        var act = async () => await _useCase.ExecuteAsync(dto);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("La cita ya está cancelada");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCompleted_ThrowsInvalidOperation()
    {
        // Arrange
        var appointment = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = Guid.NewGuid(),
            Estado = "completed"
        };

        _appointmentRepo.Setup(r => r.GetByIdAsync(appointment.IdAppointment, It.IsAny<CancellationToken>()))
            .ReturnsAsync(appointment);

        var dto = new AppointmentCancelDto { AppointmentId = appointment.IdAppointment };

        // Act
        var act = async () => await _useCase.ExecuteAsync(dto);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("La cita ya finalizó y no se puede cancelar");
    }

    [Fact]
    public async Task ExecuteAsync_WhenAppointmentNotFound_ThrowsInvalidOperation()
    {
        // Arrange
        _appointmentRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Appointment?)null);

        var dto = new AppointmentCancelDto { AppointmentId = Guid.NewGuid() };

        // Act
        var act = async () => await _useCase.ExecuteAsync(dto);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Cita no encontrada");
    }

    [Fact]
    public async Task ExecuteAsync_WithoutExternalEventId_SkipsCalendarCancel()
    {
        // Arrange
        var appointmentId = Guid.NewGuid();
        var appointment = new Appointment
        {
            IdAppointment = appointmentId,
            IdTenant = Guid.NewGuid(),
            IdClient = Guid.NewGuid(),
            Estado = "confirmed",
            ExternalEventId = null
        };

        _appointmentRepo.Setup(r => r.GetByIdAsync(appointmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(appointment);

        var dto = new AppointmentCancelDto { AppointmentId = appointmentId };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert
        result.Should().NotBeNull();
        _providerFactory.Verify(f => f.GetProviderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
