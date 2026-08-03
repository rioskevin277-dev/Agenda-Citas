using AgendaApi.Application.DTOs;
using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgendaApi.Tests.UseCases;

public class CheckAvailabilityUseCaseTests
{
    private readonly Mock<IAvailabilityRepository> _availabilityRepo = new();
    private readonly Mock<IAppointmentRepository> _appointmentRepo = new();
    private readonly Mock<ICalendarConnectionRepository> _connectionRepo = new();
    private readonly Mock<ICalendarProviderFactory> _providerFactory = new();
    private readonly Mock<IServiceTypeRepository> _serviceTypeRepo = new();
    private readonly Mock<ILogger<CheckAvailabilityUseCase>> _logger = new();

    private readonly CheckAvailabilityUseCase _useCase;

    public CheckAvailabilityUseCaseTests()
    {
        _useCase = new CheckAvailabilityUseCase(
            _availabilityRepo.Object,
            _appointmentRepo.Object,
            _connectionRepo.Object,
            _providerFactory.Object,
            _serviceTypeRepo.Object,
            _logger.Object);
    }

    [Fact]
    public async Task ExecuteAsync_WithAvailabilityRules_ReturnsTimeSlots()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var fecha = new DateOnly(2026, 8, 3); // Monday

        _availabilityRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityRule>
            {
                new() { DiaSemana = 1, HoraInicio = new TimeOnly(9, 0).ToTimeSpan(), HoraFin = new TimeOnly(13, 0).ToTimeSpan(), Activo = true }
            });
        _availabilityRepo.Setup(r => r.GetExceptionsByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>());
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());

        var query = new AvailabilityQueryDto
        {
            TenantId = tenantId,
            FechaInicio = fecha,
            FechaFin = fecha
        };

        // Act
        var result = await _useCase.ExecuteAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
        result.Should().AllSatisfy(s => s.Disponible.Should().BeTrue());
    }

    [Fact]
    public async Task ExecuteAsync_WithExistingAppointment_ExcludesBusySlot()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var fecha = new DateOnly(2026, 8, 3); // Monday

        _availabilityRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityRule>
            {
                new() { DiaSemana = 1, HoraInicio = new TimeOnly(9, 0).ToTimeSpan(), HoraFin = new TimeOnly(17, 0).ToTimeSpan(), Activo = true }
            });
        _availabilityRepo.Setup(r => r.GetExceptionsByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>());

        // Existing appointment blocks 10-11
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                new()
                {
                    IdAppointment = Guid.NewGuid(),
                    IdTenant = tenantId,
                    FechaInicio = new DateTime(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc),
                    FechaFin = new DateTime(2026, 8, 3, 11, 0, 0, DateTimeKind.Utc),
                    Estado = "confirmed"
                }
            });

        var query = new AvailabilityQueryDto
        {
            TenantId = tenantId,
            FechaInicio = fecha,
            FechaFin = fecha
        };

        // Act
        var result = await _useCase.ExecuteAsync(query);

        // Assert
        result.Should().NotBeNull();
        // Should have a gap at 9-10 and 11-17, but not at 10-11
        var nineToTen = result.Any(s => s.Start.Hour == 9 && s.End.Hour == 10);
        var tenToEleven = result.Any(s => s.Start.Hour == 10 && s.End.Hour == 11);
        nineToTen.Should().BeTrue();
        tenToEleven.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WithServiceTypeName_FiltersByServiceType()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var serviceType = new ServiceType
        {
            IdServiceType = Guid.NewGuid(),
            IdTenant = tenantId,
            Nombre = "Corte",
            DuracionMinutos = 30,
            BufferMinutos = 10
        };

        _serviceTypeRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceType> { serviceType });
        _availabilityRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityRule>());
        _availabilityRepo.Setup(r => r.GetExceptionsByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>());
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());

        var query = new AvailabilityQueryDto
        {
            TenantId = tenantId,
            FechaInicio = new DateOnly(2026, 8, 1),
            FechaFin = new DateOnly(2026, 8, 1),
            ServiceTypeName = "Corte"
        };

        // Act
        var result = await _useCase.ExecuteAsync(query);

        // Assert
        result.Should().NotBeNull();
        _serviceTypeRepo.Verify(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ExecuteAsync_WhenHoliday_ReturnsEmpty()
    {
        // Arrange
        var tenantId = Guid.NewGuid();

        _availabilityRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityRule>
            {
                new() { DiaSemana = 1, HoraInicio = new TimeOnly(9, 0).ToTimeSpan(), HoraFin = new TimeOnly(17, 0).ToTimeSpan(), Activo = true }
            });
        _availabilityRepo.Setup(r => r.GetExceptionsByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>
            {
                new() { Fecha = new DateTime(2026, 8, 3), DiaCompleto = true }
            });

        var query = new AvailabilityQueryDto
        {
            TenantId = tenantId,
            FechaInicio = new DateOnly(2026, 8, 3), // Monday but holiday
            FechaFin = new DateOnly(2026, 8, 3)
        };

        // Act
        var result = await _useCase.ExecuteAsync(query);

        // Assert
        result.Should().BeEmpty();
    }
}
