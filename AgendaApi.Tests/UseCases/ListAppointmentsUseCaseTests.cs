using AgendaApi.Application.DTOs;
using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Moq;

namespace AgendaApi.Tests.UseCases;

public class ListAppointmentsUseCaseTests
{
    private readonly Mock<IAppointmentRepository> _appointmentRepo = new();
    private readonly Mock<IClientRepository> _clientRepo = new();
    private readonly Mock<IServiceTypeRepository> _serviceTypeRepo = new();

    private readonly ListAppointmentsUseCase _useCase;

    public ListAppointmentsUseCaseTests()
    {
        _useCase = new ListAppointmentsUseCase(
            _appointmentRepo.Object,
            _clientRepo.Object,
            _serviceTypeRepo.Object);
    }

    [Fact]
    public async Task ExecuteAsync_WithUpcomingStatus_ReturnsPendingAndConfirmed()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();

        _clientRepo.Setup(r => r.GetByWhatsAppAsync("521234567890", tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Client { IdClient = clientId, Nombre = "Juan" });

        var now = DateTime.UtcNow;
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                new() { IdAppointment = Guid.NewGuid(), IdClient = clientId, IdTenant = tenantId, IdServiceType = serviceTypeId, Estado = "confirmed", FechaInicio = now.AddDays(1) },
                new() { IdAppointment = Guid.NewGuid(), IdClient = clientId, IdTenant = tenantId, IdServiceType = serviceTypeId, Estado = "pending", FechaInicio = now.AddDays(2) },
                new() { IdAppointment = Guid.NewGuid(), IdClient = clientId, IdTenant = tenantId, IdServiceType = serviceTypeId, Estado = "cancelled", FechaInicio = now.AddDays(-1) }
            });

        _serviceTypeRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceType> { new() { IdServiceType = serviceTypeId, Nombre = "Corte" } });

        // Act
        var result = await _useCase.ExecuteAsync("521234567890", tenantId);

        // Assert
        result.Should().HaveCount(2);
        result.Should().OnlyContain(a => a.Status == "confirmed" || a.Status == "pending");
    }

    [Fact]
    public async Task ExecuteAsync_WithCancelledFilter_ReturnsOnlyCancelled()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        _clientRepo.Setup(r => r.GetByWhatsAppAsync("521234567890", tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Client { IdClient = clientId, Nombre = "Maria" });

        var now = DateTime.UtcNow;
        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                new() { IdAppointment = Guid.NewGuid(), IdClient = clientId, IdTenant = tenantId, Estado = "cancelled", FechaInicio = now },
                new() { IdAppointment = Guid.NewGuid(), IdClient = clientId, IdTenant = tenantId, Estado = "confirmed", FechaInicio = now }
            });

        _serviceTypeRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceType>());

        // Act
        var result = await _useCase.ExecuteAsync("521234567890", tenantId, estado: "cancelled");

        // Assert
        result.Should().HaveCount(1);
        result[0].Status.Should().Be("cancelled");
    }

    [Fact]
    public async Task ExecuteAsync_WhenClientNotFound_ReturnsEmptyList()
    {
        // Arrange
        _clientRepo.Setup(r => r.GetByWhatsAppAsync("9999999999", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Client?)null);

        // Act
        var result = await _useCase.ExecuteAsync("9999999999", Guid.NewGuid());

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidStatus_ReturnsAllAppointments()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        _clientRepo.Setup(r => r.GetByWhatsAppAsync("521234567890", tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Client { IdClient = clientId, Nombre = "Pedro" });

        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                new() { IdAppointment = Guid.NewGuid(), IdClient = clientId, IdTenant = tenantId, Estado = "confirmed", FechaInicio = DateTime.UtcNow },
                new() { IdAppointment = Guid.NewGuid(), IdClient = clientId, IdTenant = tenantId, Estado = "cancelled", FechaInicio = DateTime.UtcNow }
            });

        _serviceTypeRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceType>());

        // Act
        var result = await _useCase.ExecuteAsync("521234567890", tenantId, estado: "invalid_status");

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExecuteAsync_IncludesServiceTypeName()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var serviceTypeId = Guid.NewGuid();

        _clientRepo.Setup(r => r.GetByWhatsAppAsync("521234567890", tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Client { IdClient = clientId, Nombre = "Ana" });

        _appointmentRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                new() { IdAppointment = Guid.NewGuid(), IdClient = clientId, IdTenant = tenantId, IdServiceType = serviceTypeId, Estado = "confirmed", FechaInicio = DateTime.UtcNow }
            });

        _serviceTypeRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceType> { new() { IdServiceType = serviceTypeId, Nombre = "Consulta" } });

        // Act
        var result = await _useCase.ExecuteAsync("521234567890", tenantId);

        // Assert
        result.Should().ContainSingle();
        result[0].ServiceTypeName.Should().Be("Consulta");
        result[0].ClientName.Should().Be("Ana");
    }
}
