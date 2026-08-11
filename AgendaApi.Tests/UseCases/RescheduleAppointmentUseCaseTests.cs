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
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ICalendarProvider> _calendarProvider = new();
    private readonly Mock<IBookingPolicy> _bookingPolicy = new();

    private readonly RescheduleAppointmentUseCase _useCase;

    public RescheduleAppointmentUseCaseTests()
    {
        _bookingPolicy.Setup(p => p.ValidateAsync(
                It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BookingValidationResult.Ok());

        _useCase = new RescheduleAppointmentUseCase(
            _appointmentRepo.Object,
            _serviceTypeRepo.Object,
            _connectionRepo.Object,
            _providerFactory.Object,
            _clientRepo.Object,
            _unitOfWork.Object,
            _bookingPolicy.Object);
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
        // Por AppointmentId (API/owner) el estado confirmado se mantiene: no se re-confirma.
        result.Status.Should().Be("confirmed");
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
    public async Task ExecuteAsync_WithWhatsAppIdentifier_ReschedulesNextFuturePending()
    {
        // Arrange: historial con confirmada pasada + pendiente futura. REAGENDAR debe
        // apuntar a la FUTURA (filtro de reloj de negocio, mismo fix que CONFIRMAR).
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();
        var client = new Client { IdClient = clientId, WhatsApp = "521234567890" };
        var pastConfirmed = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = tenantId,
            IdClient = clientId,
            IdServiceType = serviceTypeId,
            Estado = "confirmed",
            FechaInicio = DateTime.UtcNow.AddDays(-5),
            FechaFin = DateTime.UtcNow.AddDays(-5).AddHours(1)
        };
        var futurePending = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = tenantId,
            IdClient = clientId,
            IdServiceType = serviceTypeId,
            Estado = "pending",
            FechaInicio = DateTime.UtcNow.AddDays(2),
            FechaFin = DateTime.UtcNow.AddDays(2).AddHours(1)
        };

        _clientRepo.Setup(r => r.GetByWhatsAppAsync("521234567890", tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment> { pastConfirmed, futurePending });
        _serviceTypeRepo.Setup(r => r.GetByIdAsync(serviceTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceType { IdServiceType = serviceTypeId, IdTenant = tenantId, DuracionMinutos = 30, BufferMinutos = 5 });

        var newStart = new DateTime(2026, 8, 20, 14, 0, 0, DateTimeKind.Utc);
        var dto = new AppointmentRescheduleDto
        {
            AppointmentIdentifier = "521234567890",
            TenantId = tenantId,
            NuevaFechaInicio = newStart
        };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert: reprograma la pendiente futura, no la confirmada pasada
        result.Should().NotBeNull();
        result!.Id.Should().Be(futurePending.IdAppointment);
        result.FechaInicio.Should().Be(newStart);
        result.Status.Should().Be("pending");
        pastConfirmed.Estado.Should().Be("confirmed");
        pastConfirmed.FechaInicio.Should().NotBe(newStart);
    }

    [Fact]
    public async Task ExecuteAsync_WithWhatsAppIdentifier_NoFutureAppointments_ThrowsNotFound()
    {
        // Arrange: solo citas pasadas — no hay nada futura que reprogramar.
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var client = new Client { IdClient = clientId, WhatsApp = "521234567890" };
        var pastConfirmed = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = tenantId,
            IdClient = clientId,
            Estado = "confirmed",
            FechaInicio = DateTime.UtcNow.AddDays(-3),
            FechaFin = DateTime.UtcNow.AddDays(-3).AddHours(1)
        };

        _clientRepo.Setup(r => r.GetByWhatsAppAsync("521234567890", tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment> { pastConfirmed });

        var dto = new AppointmentRescheduleDto
        {
            AppointmentIdentifier = "521234567890",
            TenantId = tenantId,
            NuevaFechaInicio = DateTime.UtcNow.AddDays(1)
        };

        // Act
        var act = async () => await _useCase.ExecuteAsync(dto);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no encontrada*");
    }

    [Fact]
    public async Task ExecuteAsync_WithWhatsAppIdentifier_WhenConfirmed_RevertsToPendingAndClearsConfirmadoEn()
    {
        // Arrange: cliente reprograma (por WhatsApp) una cita CONFIRMADA futura.
        // P0/E3: debe volver a PENDIENTE y limpiar ConfirmadoEn para re-confirmar.
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();
        var client = new Client { IdClient = clientId, WhatsApp = "521234567890" };
        var futureConfirmed = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = tenantId,
            IdClient = clientId,
            IdServiceType = serviceTypeId,
            Estado = "confirmed",
            ConfirmadoEn = DateTime.UtcNow.AddDays(-1),
            FechaInicio = DateTime.UtcNow.AddDays(3),
            FechaFin = DateTime.UtcNow.AddDays(3).AddHours(1)
        };

        _clientRepo.Setup(r => r.GetByWhatsAppAsync("521234567890", tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment> { futureConfirmed });
        _serviceTypeRepo.Setup(r => r.GetByIdAsync(serviceTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceType { IdServiceType = serviceTypeId, IdTenant = tenantId, DuracionMinutos = 30, BufferMinutos = 5 });

        var newStart = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
        var dto = new AppointmentRescheduleDto
        {
            AppointmentIdentifier = "521234567890",
            TenantId = tenantId,
            NuevaFechaInicio = newStart
        };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert
        result.Should().NotBeNull();
        result!.Status.Should().Be("pending");
        futureConfirmed.Estado.Should().Be("pending");
        futureConfirmed.ConfirmadoEn.Should().BeNull();
        _appointmentRepo.Verify(r => r.UpdateAsync(
            It.Is<Appointment>(a => a.Estado == "pending" && a.ConfirmadoEn == null),
            It.IsAny<CancellationToken>()));
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

    [Fact]
    public async Task ExecuteAsync_WhenCompleted_Throws()
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

        var dto = new AppointmentRescheduleDto
        {
            AppointmentId = appointment.IdAppointment,
            NuevaFechaInicio = DateTime.UtcNow.AddDays(1),
            TenantId = Guid.NewGuid()
        };

        // Act
        var act = async () => await _useCase.ExecuteAsync(dto);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*finalizada*");
    }
}
