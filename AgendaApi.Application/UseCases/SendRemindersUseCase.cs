using AgendaApi.Application.Support;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Application.UseCases;

/// <summary>
/// Caso de uso: enviar recordatorios automáticos de citas por WhatsApp.
/// Multi-etapa configurable por tenant (ej: 24h + 2h). La 2ª etapa solo se envía a citas
/// aún no confirmadas. Dedup y estados por (cita, etapa) en reminder_logs.
/// Se ejecuta periódicamente (background job).
/// </summary>
public class SendRemindersUseCase
{
    /// <summary>Intentos máximos por etapa antes de marcarla failed definitivamente.</summary>
    public const int MaxReintentos = 3;

    private readonly IAppointmentRepository _appointmentRepo;
    private readonly IClientRepository _clientRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly ITenantContext _tenantContext;
    private readonly IMessagingProvider _messagingProvider;
    private readonly IReminderLogRepository _reminderLogRepo;
    private readonly IConversationSessionService _conversationSession;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SendRemindersUseCase> _logger;

    public SendRemindersUseCase(
        IAppointmentRepository appointmentRepo,
        IClientRepository clientRepo,
        ITenantRepository tenantRepo,
        ITenantContext tenantContext,
        IMessagingProvider messagingProvider,
        IReminderLogRepository reminderLogRepo,
        IConversationSessionService conversationSession,
        IUnitOfWork unitOfWork,
        ILogger<SendRemindersUseCase> logger)
    {
        _appointmentRepo = appointmentRepo;
        _clientRepo = clientRepo;
        _tenantRepo = tenantRepo;
        _tenantContext = tenantContext;
        _messagingProvider = messagingProvider;
        _reminderLogRepo = reminderLogRepo;
        _conversationSession = conversationSession;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        var now = BusinessClock.Now;

        // Tenants activos con recordatorios habilitados (config de etapas por negocio).
        var tenants = await _tenantRepo.GetAllActiveAsync(ct);
        var configByTenant = tenants
            .Where(t => t.RecordatorioHabilitado)
            .ToDictionary(t => t.IdTenant, t => t);

        var candidates = await _appointmentRepo.GetReminderCandidatesAsync(now, ct);
        var logs = await _reminderLogRepo.GetByAppointmentIdsAsync(candidates.Select(a => a.IdAppointment), ct);
        var logsByAppointment = logs
            .GroupBy(r => r.IdAppointment)
            .ToDictionary(g => g.Key, g => g.ToList());

        int sent = 0;
        bool logsModified = false;

        foreach (var appointment in candidates)
        {
            try
            {
                if (!configByTenant.TryGetValue(appointment.IdTenant, out var tenant))
                    continue;
                if (string.IsNullOrWhiteSpace(tenant.WhatsAppPhoneNumberId))
                    continue;

                var appointmentLogs = logsByAppointment.TryGetValue(appointment.IdAppointment, out var l) ? l : new List<ReminderLog>();

                // Etapa elegible: su momento ya llegó (FechaInicio - horas <= now), está abierta
                // en el log y la 2ª solo aplica a citas sin confirmar. De las elegibles se envía
                // la de MENOR antelación (la más cercana a la cita): así a T-25h va la 24h, a T-2h
                // la 2h, y si el servicio estuvo caído y vencen juntas se envía la 2h, nunca dos.
                var stage = GetStages(tenant)
                    .Where(s => appointment.FechaInicio.AddHours(-s.Horas) <= now)
                    .Where(s => IsOpen(s.Etapa, appointment.Estado, appointmentLogs))
                    .OrderBy(s => s.Horas)
                    .FirstOrDefault();

                if (stage.Horas == 0)
                    continue;

                var client = await _clientRepo.GetByIdAsync(appointment.IdClient, ct);
                if (client == null || string.IsNullOrWhiteSpace(client.PartnerDestination))
                    continue;

                _tenantContext.SetTenant(
                    tenant.IdTenant,
                    calendarProvider: tenant.CalendarProvider ?? "google",
                    whatsAppAccessToken: Env("WhatsApp__AccessToken", "WHATSAPP_ACCESS_TOKEN") ?? "",
                    phoneNumberId: tenant.WhatsAppPhoneNumberId);

                var log = appointmentLogs.FirstOrDefault(r => r.Etapa == stage.Etapa);
                var result = await SendStageAsync(appointment, client, stage.Etapa, stage.Horas, log, now, ct);

                // Todo intento (éxito o fallo) toca reminder_logs → hay que persistir.
                logsModified = true;
                if (result == SendResult.Sent)
                    sent++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SendReminders] Error procesando recordatorio: {Message}", ex.Message);
            }
        }

        if (logsModified)
            await _unitOfWork.SaveChangesAsync(ct);

