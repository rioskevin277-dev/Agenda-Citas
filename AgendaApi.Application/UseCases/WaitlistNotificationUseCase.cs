using AgendaApi.Application.DTOs;
using AgendaApi.Application.Support;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Application.UseCases;

/// <summary>
/// Caso de uso: detectar cupos liberados y notificar a la lista de espera (FIFO).
/// Recorre las entradas activas agrupadas por (servicio, profesional); para cada grupo
/// (cola FIFO) evalúa SOLO la entrada más antigua: si su servicio/profesional tiene ahora
/// un cupo reservable, se notifica a ese cliente y la entrada pasa a "notified". Las demás
/// del mismo grupo quedan activas para el siguiente cupo que se libere (nadie salta a otro
/// en el mismo slot). Reutiliza CheckAvailabilityUseCase (Motor de Reglas) para decidir
/// si el hueco es real y reservable. Patrón de envío copiado de SendRemindersUseCase.
/// </summary>
public class WaitlistNotificationUseCase : IWaitlistNotifier
{
    /// <summary>Días que una entrada puede estar activa antes de caducar (7 días fijo, decisión de MVP).</summary>
    public const int ExpiryDays = 7;

    /// <summary>Ventana por defecto de búsqueda hacia adelante cuando el cliente no dio un rango.</summary>
    private const int DefaultLookAheadDays = 7;

    private readonly IWaitlistEntryRepository _waitlistRepo;
    private readonly IClientRepository _clientRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IServiceTypeRepository _serviceTypeRepo;
    private readonly ITenantContext _tenantContext;
    private readonly IMessagingProvider _messagingProvider;
    private readonly IConversationSessionService _conversationSession;
    private readonly CheckAvailabilityUseCase _checkAvailability;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<WaitlistNotificationUseCase> _logger;

