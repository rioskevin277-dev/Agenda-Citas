using AgendaApi.Application.DTOs;
using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Moq;

namespace AgendaApi.Tests.UseCases;

public class RescheduleAppointmentUseCaseTests
{
    private readonly Mock<IAppointmentRepository> _appointmentRepo = new();
    private readonly Mock<IServiceTypeRepository> _serviceTypeRepo = new();
    private readonly Mock<ICalendarConnectionRepository> _connectionRepo = new();
    private readonly Mock<ICalendarProviderFactory> _providerFactory = new();
    private readonly Mock<IClientRepository> _clientRepo = new();
    private readonly Mock<IMessagingProvider> _messagingProvider = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ICalendarProvider> _calendarProvider = new();

    private readonly RescheduleAppointmentUseCase _useCase;

    public RescheduleAppointmentUseCaseTests()
    {
        _useCase = new RescheduleAppointmentUseCase(
            _appointmentRepo.Object,
            _serviceTypeRepo.Object,
            _connectionRepo.Object,
            _providerFactory.Object,
            _clientRepo.Object,
            _messagingProvider.Object,
            _unitOfWork.Object);
    }

    [Fact]
    public async Task ExecuteAsync_WithValidData_ReschedulesSuccessfully()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var appointmentId = Guid.NewGuid();
        var appointment = new Appointment
        {
            IdAppointment = appointmentId,
            IdTenant = tenantId,
            IdServiceType = Guid.NewGuid(),
            Estado = "confirmed",
            ExternalEventId = "ext_123",
            FechaInicio = DateTime.UtcNow.AddDays(1),
            FechaFin = DateTime.UtcNow.AddDays(1).AddHours(1)
        };

        _appointmentRepo.Setup(r => r.GetByIdAsync(appointmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(appointment);
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());
        _providerFactory.Setup(f => f.GetProviderAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _serviceTypeRepo.Setup(r => r.GetByIdAsync(appointment.IdServiceType, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceType
            {
                IdServiceType = appointment.IdServiceType,
                IdTenant = tenantId,
                DuracionMinutos = 60,
                BufferMinutos = 15
            });
        _clientRepo.Setup(r => r.GetByIdAsync(appointment.IdClient, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Client { WhatsApp = "521234567890" });
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var newStart = new DateTime(2026, 8, 5, 15, 0, 0, DateTimeKind.Utc);

        var dto = new AppointmentRescheduleDto
        {
            AppointmentId = appointmentId,
            NuevaFechaInicio = newStart,
            TenantId = tenantId
        };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert
        result.Should().NotBeNull();
        result!.FechaInicio.Should().Be(newStart);
        _calendarProvider.Verify(p => p.UpdateEventAsync(It.Is<Appointment>(a => a.FechaInicio == newStart), It.IsAny<CancellationToken>()));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ExecuteAsync_WithoutNuevaFechaFin_CalculatesEndTime()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var appointmentId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();
        var appointment = new Appointment
        {
            IdAppointment = appointmentId,
            IdTenant = tenantId,
            IdServiceType = serviceTypeId,
            Estado = "confirmed",
            ExternalEventId = "ext_789"
        };

        _appointmentRepo.Setup(r => r.GetByIdAsync(appointmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(appointment);
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());
        _providerFactory.Setup(f => f.GetProviderAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_calendarProvider.Object);
        _serviceTypeRepo.Setup(r => r.GetByIdAsync(serviceTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceType
            {
                IdServiceType = serviceTypeId,
                IdTenant = tenantId,
                DuracionMinutos = 45,
                BufferMinutos = 10
            });
        _clientRepo.Setup(r => r.GetByIdAsync(appointment.IdClient, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Client { WhatsApp = "521234567890" });
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var newStart = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc);
        var expectedEnd = newStart.AddMinutes(55); // 45 + 10

        var dto = new AppointmentRescheduleDto
        {
            AppointmentId = appointmentId,
            NuevaFechaInicio = newStart,
            TenantId = tenantId
        };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert
        result.Should().NotBeNull();
        result!.FechaFin.Should().Be(expectedEnd);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAppointmentNotFound_Throws()
    {
        // Arrange
        _appointmentRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Appointment?)null);

        var dto = new AppointmentRescheduleDto
        {
            AppointmentId = Guid.NewGuid(),
            NuevaFechaInicio = DateTime.UtcNow,
            TenantId = Guid.NewGuid()
        };

        // Act
        var act = async () => await _useCase.ExecuteAsync(dto);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no encontrada*");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancelled_Throws()
    {
        // Arrange
        var appointment = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = Guid.NewGuid(),
            Estado = "cancelled"
        };

        _appointmentRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(appointment);

        var dto = new AppointmentRescheduleDto
        {
            AppointmentId = Guid.NewGuid(),
            NuevaFechaInicio = DateTime.UtcNow,
            TenantId = Guid.NewGuid()
        };

        // Act
        var act = async () => await _useCase.ExecuteAsync(dto);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cancelada*");
    }
}
