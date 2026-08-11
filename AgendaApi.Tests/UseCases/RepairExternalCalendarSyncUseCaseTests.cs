using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgendaApi.Tests.UseCases;

public class RepairExternalCalendarSyncUseCaseTests
{
    private readonly Mock<IAppointmentRepository> _appointmentRepo = new();
    private readonly Mock<ICalendarConnectionRepository> _connectionRepo = new();
    private readonly Mock<ICalendarProviderFactory> _providerFactory = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ILogger<RepairExternalCalendarSyncUseCase>> _logger = new();

    private readonly RepairExternalCalendarSyncUseCase _useCase;

    public RepairExternalCalendarSyncUseCaseTests()
    {
        _useCase = new RepairExternalCalendarSyncUseCase(
            _appointmentRepo.Object,
            _connectionRepo.Object,
            _providerFactory.Object,
            _unitOfWork.Object,
            _logger.Object);
    }

    [Fact]
    public async Task ExecuteAsync_ConCitasSinEventoExterno_CreaEventosYGuardaIds()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var appointment = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = tenantId,
            IdClient = Guid.NewGuid(),
            IdServiceType = Guid.NewGuid(),
            FechaInicio = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc),
            FechaFin = new DateTime(2026, 8, 20, 11, 0, 0, DateTimeKind.Utc),
            Estado = "confirmed",
            ExternalEventId = null
        };

        _appointmentRepo.Setup(r => r.GetMissingExternalEventsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment> { appointment });
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CalendarConnection { IdTenant = tenantId, Activo = true });
        var provider = new Mock<ICalendarProvider>();
        provider.Setup(p => p.CreateEventAsync(appointment, It.IsAny<CancellationToken>()))
            .ReturnsAsync("evt-repaired-1");
        _providerFactory.Setup(f => f.GetProviderAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider.Object);

        // Act
        var count = await _useCase.ExecuteAsync(CancellationToken.None);

        // Assert: el evento se crea, se guarda el ID y se persiste
        count.Should().Be(1);
        appointment.ExternalEventId.Should().Be("evt-repaired-1");
        _appointmentRepo.Verify(r => r.UpdateAsync(appointment, It.IsAny<CancellationToken>()));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ExecuteAsync_SinCitasFaltantes_NoPersiste()
    {
        // Arrange: el repo no devuelve nada (cancelled/pasadas/no aplican)
        _appointmentRepo.Setup(r => r.GetMissingExternalEventsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());

        // Act
        var count = await _useCase.ExecuteAsync(CancellationToken.None);

        // Assert: nada que reparar → no se tocan repos ni UoW (idempotente)
        count.Should().Be(0);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _providerFactory.Verify(f => f.GetProviderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SinConexionActiva_NoCreaEventos()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var appointment = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = tenantId,
            Estado = "confirmed",
            ExternalEventId = null
        };
        _appointmentRepo.Setup(r => r.GetMissingExternalEventsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment> { appointment });
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CalendarConnection?)null);

        // Act
        var count = await _useCase.ExecuteAsync(CancellationToken.None);

        // Assert: sin conexión de calendario no hay reparación
        count.Should().Be(0);
        _providerFactory.Verify(f => f.GetProviderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ErrorCreandoUnEvento_ContinuaConLasDemas()
    {
        // Arrange: dos citas del mismo tenant; la primera falla, la segunda se repara
        var tenantId = Guid.NewGuid();
        var ok = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = tenantId,
            Estado = "confirmed",
            FechaInicio = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc),
            ExternalEventId = null
        };
        var fail = new Appointment
        {
            IdAppointment = Guid.NewGuid(),
            IdTenant = tenantId,
            Estado = "confirmed",
            FechaInicio = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
            ExternalEventId = null
        };

        _appointmentRepo.Setup(r => r.GetMissingExternalEventsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment> { fail, ok });
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CalendarConnection { IdTenant = tenantId, Activo = true });
        var provider = new Mock<ICalendarProvider>();
        provider.Setup(p => p.CreateEventAsync(fail, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("auth expired"));
        provider.Setup(p => p.CreateEventAsync(ok, It.IsAny<CancellationToken>()))
            .ReturnsAsync("evt-ok");
        _providerFactory.Setup(f => f.GetProviderAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider.Object);

        // Act
        var count = await _useCase.ExecuteAsync(CancellationToken.None);

        // Assert: la que falló no aborta la reparación de la otra
        count.Should().Be(1);
        ok.ExternalEventId.Should().Be("evt-ok");
        fail.ExternalEventId.Should().BeNull();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()));
    }
}