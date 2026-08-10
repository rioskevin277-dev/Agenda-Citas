using AgendaApi.Application.Rules;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgendaApi.Tests.Rules;

public class BookingPolicyTests
{
    // Fixtures: 2026-08-03 es lunes (DiaSemana=1), 2026-08-09 es domingo (DiaSemana=7)
    private static readonly DateTime Monday = new(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantId = Guid.NewGuid();

    private readonly Mock<IAvailabilityRepository> _availabilityRepo = new();
    private readonly Mock<IAppointmentRepository> _appointmentRepo = new();
    private readonly Mock<ICalendarConnectionRepository> _connectionRepo = new();
    private readonly Mock<ICalendarProviderFactory> _providerFactory = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<ILogger<BookingPolicy>> _logger = new();

    private readonly BookingPolicy _policy;

    public BookingPolicyTests()
    {
        // Default: lunes a viernes 9:00-18:00, sin excepciones, sin citas locales,
        // sin restricciones de antelación, sin conexión de calendario externo.
        _availabilityRepo.Setup(r => r.GetByTenantIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityRule>
            {
                new() { DiaSemana = 1, HoraInicio = new TimeSpan(9, 0, 0), HoraFin = new TimeSpan(18, 0, 0), Activo = true },
                new() { DiaSemana = 2, HoraInicio = new TimeSpan(9, 0, 0), HoraFin = new TimeSpan(18, 0, 0), Activo = true },
                new() { DiaSemana = 3, HoraInicio = new TimeSpan(9, 0, 0), HoraFin = new TimeSpan(18, 0, 0), Activo = true },
                new() { DiaSemana = 4, HoraInicio = new TimeSpan(9, 0, 0), HoraFin = new TimeSpan(18, 0, 0), Activo = true },
                new() { DiaSemana = 5, HoraInicio = new TimeSpan(9, 0, 0), HoraFin = new TimeSpan(18, 0, 0), Activo = true }
            });
        _availabilityRepo.Setup(r => r.GetExceptionsByDateRangeAsync(TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>());
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CalendarConnection?)null);
        _tenantRepo.Setup(r => r.GetByIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tenant { IdTenant = TenantId });

        _policy = new BookingPolicy(
            _availabilityRepo.Object,
            _appointmentRepo.Object,
            _connectionRepo.Object,
            _providerFactory.Object,
            _tenantRepo.Object,
            _logger.Object);
    }