    public WaitlistNotificationUseCase(
        IWaitlistEntryRepository waitlistRepo,
        IClientRepository clientRepo,
        ITenantRepository tenantRepo,
        IServiceTypeRepository serviceTypeRepo,
        ITenantContext tenantContext,
        IMessagingProvider messagingProvider,
        IConversationSessionService conversationSession,
        CheckAvailabilityUseCase checkAvailability,
        IUnitOfWork unitOfWork,
        ILogger<WaitlistNotificationUseCase> logger)
    {
        _waitlistRepo = waitlistRepo;
        _clientRepo = clientRepo;
        _tenantRepo = tenantRepo;
        _serviceTypeRepo = serviceTypeRepo;
        _tenantContext = tenantContext;
        _messagingProvider = messagingProvider;
        _conversationSession = conversationSession;
        _checkAvailability = checkAvailability;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<int> ExecuteAsync(CancellationToken ct = default)
        => await ScanAndNotifyAsync(ct);

    public async Task<int> ScanAndNotifyAsync(CancellationToken ct = default)
    {
        var now = BusinessClock.Now;
        var entries = await _waitlistRepo.GetActiveAsync(ct);
        if (entries.Count == 0)
            return 0;

        bool modified = false;
        int notified = 0;

        // FIFO: agrupar por (servicio, profesional) y gestionar solo el líder de cada grupo.
        foreach (var group in entries.GroupBy(e => (e.IdServiceType, e.IdProfessional)))
        {
            var leader = group.OrderBy(e => e.FechaCreacion).First();
            try
            {
                var outcome = await ProcessEntryAsync(leader, now, ct);
                if (outcome == EntryOutcome.Notified)
                {
                    modified = true;
                    notified++;
                }
                else if (outcome == EntryOutcome.Expired)
                {
                    modified = true; // expirar también es un cambio que hay que persistir
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[Waitlist] Error procesando entrada {EntryId} (cliente {ClientId})",
                    leader.IdWaitlistEntry, leader.IdClient);
            }
        }

        if (modified)
            await _unitOfWork.SaveChangesAsync(ct);

        return notified;
    }

    private enum EntryOutcome { None, Notified, Expired }

    /// <summary>
    /// Procesa una entrada: expira si caducó; si el servicio/profesional tiene un cupo
    /// reservable, notifica al cliente y pasa a "notified".
    /// </summary>
    private async Task<EntryOutcome> ProcessEntryAsync(WaitlistEntry entry, DateTime now, CancellationToken ct)
    {
        // Expiración: 7 días fijo desde creación.
        if (entry.FechaCreacion.AddDays(ExpiryDays) < now)
        {
            entry.Estado = "expired";
            entry.FechaActualizacion = DateTime.UtcNow;
            await _waitlistRepo.UpdateAsync(entry, ct);
            _logger.LogInformation("[Waitlist] Entrada {Id} expirada (7 días)", entry.IdWaitlistEntry);
            return EntryOutcome.Expired;
        }

        var client = await _clientRepo.GetByIdAsync(entry.IdClient, ct);
        if (client == null || string.IsNullOrWhiteSpace(client.WhatsApp))
        {
            entry.Estado = "expired";
            entry.FechaActualizacion = DateTime.UtcNow;
            await _waitlistRepo.UpdateAsync(entry, ct);
            return EntryOutcome.Expired;
        }

        var tenant = await _tenantRepo.GetByIdAsync(entry.IdTenant, ct);
        if (tenant == null || string.IsNullOrWhiteSpace(tenant.WhatsAppPhoneNumberId))
            return EntryOutcome.None;

        var slots = await FindAvailableSlotsAsync(entry, tenant, ct);
        if (slots.Count == 0)
            return EntryOutcome.None; // aún no hay cupo; la entrada sigue activa

        var slot = slots[0];
        var sent = await NotifyAsync(entry, client, tenant, slot, ct);
        if (!sent)
            return EntryOutcome.None; // no se pudo entregar (sin template / fuera de sesión): queda activa para reintentar

        entry.Estado = "notified";
        entry.FechaActualizacion = DateTime.UtcNow;
        await _waitlistRepo.UpdateAsync(entry, ct);
        _logger.LogInformation("[Waitlist] Cupo liberado notificado a {Phone} (servicio {Service}, {Start})",
            client.WhatsApp, entry.IdServiceType, slot.Start);
        return EntryOutcome.Notified;
    }

    /// <summary>
    /// Consulta disponibilidad real del servicio/profesional de la entrada dentro de su
    /// ventana de preferencia (o una ventana por defecto). Filtra cupos que la antelación
    /// mínima del tenant aún no permite reservar, para no avisar un hueco no reservable.
    /// </summary>
    private async Task<List<TimeSlotDto>> FindAvailableSlotsAsync(
        WaitlistEntry entry, Tenant tenant, CancellationToken ct)
    {
        var now = BusinessClock.Now;
        var maxAhead = tenant.AntelacionMaximaDias > 0 ? tenant.AntelacionMaximaDias : DefaultLookAheadDays;

        var from = entry.FechaDesde ?? now;
        var to = entry.FechaHasta ?? now.AddDays(maxAhead);
        if (to < from)
            (from, to) = (to, from);

        // No mirar más allá de la antelación máxima del tenant.
        var horizon = now.AddDays(maxAhead);
        if (to > horizon)
            to = horizon;

        var query = new AvailabilityQueryDto
        {
            TenantId = entry.IdTenant,
            FechaInicio = DateOnly.FromDateTime(from),
            FechaFin = DateOnly.FromDateTime(to),
            ServiceTypeId = entry.IdServiceType,
            ProfessionalId = entry.IdProfessional
        };

        List<TimeSlotDto> slots;
        try
        {
            slots = await _checkAvailability.ExecuteAsync(query, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Waitlist] No se pudo consultar disponibilidad para entrada {Id}", entry.IdWaitlistEntry);
            return new List<TimeSlotDto>();
        }

        // CheckAvailability devuelve VENTANAS de disponibilidad (p.ej. todo el día 00:00–23:59)
        // con Start antes de "ahora"; lo que importa es que quede una porción reservable a
        // partir de la antelación mínima del tenant. Un slot con End > minStart tiene futuro reservable.
        var minStart = now.AddHours(Math.Max(0, tenant.AntelacionMinimaHoras));
        return slots
            .Where(s => s.End > minStart)
            .OrderBy(s => s.Start)
            .ToList();
    }

    /// <summary>
    /// Envía el aviso (template aprobado si está configurado; texto libre solo dentro de la
    /// ventana de sesión). Reutiliza la convención de SendRemindersUseCase.
    /// </summary>
    private async Task<bool> NotifyAsync(WaitlistEntry entry, Client client, Tenant tenant, TimeSlotDto slot, CancellationToken ct)
    {
        string? serviceName = null;
        var service = await _serviceTypeRepo.GetByIdAsync(entry.IdServiceType, ct);
        if (service != null) serviceName = service.Nombre;

        _tenantContext.SetTenant(
            tenant.IdTenant,
            calendarProvider: tenant.CalendarProvider ?? "google",
            whatsAppAccessToken: Env("WhatsApp__AccessToken", "WHATSAPP_ACCESS_TOKEN") ?? "",
            phoneNumberId: tenant.WhatsAppPhoneNumberId);

        var templateName = Env("WhatsApp__WaitlistTemplate", "WHATSAPP_WAITLIST_TEMPLATE");

        // CheckAvailability devuelve ventanas agregadas; si la ventana es de día completo (00:00)
        // no se puede asumir una hora exacta, así que se anuncia la fecha sin hora (la agenda el AI).
        var esDiaCompleto = slot.Start.Hour == 0 && slot.Start.Minute == 0;
        var fechaStr = slot.Start.ToString("dd/MM/yyyy");
        var cuandoStr = esDiaCompleto ? "el " + fechaStr : $"el {fechaStr} a las {slot.Start:HH:mm}";

        try
        {
            if (!string.IsNullOrWhiteSpace(templateName))
            {
                var wamId = await _messagingProvider.SendTemplateAsync(client.WhatsApp, templateName,
                    new Dictionary<string, string>
                    {
                        ["1"] = string.IsNullOrWhiteSpace(client.Nombre) ? "Hola" : client.Nombre,
                        ["2"] = serviceName ?? "el servicio",
                        ["3"] = cuandoStr
                    }, ct);
                return wamId != null;
            }

            if (_conversationSession.HasActiveSession(entry.IdTenant, client.WhatsApp))
            {
                var wamId = await _messagingProvider.SendTextAsync(client.WhatsApp,
                    BuildText(serviceName ?? "el servicio", cuandoStr), ct);
                return wamId != null;
            }

            _logger.LogInformation("[Waitlist] Sin template y fuera de ventana de sesión: no se envía a {Phone}", client.WhatsApp);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Waitlist] Error enviando aviso de cupo liberado a {Phone}", client.WhatsApp);
            return false;
        }
    }

    private static string BuildText(string service, string cuando)
        => $"""
            ¡Buenas noticias! 🎉 Se liberó un cupo para {service} {cuando}.
            Respondé si querés reservarlo. Podés decir "quiero el cupo" y te lo agendo de inmediato.
            (Solo hay cupo para una persona: se asigna por orden de llegada.)
            """;

    private static string? Env(string key, string altKey)
        => Environment.GetEnvironmentVariable(key) ?? Environment.GetEnvironmentVariable(altKey);
}