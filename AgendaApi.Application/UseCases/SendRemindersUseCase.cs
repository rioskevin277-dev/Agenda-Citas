using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Application.UseCases;

/// <summary>
/// Caso de uso: Enviar recordatorios de citas pendientes/confirmadas.
/// Se ejecuta periódicamente (background job).
/// </summary>
public class SendRemindersUseCase
{
    private readonly IAppointmentRepository _appointmentRepo;
    private readonly IClientRepository _clientRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly ITenantContext _tenantContext;
    private readonly ICalendarProviderFactory _providerFactory;
    private readonly ICalendarConnectionRepository _connectionRepo;
    private readonly IMessagingProvider _messagingProvider;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SendRemindersUseCase> _logger;

    public SendRemindersUseCase(
        IAppointmentRepository appointmentRepo,
        IClientRepository clientRepo,
        ITenantRepository tenantRepo,
        ITenantContext tenantContext,
        ICalendarProviderFactory providerFactory,
        ICalendarConnectionRepository connectionRepo,
        IMessagingProvider messagingProvider,
        IUnitOfWork unitOfWork,
        ILogger<SendRemindersUseCase> logger)
    {
        _appointmentRepo = appointmentRepo;
        _clientRepo = clientRepo;
        _tenantRepo = tenantRepo;
        _tenantContext = tenantContext;
        _providerFactory = providerFactory;
        _connectionRepo = connectionRepo;
        _messagingProvider = messagingProvider;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        var pendingReminders = await _appointmentRepo.GetPendingRemindersAsync(ct);
        int sent = 0;

        foreach (var appointment in pendingReminders)
        {
            try
            {
                var client = await _clientRepo.GetByIdAsync(appointment.IdClient, ct);
                if (client == null) continue;

                // El envío por WhatsApp exige un contexto de tenant configurado,
                // pero este servicio corre en background (sin HTTP/middleware).
                // Se resuelve el tenant de la cita y se configura el contexto antes de enviar.
                var tenant = await _tenantRepo.GetByIdAsync(appointment.IdTenant, ct);
                if (tenant == null) continue;

                _tenantContext.SetTenant(
                    tenant.IdTenant,
                    calendarProvider: tenant.CalendarProvider ?? "google",
                    whatsAppAccessToken: Environment.GetEnvironmentVariable("WhatsApp__AccessToken")
                                       ?? Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN")
                                       ?? "",
                    phoneNumberId: tenant.WhatsAppPhoneNumberId ?? "");

                var fechaStr = appointment.FechaInicio.ToString("dd/MM/yyyy 'a las' HH:mm");
                await _messagingProvider.SendTextAsync(
                    client.WhatsApp,
                    $"⏰ Recordatorio: Tienes una cita agendada para el {fechaStr}.\n" +
                    $"Responde CONFIRMAR para confirmar, CANCELAR para cancelarla o " +
                    $"REAGENDAR para cambiar la fecha.",
                    ct);

                appointment.RecordatorioEnviadoEn = DateTime.UtcNow;
                await _appointmentRepo.UpdateAsync(appointment, ct);
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SendReminders] Error enviando recordatorio: {Message}", ex.Message);
            }
        }

        if (sent > 0)
            await _unitOfWork.SaveChangesAsync(ct);

        return sent;
    }
}
