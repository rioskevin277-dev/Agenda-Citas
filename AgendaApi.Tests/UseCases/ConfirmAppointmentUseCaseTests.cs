using AgendaApi.Application.DTOs;
using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Moq;

namespace AgendaApi.Tests.UseCases;

public class ConfirmAppointmentUseCaseTests
{
    private readonly Mock<IAppointmentRepository> _appointmentRepo = new();
    private readonly Mock<IClientRepository> _clientRepo = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private readonly ConfirmAppointmentUseCase _useCase;

    public ConfirmAppointmentUseCaseTests()
    {
        _useCase = new ConfirmAppointmentUseCase(
            _appointmentRepo.Object,
            _clientRepo.Object,
            _unitOfWork.Object);
    }

    [Fact]
    public async Task ExecuteAsync_WithWhatsAppIdentifier_ConfirmsNextFuturePending()
    {
        // Arrange: el cliente tiene una cita pasada confirmada (que se acumula en el
        // historial) y una futura pendiente. CONFIRMAR debe apuntar a la FUTURA, no a la vieja.
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
            FechaFin = DateTime.UtcNow.AddDays(-5).AddHours(1)
        };
        var futurePending = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = tenantId,
            IdClient = clientId,
            Estado = "pending",
            FechaInicio = DateTime.UtcNow.AddDays(2),
            FechaFin = DateTime.UtcNow.AddDays(2).AddHours(1)
        };

        _clientRepo.Setup(r => r.GetByWhatsAppAsync("521234567890", tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment> { pastConfirmed, futurePending });

        var dto = new AppointmentCancelDto
        {
            AppointmentIdentifier = "521234567890",
            TenantId = tenantId
        };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert: confirma la pendiente futura (no la vieja confirmada)
        result.Should().NotBeNull();
        result!.Id.Should().Be(futurePending.IdAppointment);
        result.Status.Should().Be("confirmed");
        futurePending.Estado.Should().Be("confirmed");
        futurePending.ConfirmadoEn.Should().NotBeNull();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithWhatsAppIdentifier_RefreshesConfirmadoEnOnConfirmedFuture()
    {
        // Arrange: cita futura ya confirmada — re-confirmar refresca ConfirmadoEn.
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var client = new Client { IdClient = clientId, WhatsApp = "521234567890" };
        var futureConfirmed = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = tenantId,
            IdClient = clientId,
            Estado = "confirmed",
            ConfirmadoEn = DateTime.UtcNow.AddDays(-1),
            FechaInicio = DateTime.UtcNow.AddDays(3),
            FechaFin = DateTime.UtcNow.AddDays(3).AddHours(1)
        };

        _clientRepo.Setup(r => r.GetByWhatsAppAsync("521234567890", tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment> { futureConfirmed });

        var dto = new AppointmentCancelDto
        {
            AppointmentIdentifier = "521234567890",
            TenantId = tenantId
        };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(futureConfirmed.IdAppointment);
        result.Status.Should().Be("confirmed");
        _appointmentRepo.Verify(r => r.UpdateAsync(
            It.Is<Appointment>(a => a.IdAppointment == futureConfirmed.IdAppointment && a.ConfirmadoEn.HasValue),
            It.IsAny<CancellationToken>()));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithWhatsAppIdentifier_NoFutureAppointments_ThrowsNotFound()
    {
        // Arrange: solo citas pasadas — no hay nada futura que confirmar.
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
    public async Task ExecuteAsync_WithValidAppointmentId_ConfirmsById()
    {
        // Arrange
        var appointmentId = Guid.NewGuid();
        var appointment = new Appointment
        {
            IdAppointment = appointmentId,
            IdTenant = Guid.NewGuid(),
            IdClient = Guid.NewGuid(),
            Estado = "pending",
            FechaInicio = DateTime.UtcNow.AddDays(1),
            FechaFin = DateTime.UtcNow.AddDays(1).AddHours(1)
        };

        _appointmentRepo.Setup(r => r.GetByIdAsync(appointmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(appointment);

        var dto = new AppointmentCancelDto { AppointmentId = appointmentId };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(appointmentId);
        result.Status.Should().Be("confirmed");
        appointment.ConfirmadoEn.Should().NotBeNull();
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
    public async Task ExecuteAsync_WhenCompleted_ThrowsInvalidOperation()
    {
        // Arrange
        var appointment = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = Guid.NewGuid(),
            Estado = "completed",
            FechaInicio = DateTime.UtcNow.AddDays(-1),
            FechaFin = DateTime.UtcNow.AddDays(-1).AddHours(1)
        };

        _appointmentRepo.Setup(r => r.GetByIdAsync(appointment.IdAppointment, It.IsAny<CancellationToken>()))
            .ReturnsAsync(appointment);

        var dto = new AppointmentCancelDto { AppointmentId = appointment.IdAppointment };

        // Act
        var act = async () => await _useCase.ExecuteAsync(dto);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("La cita ya finalizó y no se puede confirmar");
    }
}