    private static DateTime On(DateTime reference, int hour, int minute = 0)
        => new(reference.Year, reference.Month, reference.Day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ValidateAsync_FechaFinAntesDeInicio_Fails()
    {
        var result = await _policy.ValidateAsync(TenantId, Monday.AddHours(1), Monday);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("La fecha de fin debe ser posterior a la fecha de inicio");
    }

    [Fact]
    public async Task ValidateAsync_FueraDelHorarioLaboral_Fails()
    {
        var result = await _policy.ValidateAsync(TenantId, On(Monday, 19), On(Monday, 20));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("El horario solicitado está fuera del horario laboral del negocio");
    }

    [Fact]
    public async Task ValidateAsync_MismoDiaSinRegla_Domingo_Fails()
    {
        var sunday = new DateTime(2026, 8, 9, 10, 0, 0, DateTimeKind.Utc);

        var result = await _policy.ValidateAsync(TenantId, sunday, sunday.AddHours(1));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateAsync_DentroDelHorarioLaboral_Passes()
    {
        var result = await _policy.ValidateAsync(TenantId, On(Monday, 10), On(Monday, 11));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_ReservaQueCruzaDeDia_Fails()
    {
        var start = new DateTime(2026, 8, 3, 23, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 8, 4, 1, 0, 0, DateTimeKind.Utc);

        var result = await _policy.ValidateAsync(TenantId, start, end);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateAsync_ExcepcionDiaCompleto_Fails()
    {
        _availabilityRepo.Setup(r => r.GetExceptionsByDateRangeAsync(TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>
            {
                new() { Fecha = new DateTime(2026, 8, 3), DiaCompleto = true }
            });

        var result = await _policy.ValidateAsync(TenantId, On(Monday, 10), On(Monday, 11));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("El horario solicitado está fuera del horario laboral del negocio");
    }

    [Fact]
    public async Task ValidateAsync_ExcepcionHorarioEspecial_ReemplazaRegla()
    {
        // El lunes 3-ago pasa a tener horario especial de 14:00 a 16:00 (reemplaza la regla recurrente)
        _availabilityRepo.Setup(r => r.GetExceptionsByDateRangeAsync(TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>
            {
                new() { Fecha = new DateTime(2026, 8, 3), DiaCompleto = false, HoraInicio = new TimeSpan(14, 0, 0), HoraFin = new TimeSpan(16, 0, 0) }
            });

        // Dentro del horario especial → pasa
        var dentro = await _policy.ValidateAsync(TenantId, On(Monday, 14), On(Monday, 15));
        dentro.IsValid.Should().BeTrue();

        // Dentro de la regla recurrente pero fuera del horario especial → falla
        var fuera = await _policy.ValidateAsync(TenantId, On(Monday, 10), On(Monday, 11));
        fuera.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateAsync_ConflictoConCitaLocal_Fails()
    {
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                new() { IdAppointment = Guid.NewGuid(), IdTenant = TenantId, Estado = "confirmed", FechaInicio = On(Monday, 10), FechaFin = On(Monday, 11) }
            });

        var result = await _policy.ValidateAsync(TenantId, On(Monday, 10), On(Monday, 11));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("El horario solicitado ya está ocupado");
    }

    [Fact]
    public async Task ValidateAsync_CitaCancelada_NoBloquea()
    {
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                new() { IdAppointment = Guid.NewGuid(), IdTenant = TenantId, Estado = "cancelled", FechaInicio = On(Monday, 10), FechaFin = On(Monday, 11) }
            });

        var result = await _policy.ValidateAsync(TenantId, On(Monday, 10), On(Monday, 11));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_ExcluyendoLaPropiaCita_Pasa()
    {
        var appointmentId = Guid.NewGuid();
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                new() { IdAppointment = appointmentId, IdTenant = TenantId, Estado = "confirmed", FechaInicio = On(Monday, 10), FechaFin = On(Monday, 11) }
            });

        var result = await _policy.ValidateAsync(TenantId, On(Monday, 10), On(Monday, 11), excludeAppointmentId: appointmentId);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_ConflictoConCalendarioExterno_Fails()
    {
        var connection = new CalendarConnection { IdTenant = TenantId, Activo = true };
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);

        var provider = new Mock<ICalendarProvider>();
        provider.Setup(p => p.GetAvailabilityAsync(TenantId, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TimeSlot>
            {
                new() { FechaInicio = On(Monday, 10), FechaFin = On(Monday, 11), Disponible = false }
            });
        _providerFactory.Setup(f => f.GetProviderAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider.Object);

        var result = await _policy.ValidateAsync(TenantId, On(Monday, 10), On(Monday, 11));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("El horario solicitado ya está ocupado en el calendario del negocio");
    }

    [Fact]
    public async Task ValidateAsync_CalendarioExternoNoResponde_NoBloquea()
    {
        var connection = new CalendarConnection { IdTenant = TenantId, Activo = true };
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);

        var provider = new Mock<ICalendarProvider>();
        provider.Setup(p => p.GetAvailabilityAsync(TenantId, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("timeout"));
        _providerFactory.Setup(f => f.GetProviderAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider.Object);

        // Fallo del calendario externo no debe bloquear la reserva (degrade igual que el read path)
        var result = await _policy.ValidateAsync(TenantId, On(Monday, 10), On(Monday, 11));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_AntelacionMinimaNoCumplida_Fails()
    {
        // 10000 horas de antelación mínima: cualquier reserva cercana la incumple
        _tenantRepo.Setup(r => r.GetByIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tenant { IdTenant = TenantId, AntelacionMinimaHoras = 10000 });

        var result = await _policy.ValidateAsync(TenantId, On(Monday, 10), On(Monday, 11));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("Debes agendar con al menos 10000 horas de anticipación");
    }

    [Fact]
    public async Task ValidateAsync_AntelacionMaximaExcedida_Fails()
    {
        // 2026-10-01 (jueves) está a más de 1 día
        _tenantRepo.Setup(r => r.GetByIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tenant { IdTenant = TenantId, AntelacionMaximaDias = 1 });
        var farFuture = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

        var result = await _policy.ValidateAsync(TenantId, farFuture, farFuture.AddHours(1));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("No se pueden agendar citas con más de 1 día de anticipación");
    }

    [Fact]
    public async Task ValidateAsync_AntelacionDentroDeLimites_Pasa()
    {
        // Límite máximo muy holgado: la reserva lejana lo cumple y el horario habil (jueves) pasa
        _tenantRepo.Setup(r => r.GetByIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tenant { IdTenant = TenantId, AntelacionMaximaDias = 10000 });
        var farFuture = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

        var result = await _policy.ValidateAsync(TenantId, farFuture, farFuture.AddHours(1));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_CapacidadDos_PermiteSegundaCitaSimultanea()
    {
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                new() { IdAppointment = Guid.NewGuid(), IdTenant = TenantId, Estado = "confirmed", FechaInicio = On(Monday, 10), FechaFin = On(Monday, 11) }
            });

        var result = await _policy.ValidateAsync(TenantId, On(Monday, 10), On(Monday, 11), capacidad: 2);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_CapacidadDos_RechazaTerceraCita()
    {
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                new() { IdAppointment = Guid.NewGuid(), IdTenant = TenantId, Estado = "confirmed", FechaInicio = On(Monday, 10), FechaFin = On(Monday, 11) },
                new() { IdAppointment = Guid.NewGuid(), IdTenant = TenantId, Estado = "confirmed", FechaInicio = On(Monday, 10, 15), FechaFin = On(Monday, 11, 15) }
            });

        var result = await _policy.ValidateAsync(TenantId, On(Monday, 10), On(Monday, 11), capacidad: 2);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("El horario solicitado ya está ocupado");
    }

    [Fact]
    public async Task ValidateAsync_CalendarioExternoBloqueaDuroPeseACapacidad()
    {
        // El calendario del negocio es bloqueo duro aunque el servicio tenga capacidad para varias personas
        var connection = new CalendarConnection { IdTenant = TenantId, Activo = true };
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);

        var provider = new Mock<ICalendarProvider>();
        provider.Setup(p => p.GetAvailabilityAsync(TenantId, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TimeSlot>
            {
                new() { FechaInicio = On(Monday, 10), FechaFin = On(Monday, 11), Disponible = false }
            });
        _providerFactory.Setup(f => f.GetProviderAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider.Object);

        var result = await _policy.ValidateAsync(TenantId, On(Monday, 10), On(Monday, 11), capacidad: 2);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("El horario solicitado ya está ocupado en el calendario del negocio");
    }

    [Fact]
    public async Task ValidateAsync_EventoExternoDeCitaPropia_NoBloqueaSegundaCita()
    {
        // Una cita local que ya creó su evento externo no debe contar DOS veces
        // contra un servicio con capacidad para varias personas.
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                new() { IdAppointment = Guid.NewGuid(), IdTenant = TenantId, Estado = "confirmed", FechaInicio = On(Monday, 10), FechaFin = On(Monday, 11), ExternalEventId = "evt-1" }
            });

        var connection = new CalendarConnection { IdTenant = TenantId, Activo = true };
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);

