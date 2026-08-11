using AgendaApi.Application.Support;
using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgendaApi.Tests.UseCases;

/// <summary>
/// Pruebas del caso de uso de recordatorios multi-etapa por tenant (24h + 2h).
/// Convención de horas: el "ahora" del negocio es BusinessClock.Now (huso del tenant
/// "disfrazado de UTC"), y las citas se agendan relativo a ese instante.
/// </summary>
public class SendRemindersUseCaseTests
{
    private readonly Mock<IAppointmentRepository> _appointmentRepo = new();
    private readonly Mock<IClientRepository> _clientRepo = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<ITenantContext> _tenantContext = new();
    private readonly Mock<IMessagingProvider> _messagingProvider = new();
    private readonly Mock<IReminderLogRepository> _reminderLogRepo = new();
    private readonly Mock<IConversationSessionService> _conversationSession = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ILogger<SendRemindersUseCase>> _logger = new();

    private readonly SendRemindersUseCase _useCase;

    private static readonly DateTime Now = BusinessClock.Now;

    public SendRemindersUseCaseTests()
    {
        // Estado determinista: sin templates configurados (se llenan solo en el test de template)
        // y sesión de WhatsApp activa por defecto (camino de texto libre dentro de la ventana de 24h).
        Environment.SetEnvironmentVariable("WhatsApp__RecordatorioTemplate24h", null);
        Environment.SetEnvironmentVariable("WhatsApp__RecordatorioTemplate2h", null);
        Environment.SetEnvironmentVariable("WHATSAPP_RECORDATORIO_TEMPLATE_24H", null);
        Environment.SetEnvironmentVariable("WHATSAPP_RECORDATORIO_TEMPLATE_2H", null);

        _conversationSession.Setup(c => c.HasActiveSession(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns(true);

        // Sin wamid por defecto → SendStageAsync registraría "failed". Cada test que espera
        // un envío exitoso configura el wamid de Meta.
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _messagingProvider.Setup(m => m.SendTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        _reminderLogRepo.Setup(r => r.GetByAppointmentIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReminderLog>());

        _useCase = new SendRemindersUseCase(
            _appointmentRepo.Object,
            _clientRepo.Object,
            _tenantRepo.Object,
            _tenantContext.Object,
            _messagingProvider.Object,
            _reminderLogRepo.Object,
            _conversationSession.Object,
            _unitOfWork.Object,
            _logger.Object);
    }

    private void SetupTenant(Tenant tenant)
    {
        _tenantRepo.Setup(r => r.GetAllActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Tenant> { tenant });
    }

    private void SetupAppointment(Appointment appointment)
    {
        _appointmentRepo.Setup(r => r.GetReminderCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment> { appointment });
        _clientRepo.Setup(r => r.GetByIdAsync(appointment.IdClient, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Client { IdClient = appointment.IdClient, WhatsApp = "521234567890", Nombre = "Juan" });
    }

    private static Tenant DefaultTenant() => new()
    {
        IdTenant = Guid.NewGuid(),
        CalendarProvider = "google",
        WhatsAppPhoneNumberId = "111111111111111",
        RecordatorioHabilitado = true,
        Recordatorio1Horas = 24,
        Recordatorio2Horas = 2
    };

    private static Appointment AppointmentAt(DateTime fechaInicio, string estado, Guid tenantId) => new()
    {
        IdAppointment = Guid.NewGuid(),
        IdTenant = tenantId,
        IdClient = Guid.NewGuid(),
        FechaInicio = fechaInicio,
        FechaFin = fechaInicio.AddMinutes(30),
        Estado = estado
    };

    // ─── Etapa 1 (antelación larga) ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Etapa1VencidaPending_EnviaTextoLibreEnSesion()
    {
        // Arrange
        var tenant = DefaultTenant();
        var appointment = AppointmentAt(Now.AddHours(23), "pending", tenant.IdTenant);
        SetupTenant(tenant);
        SetupAppointment(appointment);
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("wamid123");

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert: 1 solo envío (etapa 1 de 24h), texto que invita a confirmar, y fila sent con wamid.
        count.Should().Be(1);
        _messagingProvider.Verify(m => m.SendTextAsync("521234567890",
            It.Is<string>(msg => msg.Contains("PENDIENTE")), It.IsAny<CancellationToken>()), Times.Once);
        _messagingProvider.Verify(m => m.SendTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
        _reminderLogRepo.Verify(r => r.AddAsync(It.Is<ReminderLog>(log =>
            log.Etapa == 1 && log.Estado == "sent" && log.WamId == "wamid123"
            && log.FechaProgramada == appointment.FechaInicio.AddHours(-24)),
            It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Etapa1VencidaConfirmada_EnviaTextoDeCitaConfirmada()
    {
        // Arrange
        var tenant = DefaultTenant();
        var appointment = AppointmentAt(Now.AddHours(23), "confirmed", tenant.IdTenant);
        SetupTenant(tenant);
        SetupAppointment(appointment);
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("wamid1");

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert: la cita confirmada sí recibe la etapa 1 (recordatorio de cita confirmada).
        count.Should().Be(1);
        _messagingProvider.Verify(m => m.SendTextAsync("521234567890",
            It.Is<string>(msg => msg.Contains("confirmada") && !msg.Contains("PENDIENTE")), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Etapa 2 (nudge final) ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Etapa2VencidaSoloSiNoConfirmo_EnviaNudge()
    {
        // Arrange: cita pending a T-2h → ambas etapas vencidas, se elige la de menor
        // antelación (etapa 2, 2h) y solo se envía UNA vez.
        var tenant = DefaultTenant();
        var appointment = AppointmentAt(Now.AddHours(2), "pending", tenant.IdTenant);
        SetupTenant(tenant);
        SetupAppointment(appointment);
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("wamid2");

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert
        count.Should().Be(1);
        _messagingProvider.Verify(m => m.SendTextAsync("521234567890",
            It.Is<string>(msg => msg.Contains("aún no la has confirmado")), It.IsAny<CancellationToken>()), Times.Once);
        _messagingProvider.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _reminderLogRepo.Verify(r => r.AddAsync(It.Is<ReminderLog>(log => log.Etapa == 2), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CitaConfirmada_NoRecibeEtapa2()
    {
        // Arrange: cita confirmada a T-2h → la etapa 2 queda cerrada (solo pending),
        // se envía la etapa 1 en su lugar.
        var tenant = DefaultTenant();
        var appointment = AppointmentAt(Now.AddHours(2), "confirmed", tenant.IdTenant);
        SetupTenant(tenant);
        SetupAppointment(appointment);
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("wamid3");

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert: nunca llega el nudge de "aún no confirmada", y no se crea log de etapa 2.
        count.Should().Be(1);
        _messagingProvider.Verify(m => m.SendTextAsync("521234567890",
            It.Is<string>(msg => msg.Contains("aún no la has confirmado")), It.IsAny<CancellationToken>()), Times.Never);
        _reminderLogRepo.Verify(r => r.AddAsync(It.Is<ReminderLog>(log => log.Etapa == 2), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Dedup y reintentos ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EtapaYaEntregada_NoSeReenvia()
    {
        // Arrange
        var tenant = DefaultTenant();
        var appointment = AppointmentAt(Now.AddHours(23), "pending", tenant.IdTenant);
        SetupTenant(tenant);
        SetupAppointment(appointment);
        _reminderLogRepo.Setup(r => r.GetByAppointmentIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReminderLog>
            {
                new() { IdAppointment = appointment.IdAppointment, IdTenant = tenant.IdTenant, Etapa = 1, Estado = "delivered" }
            });

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert: sin envío ni persistencia (nada cambió).
        count.Should().Be(0);
        _messagingProvider.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_EtapaFailedConReintentosPendientes_SeReenvia()
    {
        // Arrange: reintentos 2 (< 3) → la etapa sigue abierta; el reenvío exitoso
        // actualiza la fila existente (no crea otra).
        var tenant = DefaultTenant();
        var appointment = AppointmentAt(Now.AddHours(23), "pending", tenant.IdTenant);
        SetupTenant(tenant);
        SetupAppointment(appointment);
        _reminderLogRepo.Setup(r => r.GetByAppointmentIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReminderLog>
            {
                new() { IdAppointment = appointment.IdAppointment, IdTenant = tenant.IdTenant, Etapa = 1, Estado = "failed", Reintentos = 2 }
            });
        _messagingProvider.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("wamidX");

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert
        count.Should().Be(1);
        _reminderLogRepo.Verify(r => r.UpdateAsync(It.Is<ReminderLog>(log =>
            log.Etapa == 1 && log.Estado == "sent" && log.WamId == "wamidX" && log.Reintentos == 2),
            It.IsAny<CancellationToken>()), Times.Once);
        _reminderLogRepo.Verify(r => r.AddAsync(It.IsAny<ReminderLog>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_EtapaFailedConReintentosAgotados_Omite()
    {
        // Arrange: reintentos 3 (>= máximo) → la etapa queda cerrada para siempre.
        var tenant = DefaultTenant();
        var appointment = AppointmentAt(Now.AddHours(23), "pending", tenant.IdTenant);
        SetupTenant(tenant);
        SetupAppointment(appointment);
        _reminderLogRepo.Setup(r => r.GetByAppointmentIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReminderLog>
            {
                new() { IdAppointment = appointment.IdAppointment, IdTenant = tenant.IdTenant, Etapa = 1, Estado = "failed", Reintentos = 3 }
            });

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert
        count.Should().Be(0);
        _messagingProvider.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Canal: template vs texto libre ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SinTemplateFueraDeSesion_RegistraFailedSinEnviar()
    {
        // Arrange: sin template configurado y sin sesión activa → WhatsApp rechazaría el
        // texto libre (131047), así que no se envía y el log queda failed para ser honesto.
        var tenant = DefaultTenant();
        var appointment = AppointmentAt(Now.AddHours(23), "pending", tenant.IdTenant);
        SetupTenant(tenant);
        SetupAppointment(appointment);
        _conversationSession.Setup(c => c.HasActiveSession(It.IsAny<Guid>(), It.IsAny<string>())).Returns(false);

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert
        count.Should().Be(0);
        _messagingProvider.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _messagingProvider.Verify(m => m.SendTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
        _reminderLogRepo.Verify(r => r.AddAsync(It.Is<ReminderLog>(log =>
            log.Estado == "failed" && log.Reintentos == 1 && log.Error != null), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ConTemplateConfigurado_UsaTemplate()
    {
        // Arrange: con template aprobado se envía por template (funciona fuera de la
        // ventana de sesión) con los 3 body params nombre/fecha/hora.
        Environment.SetEnvironmentVariable("WhatsApp__RecordatorioTemplate24h", "recordatorio_24h");
        var tenant = DefaultTenant();
        var appointment = AppointmentAt(Now.AddHours(23), "pending", tenant.IdTenant);
        SetupTenant(tenant);
        SetupAppointment(appointment);
        _messagingProvider.Setup(m => m.SendTemplateAsync(It.IsAny<string>(), "recordatorio_24h", It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("wamidT");

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert
        count.Should().Be(1);
        _messagingProvider.Verify(m => m.SendTemplateAsync("521234567890", "recordatorio_24h",
            It.Is<Dictionary<string, string>>(p =>
                p["1"] == "Juan"
                && p["2"] == appointment.FechaInicio.ToString("dd/MM/yyyy")
                && p["3"] == appointment.FechaInicio.ToString("HH:mm")),
            It.IsAny<CancellationToken>()), Times.Once);
        _messagingProvider.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Filtros y bordes ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SinCandidatos_DevuelveCero()
    {
        // Arrange
        SetupTenant(DefaultTenant());
        _appointmentRepo.Setup(r => r.GetReminderCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Appointment>());

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert
        count.Should().Be(0);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_TenantSinWhatsAppPhoneNumber_SaltaCita()
    {
        // Arrange
        var tenant = DefaultTenant();
        tenant.WhatsAppPhoneNumberId = null;
        var appointment = AppointmentAt(Now.AddHours(23), "pending", tenant.IdTenant);
        SetupTenant(tenant);
        SetupAppointment(appointment);

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert
        count.Should().Be(0);
        _messagingProvider.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_TenantConRecordatoriosDeshabilitados_SaltaCita()
    {
        // Arrange
        var tenant = DefaultTenant();
        tenant.RecordatorioHabilitado = false;
        var appointment = AppointmentAt(Now.AddHours(23), "pending", tenant.IdTenant);
        SetupTenant(tenant);
        SetupAppointment(appointment);

        // Act
        var count = await _useCase.ExecuteAsync();

        // Assert: la cita del tenant no entra al mapa de config → se salta sin enviar.
        count.Should().Be(0);
        _messagingProvider.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
