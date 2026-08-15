using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Moq;

namespace AgendaApi.Tests.UseCases;

/// <summary>
/// Pruebas del resumen operativo (dashboard): totales por estado, tasas de cumplimiento,
/// serie de demanda diaria, ocupación por profesional, cartera de clientes y waitlist.
/// Se construye un GetDashboardStatsUseCase REAL con repos Moq que devuelven las entidades.
/// </summary>
public class GetDashboardStatsUseCaseTests
{
    private readonly Mock<IAppointmentRepository> _appointmentRepo = new();
    private readonly Mock<IClientRepository> _clientRepo = new();
    private readonly Mock<IProfessionalRepository> _professionalRepo = new();
    private readonly Mock<IWaitlistEntryRepository> _waitlistRepo = new();

    private readonly GetDashboardStatsUseCase _useCase;

    private readonly Guid _tenantId = Guid.NewGuid();

    public GetDashboardStatsUseCaseTests()
    {
        _useCase = new GetDashboardStatsUseCase(
            _appointmentRepo.Object,
            _clientRepo.Object,
            _professionalRepo.Object,
            _waitlistRepo.Object);

        // Ruido de fondo: ni citas, ni profesionales, ni clientes, ni waitlist por defecto
        // (cada test sobrescribe lo que necesita).
        _appointmentRepo.Setup(r => r.GetByTenantIdAsync(_tenantId, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());
        _clientRepo.Setup(r => r.GetByTenantIdAsync(_tenantId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Client>());
        _professionalRepo.Setup(r => r.GetActiveByTenantIdAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Professional>());
        _waitlistRepo.Setup(r => r.GetActiveByTenantAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WaitlistEntry>());
        _waitlistRepo.Setup(r => r.GetFulfilledByTenantAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
    }

    private void SetupAppointments(IEnumerable<Appointment> appointments)
        => _appointmentRepo.Setup(r => r.GetByTenantIdAsync(_tenantId, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(appointments.ToList());

    // ─── Totales por estado + tasas ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TotalesPorEstadoYTasas()
    {
        SetupAppointments(new List<Appointment>
        {
            new() { IdTenant = _tenantId, Estado = "completed", FechaInicio = new DateTime(2026, 8, 1, 10, 0, 0) },
            new() { IdTenant = _tenantId, Estado = "cancelled", FechaInicio = new DateTime(2026, 8, 1, 11, 0, 0) },
            new() { IdTenant = _tenantId, Estado = "completed", FechaInicio = new DateTime(2026, 8, 2, 10, 0, 0) },
            new() { IdTenant = _tenantId, Estado = "pending", FechaInicio = new DateTime(2026, 8, 3, 10, 0, 0) }
        });

        var resumen = await _useCase.ExecuteAsync(_tenantId, new DateTime(2026, 8, 1), new DateTime(2026, 8, 2));

        resumen.Totales.Citas.Should().Be(3);
        resumen.Totales.Completed.Should().Be(2);
        resumen.Totales.Cancelled.Should().Be(1);
        resumen.Totales.Pending.Should().Be(0); // 8/3 está fuera del rango
        // Cumplimiento = 2 completadas / 3 cerradas; inasistencias = 0 / 2 en fecha.
        resumen.Tasas.Cumplimiento.Should().BeApproximately(2.0 / 3, 0.0001);
        resumen.Tasas.Inasistencias.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_TasasDenominadorCero_DevuelveCero()
    {
        SetupAppointments(new List<Appointment>
        {
            new() { IdTenant = _tenantId, Estado = "pending", FechaInicio = new DateTime(2026, 8, 1, 10, 0, 0) }
        });

        var resumen = await _useCase.ExecuteAsync(_tenantId, new DateTime(2026, 8, 1), new DateTime(2026, 8, 1));

        resumen.Totales.Completed.Should().Be(0);
        resumen.Tasas.Cumplimiento.Should().Be(0);
        resumen.Tasas.Inasistencias.Should().Be(0);
    }

    // ─── Serie de demanda diaria ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SerieDemanda_CreadasYRealizadasPorDia()
    {
        SetupAppointments(new List<Appointment>
        {
            new() { IdTenant = _tenantId, Estado = "completed", FechaInicio = new DateTime(2026, 8, 1, 10, 0, 0), FechaCreacion = new DateTime(2026, 8, 1) },
            new() { IdTenant = _tenantId, Estado = "cancelled", FechaInicio = new DateTime(2026, 8, 1, 11, 0, 0), FechaCreacion = new DateTime(2026, 8, 1) },
            new() { IdTenant = _tenantId, Estado = "completed", FechaInicio = new DateTime(2026, 8, 2, 10, 0, 0), FechaCreacion = new DateTime(2026, 8, 2) }
        });

        var resumen = await _useCase.ExecuteAsync(_tenantId, new DateTime(2026, 8, 1), new DateTime(2026, 8, 3));

        var dia1 = resumen.SerieDemanda.Single(d => d.Fecha == new DateOnly(2026, 8, 1));
        dia1.Creadas.Should().Be(2);   // 2 citas creadas el 8/1
        dia1.Realizadas.Should().Be(1); // 1 completada el 8/1

        var dia3 = resumen.SerieDemanda.Single(d => d.Fecha == new DateOnly(2026, 8, 3));
        dia3.Creadas.Should().Be(0);
        dia3.Realizadas.Should().Be(0);
    }

    // ─── Ocupación por profesional ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Ocupacion_AgrupaPorProfesionalYGeneral()
    {
        var profA = Guid.NewGuid();
        var profB = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _professionalRepo.Setup(r => r.GetActiveByTenantIdAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Professional>
            {
                new() { IdProfessional = profA, Nombre = "Dra. María" },
                new() { IdProfessional = profB, Nombre = "Dr. Carlos" }
            });

        SetupAppointments(new List<Appointment>
        {
            // Futuras pendientes/confirmadas → cuentan.
            new() { IdTenant = _tenantId, IdProfessional = profA, Estado = "confirmed", FechaInicio = now.AddDays(1) },
            new() { IdTenant = _tenantId, IdProfessional = profA, Estado = "pending", FechaInicio = now.AddDays(2) },
            new() { IdTenant = _tenantId, IdProfessional = profB, Estado = "pending", FechaInicio = now.AddDays(3) },
            new() { IdTenant = _tenantId, Estado = "pending", FechaInicio = now.AddDays(4) }, // sin profesional → General
            // No cuentan: pasada o completada.
            new() { IdTenant = _tenantId, IdProfessional = profA, Estado = "completed", FechaInicio = now.AddDays(1) }
        });

        var resumen = await _useCase.ExecuteAsync(_tenantId, now.AddDays(-30), now.AddDays(30));

        var ocupacion = resumen.OcupacionPorProfesional;
        ocupacion.Should().ContainSingle(o => o.Profesional == "Dra. María" && o.CitasProximas == 2);
        ocupacion.Should().ContainSingle(o => o.Profesional == "Dr. Carlos" && o.CitasProximas == 1);
        ocupacion.Should().ContainSingle(o => o.Profesional == "General" && o.CitasProximas == 1);
    }

    // ─── Cartera de clientes ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Cartera_AgrupaPorEstado()
    {
        _clientRepo.Setup(r => r.GetByTenantIdAsync(_tenantId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Client>
            {
                new() { IdTenant = _tenantId, Estado = "nuevo" },
                new() { IdTenant = _tenantId, Estado = "nuevo" },
                new() { IdTenant = _tenantId, Estado = "frecuente" },
                new() { IdTenant = _tenantId, Estado = "inactivo" },
                new() { IdTenant = _tenantId, Estado = "vip" }
            });

        var resumen = await _useCase.ExecuteAsync(_tenantId, new DateTime(2026, 8, 1), new DateTime(2026, 8, 1));

        resumen.CarteraClientes.Nuevo.Should().Be(2);
        resumen.CarteraClientes.Frecuente.Should().Be(1);
        resumen.CarteraClientes.Inactivo.Should().Be(1);
        resumen.CarteraClientes.Vip.Should().Be(1);
        resumen.CarteraClientes.Blacklist.Should().Be(0);
    }

    // ─── Waitlist (activa + cumplidas) ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Waitlist_ActivaYCumplidas()
    {
        _waitlistRepo.Setup(r => r.GetActiveByTenantAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WaitlistEntry>
            {
                new() { IdTenant = _tenantId, Estado = "active" },
                new() { IdTenant = _tenantId, Estado = "active" }
            });
        _waitlistRepo.Setup(r => r.GetFulfilledByTenantAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var resumen = await _useCase.ExecuteAsync(_tenantId, new DateTime(2026, 8, 1), new DateTime(2026, 8, 1));

        resumen.Waitlist.Activa.Should().Be(2);
        resumen.Waitlist.Cumplidas.Should().Be(3);
    }
}