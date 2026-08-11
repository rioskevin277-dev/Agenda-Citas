using AgendaApi.Application.DTOs;
using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Moq;

namespace AgendaApi.Tests.UseCases;

public class CreateAppointmentUseCaseTests
{
    private readonly Mock<IAppointmentRepository> _appointmentRepo = new();
    private readonly Mock<IClientRepository> _clientRepo = new();
    private readonly Mock<IServiceTypeRepository> _serviceTypeRepo = new();
    private readonly Mock<ICalendarConnectionRepository> _connectionRepo = new();
    private readonly Mock<ICalendarProviderFactory> _providerFactory = new();
    private readonly Mock<IMessagingProvider> _messagingProvider = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ICalendarProvider> _calendarProvider = new();
    private readonly Mock<IProfessionalRepository> _professionalRepo = new();
    private readonly Mock<IBookingPolicy> _bookingPolicy = new();

    private readonly CreateAppointmentUseCase _useCase;

    public CreateAppointmentUseCaseTests()
    {
        _bookingPolicy.Setup(p => p.ValidateAsync(
                It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BookingValidationResult.Ok());

        _useCase = new CreateAppointmentUseCase(
            _appointmentRepo.Object,
            _clientRepo.Object,
            _serviceTypeRepo.Object,
            _professionalRepo.Object,
            _connectionRepo.Object,
            _providerFactory.Object,
            _messagingProvider.Object,
            _unitOfWork.Object,
            _bookingPolicy.Object);
    }

    [Fact]
    public async Task ExecuteAsync_WithClientIdAndServiceType_CreatesSuccessfully()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();

        _clientRepo.Setup(r => r.GetByIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Client { IdClient = clientId, WhatsApp = "521234567890", Nombre = "Juan" });
        _serviceTypeRepo.Setup(r => r.GetByIdAsync(serviceTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceType { IdServiceType = serviceTypeId, IdTenant = tenantId, DuracionMinutos = 60, BufferMinutos = 15 });
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());
        _appointmentRepo.Setup(r => r.CreateAsync(It.IsAny<Appointment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Appointment a, CancellationToken _) => a);
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CalendarConnection?)null);
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var dto = new AppointmentCreateDto
        {
            TenantId = tenantId,
            ClientId = clientId,
            ServiceTypeId = serviceTypeId,
            FechaInicio = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc)
        };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert
        result.Should().NotBeNull();
        _appointmentRepo.Verify(r => r.CreateAsync(It.IsAny<Appointment>(), It.IsAny<CancellationToken>()));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ExecuteAsync_WithClientWhatsApp_AutoCreatesClient()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();

        _clientRepo.Setup(r => r.GetByWhatsAppAsync("521234567890", tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Client?)null);
        _clientRepo.Setup(r => r.CreateAsync(It.IsAny<Client>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Client c, CancellationToken _) => c);
        _serviceTypeRepo.Setup(r => r.GetByIdAsync(serviceTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceType { IdServiceType = serviceTypeId, IdTenant = tenantId, DuracionMinutos = 60 });
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());
        _appointmentRepo.Setup(r => r.CreateAsync(It.IsAny<Appointment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Appointment a, CancellationToken _) => a);
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CalendarConnection?)null);
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var dto = new AppointmentCreateDto
        {
            TenantId = tenantId,
            ClientWhatsApp = "521234567890",
            ClientName = "Cliente Nuevo",
            ServiceTypeId = serviceTypeId,
            FechaInicio = new DateTime(2026, 8, 2, 14, 0, 0, DateTimeKind.Utc)
        };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert
        result.Should().NotBeNull();
        _clientRepo.Verify(r => r.CreateAsync(
            It.Is<Client>(c => c.WhatsApp == "521234567890" && c.Nombre == "Cliente Nuevo"),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ExecuteAsync_WithoutFechaFin_CalculatesFromServiceDuration()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();
        var startTime = new DateTime(2026, 8, 4, 9, 0, 0, DateTimeKind.Utc);
        var expectedEnd = startTime.AddMinutes(40); // 30min + 10min buffer

        _clientRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Client { IdClient = Guid.NewGuid(), WhatsApp = "521234567890" });
        _serviceTypeRepo.Setup(r => r.GetByIdAsync(serviceTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceType
            {
                IdServiceType = serviceTypeId,
                IdTenant = tenantId,
                DuracionMinutos = 30,
                BufferMinutos = 10
            });
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());
        _appointmentRepo.Setup(r => r.CreateAsync(It.IsAny<Appointment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Appointment a, CancellationToken _) => a);
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CalendarConnection?)null);
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var dto = new AppointmentCreateDto
        {
            TenantId = tenantId,
            ClientId = Guid.NewGuid(),
            ServiceTypeId = serviceTypeId,
            FechaInicio = startTime
        };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert
        result.Should().NotBeNull();
        _appointmentRepo.Verify(r => r.CreateAsync(
            It.Is<Appointment>(a => a.FechaFin == expectedEnd),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ExecuteAsync_WithProfessionalName_AssignsProfessionalAndValidatesItsChannel()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var professionalId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();

        _clientRepo.Setup(r => r.GetByWhatsAppAsync("521234567890", tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Client?)null);
        _clientRepo.Setup(r => r.CreateAsync(It.IsAny<Client>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Client c, CancellationToken _) => c);
        _serviceTypeRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceType>
            {
                new() { IdServiceType = serviceTypeId, IdTenant = tenantId, Nombre = "Consulta", DuracionMinutos = 45, Activo = true }
            });
        _professionalRepo.Setup(r => r.GetActiveByTenantAndNameAsync(tenantId, "Dra. María", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Professional { IdProfessional = professionalId, IdTenant = tenantId, Nombre = "Dra. María", Activo = true });
        _professionalRepo.Setup(r => r.ProvidesServiceAsync(professionalId, serviceTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _appointmentRepo.Setup(r => r.CreateAsync(It.IsAny<Appointment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Appointment a, CancellationToken _) => a);
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CalendarConnection?)null);
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var dto = new AppointmentCreateDto
        {
            TenantId = tenantId,
            ClientWhatsApp = "521234567890",
            ClientName = "Juan",
            ServiceTypeName = "Consulta",
            ProfessionalName = "Dra. María",
            FechaInicio = new DateTime(2026, 8, 15, 10, 0, 0, DateTimeKind.Utc)
        };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert: el profesional se asigna y la política valida el canal de ese profesional
        result.Should().NotBeNull();
        result!.ProfessionalId.Should().Be(professionalId);
        result.ProfessionalName.Should().Be("Dra. María");
        _appointmentRepo.Verify(r => r.CreateAsync(
            It.Is<Appointment>(a => a.IdProfessional == professionalId),
            It.IsAny<CancellationToken>()));
        _bookingPolicy.Verify(p => p.ValidateAsync(
            tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            It.IsAny<Guid?>(), It.IsAny<int>(), professionalId, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ExecuteAsync_WithProfessionalOutsidePortfolio_Throws()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var professionalId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();

        _clientRepo.Setup(r => r.GetByWhatsAppAsync("521234567890", tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Client?)null);
        _clientRepo.Setup(r => r.CreateAsync(It.IsAny<Client>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Client c, CancellationToken _) => c);
        _serviceTypeRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceType>
            {
                new() { IdServiceType = serviceTypeId, IdTenant = tenantId, Nombre = "Consulta", DuracionMinutos = 45, Activo = true }
            });
        _professionalRepo.Setup(r => r.GetActiveByTenantAndNameAsync(tenantId, "Dra. María", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Professional { IdProfessional = professionalId, IdTenant = tenantId, Nombre = "Dra. María", Activo = true });
        // No realiza el servicio: fuera de su cartera
        _professionalRepo.Setup(r => r.ProvidesServiceAsync(professionalId, serviceTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var dto = new AppointmentCreateDto
        {
            TenantId = tenantId,
            ClientWhatsApp = "521234567890",
            ClientName = "Juan",
            ServiceTypeName = "Consulta",
            ProfessionalName = "Dra. María",
            FechaInicio = new DateTime(2026, 8, 15, 10, 0, 0, DateTimeKind.Utc)
        };

        // Act & Assert
        var act = async () => await _useCase.ExecuteAsync(dto);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no realiza el servicio*");
    }

    [Fact]
    public async Task ExecuteAsync_ByDefault_CreatesPendingAppointment()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();

        _clientRepo.Setup(r => r.GetByIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Client { IdClient = clientId, WhatsApp = "521234567890", Nombre = "Juan" });
        _serviceTypeRepo.Setup(r => r.GetByIdAsync(serviceTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceType { IdServiceType = serviceTypeId, IdTenant = tenantId, DuracionMinutos = 60, BufferMinutos = 15 });
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());
        _appointmentRepo.Setup(r => r.CreateAsync(It.IsAny<Appointment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Appointment a, CancellationToken _) => a);
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CalendarConnection?)null);
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var dto = new AppointmentCreateDto
        {
            TenantId = tenantId,
            ClientId = clientId,
            ServiceTypeId = serviceTypeId,
            FechaInicio = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc)
        };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert: flujo WhatsApp (default) → nace PENDIENTE de confirmación
        result!.Status.Should().Be("pending");
        _appointmentRepo.Verify(r => r.CreateAsync(
            It.Is<Appointment>(a => a.Estado == "pending"),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ExecuteAsync_WithConfirmarAlCrear_CreatesConfirmedAppointment()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();

        _clientRepo.Setup(r => r.GetByIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Client { IdClient = clientId, WhatsApp = "521234567890", Nombre = "Juan" });
        _serviceTypeRepo.Setup(r => r.GetByIdAsync(serviceTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceType { IdServiceType = serviceTypeId, IdTenant = tenantId, DuracionMinutos = 60, BufferMinutos = 15 });
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());
        _appointmentRepo.Setup(r => r.CreateAsync(It.IsAny<Appointment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Appointment a, CancellationToken _) => a);
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CalendarConnection?)null);
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var dto = new AppointmentCreateDto
        {
            TenantId = tenantId,
            ClientId = clientId,
            ServiceTypeId = serviceTypeId,
            FechaInicio = new DateTime(2026, 8, 21, 11, 0, 0, DateTimeKind.Utc),
            ConfirmarAlCrear = true
        };

        // Act
        var result = await _useCase.ExecuteAsync(dto);

        // Assert: la API HTTP crea la cita YA confirmada
        result!.Status.Should().Be("confirmed");
        _appointmentRepo.Verify(r => r.CreateAsync(
            It.Is<Appointment>(a => a.Estado == "confirmed"),
            It.IsAny<CancellationToken>()));
    }
}