        return sent;
    }

    private enum SendResult { None, Sent, Failed }

    /// <summary>
    /// Envía la etapa elegida (template si está configurado; texto libre solo dentro de la
    /// ventana de sesión) y registra/actualiza la fila de reminder_logs.
    /// </summary>
    private async Task<SendResult> SendStageAsync(
        Appointment appointment, Client client, int etapa, int horas,
        ReminderLog? log, DateTime now, CancellationToken ct)
    {
        var fechaStr = appointment.FechaInicio.ToString("dd/MM/yyyy");
        var horaStr = appointment.FechaInicio.ToString("HH:mm");
        var fechaProgramada = appointment.FechaInicio.AddHours(-horas);

        // Template por etapa: fuera de la ventana de sesión de 24h el texto libre es
        // rechazado (131047), así que el camino de producción es el template aprobado.
        var templateName = Env(
            etapa == 1 ? "WhatsApp__RecordatorioTemplate24h" : "WhatsApp__RecordatorioTemplate2h",
            etapa == 1 ? "WHATSAPP_RECORDATORIO_TEMPLATE_24H" : "WHATSAPP_RECORDATORIO_TEMPLATE_2H");

        string? wamId = null;
        string? error = null;

        if (!string.IsNullOrWhiteSpace(templateName))
        {
            try
            {
                wamId = await _messagingProvider.SendTemplateAsync(client.PartnerDestination, templateName,
                    new Dictionary<string, string>
                    {
                        ["1"] = string.IsNullOrWhiteSpace(client.Nombre) ? "Hola" : client.Nombre,
                        ["2"] = fechaStr,
                        ["3"] = horaStr
                    }, ct);
            }
            catch (Exception ex)
            {
                error = $"Template {templateName}: {ex.Message}";
            }
        }
        else if (_conversationSession.HasActiveSession(appointment.IdTenant, client.UserId ?? client.WhatsApp))
        {
            try
            {
                wamId = await _messagingProvider.SendTextAsync(client.PartnerDestination, BuildText(etapa, horas, appointment), ct);
            }
            catch (Exception ex)
            {
                error = $"Texto: {ex.Message}";
            }
        }
        else
        {
            error = "Sin template y fuera de ventana de sesión";
        }

        if (wamId != null)
        {
            if (log == null)
            {
                log = new ReminderLog
                {
                    IdAppointment = appointment.IdAppointment,
                    IdTenant = appointment.IdTenant,
                    Etapa = etapa,
                    FechaProgramada = fechaProgramada,
                    FechaIntento = now,
                    Estado = "sent",
                    WamId = wamId,
                    Error = null,
                    Reintentos = 0
                };
                await _reminderLogRepo.AddAsync(log, ct);
            }
            else
            {
                log.Estado = "sent";
                log.WamId = wamId;
                log.Error = null;
                log.FechaIntento = now;
                await _reminderLogRepo.UpdateAsync(log, ct);
            }
            return SendResult.Sent;
        }

        // Fallo: registrar/actualizar la fila para reintentos en ciclos siguientes.
        if (log == null)
        {
            log = new ReminderLog
            {
                IdAppointment = appointment.IdAppointment,
                IdTenant = appointment.IdTenant,
                Etapa = etapa,
                FechaProgramada = fechaProgramada,
                FechaIntento = now,
                Estado = "failed",
                Error = error,
                Reintentos = 1
            };
            await _reminderLogRepo.AddAsync(log, ct);
        }
        else
        {
            log.Estado = "failed";
            log.Error = error;
            log.FechaIntento = now;
            log.Reintentos++;
            await _reminderLogRepo.UpdateAsync(log, ct);
        }
        return SendResult.Failed;
    }

    private static List<(int Etapa, int Horas)> GetStages(Tenant tenant)
    {
        var stages = new List<(int Etapa, int Horas)>();
        if (tenant.Recordatorio1Horas > 0) stages.Add((1, tenant.Recordatorio1Horas));
        if (tenant.Recordatorio2Horas > 0) stages.Add((2, tenant.Recordatorio2Horas));
        return stages.OrderByDescending(s => s.Horas).ToList();
    }

    /// <summary>
    /// Abierta = no hay fila, o fila failed con reintentos restantes. sent/delivered cierran la etapa.
    /// La etapa 2 (nudge final) solo aplica a citas aún pendientes de confirmación.
    /// </summary>
    private static bool IsOpen(int etapa, string estado, List<ReminderLog> logs)
    {
        if (etapa == 2 && estado != "pending") return false;
        var log = logs.FirstOrDefault(r => r.Etapa == etapa);
        if (log == null) return true;
        if (log.Estado is "sent" or "delivered") return false;
        return log.Reintentos < MaxReintentos;
    }

    private static string BuildText(int etapa, int horas, Appointment appointment)
    {
        var fechaStr = appointment.FechaInicio.ToString("dd/MM/yyyy 'a las' HH:mm");
        return etapa switch
        {
            1 when appointment.Estado == "confirmed" =>
                $"⏰ Recordatorio: tienes una cita confirmada para el {fechaStr}.\n" +
                $"Si necesitas cambiarla responde REAGENDAR, o CANCELAR para cancelarla.",
            1 =>
                $"⏰ Recordatorio: tienes una cita PENDIENTE de confirmación para el {fechaStr}.\n" +
                $"Responde CONFIRMAR para confirmarla, CANCELAR para cancelarla o REAGENDAR para cambiar la fecha.",
            2 =>
                $"⏰ Tu cita {Cuando(horas)} ({fechaStr}) y aún no la has confirmado.\n" +
                $"Responde CONFIRMAR para confirmar tu asistencia, o CANCELAR/REAGENDAR.",
            _ => string.Empty
        };
    }

    private static string Cuando(int horas)
        => horas == 1 ? "comienza en 1 hora" : $"comienza en {horas} horas";

    private static string? Env(string key, string altKey)
        => Environment.GetEnvironmentVariable(key) ?? Environment.GetEnvironmentVariable(altKey);
}
