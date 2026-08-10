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
    private readonly Mock<IProfessionalRepository> _professionalRepo = new();
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
            _professionalRepo.Object,
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

    [Fact]
    public async Task ExecuteAsync_WithCapacityTwoAndOneBooking_ReturnsWholeDay()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var fecha = new DateOnly(2026, 8, 3); // Monday
        var serviceType = new ServiceType
        {
            IdServiceType = Guid.NewGuid(),
            IdTenant = tenantId,
            Nombre = "Corte",
            DuracionMinutos = 30,
            CapacidadMaxima = 2
        };

        _serviceTypeRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceType> { serviceType });
        _availabilityRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityRule>
            {
                new() { DiaSemana = 1, HoraInicio = new TimeSpan(9, 0, 0), HoraFin = new TimeSpan(17, 0, 0), Activo = true }
            });
        _availabilityRepo.Setup(r => r.GetExceptionsByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>());

        // Una sola cita no llena un servicio con capacidad 2
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
            FechaFin = fecha,
            ServiceTypeName = "Corte"
        };

        // Act
        var result = await _useCase.ExecuteAsync(query);

        // Assert
        result.Any(s => s.Start.Hour == 9 && s.End.Hour == 17).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WithCapacityTwoFull_ExcludesOccupiedSegment()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var fecha = new DateOnly(2026, 8, 3); // Monday
        var serviceType = new ServiceType
        {
            IdServiceType = Guid.NewGuid(),
            IdTenant = tenantId,
            Nombre = "Clase",
            DuracionMinutos = 60,
            CapacidadMaxima = 2
        };

        _serviceTypeRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceType> { serviceType });
        _availabilityRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityRule>
            {
                new() { DiaSemana = 1, HoraInicio = new TimeSpan(9, 0, 0), HoraFin = new TimeSpan(17, 0, 0), Activo = true }
            });
        _availabilityRepo.Setup(r => r.GetExceptionsByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>());

        // Dos citas que se pisan de 10:30 a 11:30 llenan la capacidad 2 en ese tramo
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                new()
                {
                    IdAppointment = Guid.NewGuid(),
                    IdTenant = tenantId,
                    FechaInicio = new DateTime(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc),
                    FechaFin = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc),
                    Estado = "confirmed"
                },
                new()
                {
                    IdAppointment = Guid.NewGuid(),
                    IdTenant = tenantId,
                    FechaInicio = new DateTime(2026, 8, 3, 10, 30, 0, DateTimeKind.Utc),
                    FechaFin = new DateTime(2026, 8, 3, 11, 30, 0, DateTimeKind.Utc),
                    Estado = "confirmed"
                }
            });

        var query = new AvailabilityQueryDto
        {
            TenantId = tenantId,
            FechaInicio = fecha,
            FechaFin = fecha,
            ServiceTypeName = "Clase"
        };

        // Act
        var result = await _useCase.ExecuteAsync(query);

        // Assert
        // El tramo donde se juntan 2 citas (10:30-11:30) ya no debe aparecer como libre
        var blockedMid = new DateTime(2026, 8, 3, 11, 0, 0, DateTimeKind.Utc);
        result.Any(s => s.Start < blockedMid && s.End > blockedMid).Should().BeFalse();
        // Los tramos con un solo asiento ocupado siguen libres: 9-10:30 y 11:30-17
        result.Any(s => s.Start.Hour == 9 && s.End.Hour == 10 && s.End.Minute == 30).Should().BeTrue();
        result.Any(s => s.Start.Hour == 11 && s.Start.Minute == 30).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WithProfessional_UsesPersonalScheduleAndOwnChannel()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var professionalId = Guid.NewGuid();
        var fecha = new DateOnly(2026, 8, 3); // Monday

        _professionalRepo.Setup(r => r.GetActiveByTenantAndNameAsync(tenantId, "Dra. María", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Professional { IdProfessional = professionalId, IdTenant = tenantId, Nombre = "Dra. María", Activo = true });

        // Negocio: lunes 9-17. Dra. María: lunes SOLO 10-12 (lo específico reemplaza lo genérico).
        _availabilityRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityRule>
            {
                new() { DiaSemana = 1, HoraInicio = new TimeOnly(9, 0).ToTimeSpan(), HoraFin = new TimeOnly(17, 0).ToTimeSpan(), Activo = true }
            });
        _availabilityRepo.Setup(r => r.GetExceptionsByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>());
        _availabilityRepo.Setup(r => r.GetByTenantAndProfessionalAsync(tenantId, professionalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityRule>
            {
                new() { DiaSemana = 1, HoraInicio = new TimeOnly(10, 0).ToTimeSpan(), HoraFin = new TimeOnly(12, 0).ToTimeSpan(), Activo = true }
            });
        _availabilityRepo.Setup(r => r.GetExceptionsByDateRangeForProfessionalAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), professionalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>());

        // El repo ya filtra el canal del profesional (IdProfessional == professionalId || null).
        // Una cita LEGADA (sin profesional) ocupa el canal de cualquiera por compatibilidad:
        // Dra. María queda con huecos 10-10:30 y 11:30-12.
        _appointmentRepo.Setup(r => r.GetByDateRangeForProfessionalAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), professionalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                new()
                {
                    IdAppointment = Guid.NewGuid(),
                    IdTenant = tenantId,
                    IdProfessional = null, // cita legada: bloquea a todos los profesionales
                    FechaInicio = new DateTime(2026, 8, 3, 10, 30, 0, DateTimeKind.Utc),
                    FechaFin = new DateTime(2026, 8, 3, 11, 30, 0, DateTimeKind.Utc),
                    Estado = "confirmed"
                }
            });

        var query = new AvailabilityQueryDto
        {
            TenantId = tenantId,
            FechaInicio = fecha,
            FechaFin = fecha,
            ProfessionalName = "Dra. María"
        };

        // Act
        var result = await _useCase.ExecuteAsync(query);

        // Assert: horario personal 10-12 (nada antes de las 10 ni después de las 12,
        // aunque el negocio abra de 9 a 17) y la cita legada divide el tramo.
        result.Should().NotBeEmpty();
        result.Any(s => s.Start == new DateTime(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc)
                        && s.End == new DateTime(2026, 8, 3, 10, 30, 0, DateTimeKind.Utc)).Should().BeTrue();
        result.Any(s => s.Start == new DateTime(2026, 8, 3, 11, 30, 0, DateTimeKind.Utc)
                        && s.End == new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)).Should().BeTrue();
        result.Any(s => s.Start.Hour < 10 || s.Start.Hour >= 12).Should().BeFalse();
        _availabilityRepo.Verify(r => r.GetByTenantAndProfessionalAsync(tenantId, professionalId, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ExecuteAsync_EventoExternoDeOtroProfesional_NoRecortaElSlotDelCanal()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var mariaId = Guid.NewGuid();
        var fecha = new DateOnly(2026, 8, 3); // Monday

        _professionalRepo.Setup(r => r.GetActiveByTenantAndNameAsync(tenantId, "Dra. María", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Professional { IdProfessional = mariaId, IdTenant = tenantId, Nombre = "Dra. María", Activo = true });

        // Negocio lunes 9-17; Dra. María personal lunes 9-16 (lo específico reemplaza).
        _availabilityRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityRule>
            {
                new() { DiaSemana = 1, HoraInicio = new TimeOnly(9, 0).ToTimeSpan(), HoraFin = new TimeOnly(17, 0).ToTimeSpan(), Activo = true }
            });
        _availabilityRepo.Setup(r => r.GetExceptionsByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>());
        _availabilityRepo.Setup(r => r.GetByTenantAndProfessionalAsync(tenantId, mariaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityRule>
            {
                new() { DiaSemana = 1, HoraInicio = new TimeOnly(9, 0).ToTimeSpan(), HoraFin = new TimeOnly(16, 0).ToTimeSpan(), Activo = true }
            });
        _availabilityRepo.Setup(r => r.GetExceptionsByDateRangeForProfessionalAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), mariaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>());

        // El canal de María está libre; la cita de Carlos (10-11, con evento externo) NO le pertenece.
        _appointmentRepo.Setup(r => r.GetByDateRangeForProfessionalAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), mariaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());
        // Lista completa del rango (para deduplicar eventos del propio sistema): la cita de Carlos.
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                new()
                {
                    IdAppointment = Guid.NewGuid(),
                    IdTenant = tenantId,
                    IdProfessional = Guid.NewGuid(), // Carlos
                    FechaInicio = new DateTime(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc),
                    FechaFin = new DateTime(2026, 8, 3, 11, 0, 0, DateTimeKind.Utc),
                    Estado = "confirmed",
                    ExternalEventId = "evt-carlos"
                }
            });

        // Calendario externo conectado: devuelve el evento de Carlos como ocupado.
        var connection = new CalendarConnection { IdTenant = tenantId, Activo = true };
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);
        var provider = new Mock<ICalendarProvider>();
        provider.Setup(p => p.GetAvailabilityAsync(tenantId, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TimeSlot>
            {
                new() { FechaInicio = new DateTime(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc), FechaFin = new DateTime(2026, 8, 3, 11, 0, 0, DateTimeKind.Utc), Disponible = false, ExternalEventId = "evt-carlos" }
            });
        _providerFactory.Setup(f => f.GetProviderAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider.Object);

        var query = new AvailabilityQueryDto
        {
            TenantId = tenantId,
            FechaInicio = fecha,
            FechaFin = fecha,
            ProfessionalName = "Dra. María"
        };

        // Act
        var result = await _useCase.ExecuteAsync(query);

        // Assert: el slot personal 9-16 llega completo (el evento de Carlos es su cita local, no ocupa el canal de María)
        result.Should().HaveCount(1);
        result[0].Start.Should().Be(new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc));
        result[0].End.Should().Be(new DateTime(2026, 8, 3, 16, 0, 0, DateTimeKind.Utc));
    }
}