        var provider = new Mock<ICalendarProvider>();
        provider.Setup(p => p.GetAvailabilityAsync(TenantId, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TimeSlot>
            {
                new() { FechaInicio = On(Monday, 10), FechaFin = On(Monday, 11), Disponible = false, ExternalEventId = "evt-1" }
            });
        _providerFactory.Setup(f => f.GetProviderAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider.Object);

        // capacidad 2: la cita local ocupa 1 asiento; su evento externo es duplicado (no cuenta)
        var result = await _policy.ValidateAsync(TenantId, On(Monday, 10), On(Monday, 11), capacidad: 2);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_EventoExternoDeOtroProfesional_NoBloqueaCanalEnParalelo()
    {
        var mariaId = Guid.NewGuid();

        // Dr. Carlos ya tiene su cita 10-11 con su evento externo (cita local con ExternalEventId).
        _appointmentRepo.Setup(r => r.GetByDateRangeAsync(TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>
            {
                new() { IdAppointment = Guid.NewGuid(), IdTenant = TenantId, Estado = "confirmed", FechaInicio = On(Monday, 10), FechaFin = On(Monday, 11), ExternalEventId = "evt-carlos" }
            });
        // El canal de María está libre (sin citas suyas ni legadas).
        _appointmentRepo.Setup(r => r.GetByDateRangeForProfessionalAsync(TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), mariaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());
        _availabilityRepo.Setup(r => r.GetByTenantAndProfessionalAsync(TenantId, mariaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityRule>());
        _availabilityRepo.Setup(r => r.GetExceptionsByDateRangeForProfessionalAsync(TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), mariaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailabilityException>());

        var connection = new CalendarConnection { IdTenant = TenantId, Activo = true };
        _connectionRepo.Setup(r => r.GetByTenantIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);

        var provider = new Mock<ICalendarProvider>();
        provider.Setup(p => p.GetAvailabilityAsync(TenantId, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TimeSlot>
            {
                new() { FechaInicio = On(Monday, 10), FechaFin = On(Monday, 11), Disponible = false, ExternalEventId = "evt-carlos" }
            });
        _providerFactory.Setup(f => f.GetProviderAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider.Object);

        // El evento externo es el de la cita de Carlos (canal paralelo): no debe bloquear a María.
        var result = await _policy.ValidateAsync(TenantId, On(Monday, 10), On(Monday, 10, 30), professionalId: mariaId);

        result.IsValid.Should().BeTrue();
    }
}