using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Infrastructure.Services;

/// <summary>
/// Servicio del handoff a asesor humano. Centraliza:
/// - abrir un ticket de escalado por conversación (dedup mientras haya uno abierto) y
///   notificar al asesor por WhatsApp con el contexto estructurado del turno;
/// - el canal del asesor por WhatsApp: sus mensajes se reenvían al cliente (ticket Pending
///   → Active) y el comando FIN cierra el ticket y devuelve el control al AI.
/// La cola y la auditoría viven en la tabla handoffs (sobrevive a redeploys).
/// </summary>
public class HandoffService
{
    private const string CloseCommand = "FIN";

    private readonly IHandoffRepository _handoffRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMessagingProvider _messaging;
    private readonly ITenantRepository _tenantRepo;
    private readonly ITenantContext _tenantContext;
    private readonly IConversationHistoryRepository _historyRepo;
    private readonly ILogger<HandoffService> _logger;

    public HandoffService(
        IHandoffRepository handoffRepo,
        IUnitOfWork unitOfWork,
        IMessagingProvider messaging,
        ITenantRepository tenantRepo,
        ITenantContext tenantContext,
        IConversationHistoryRepository historyRepo,
        ILogger<HandoffService> logger)
    {
        _handoffRepo = handoffRepo;
        _unitOfWork = unitOfWork;
        _messaging = messaging;
        _tenantRepo = tenantRepo;
        _tenantContext = tenantContext;
        _historyRepo = historyRepo;
        _logger = logger;
    }

    /// <summary>Número WhatsApp del asesor/dueño (env). null si no está configurado.</summary>
    public static string? GetOwnerNumber()
        => Environment.GetEnvironmentVariable("Notificaciones__WhatsAppDueno")
           ?? Environment.GetEnvironmentVariable("NOTIFICACIONES_WHATSAPP_DUENO");

    public static bool IsOwner(string senderPhone)
    {
        var owner = GetOwnerNumber();
        return !string.IsNullOrWhiteSpace(owner) && Normalize(senderPhone) == Normalize(owner);
    }

    /// <summary>FIN (con o sin '/') cierra el handoff y devuelve la conversación al AI.</summary>
    public static bool IsCloseCommand(string text)
        => string.Equals(text.Trim().TrimStart('/'), CloseCommand, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string phone)
        => new(phone.Where(char.IsDigit).ToArray());

    /// <summary>
    /// Abre un ticket de handoff para la conversación si no hay uno abierto (dedup) y
    /// notifica al asesor por WhatsApp. Devuelve el ticket creado, o null si ya había uno abierto.
    /// </summary>
    public async Task<Handoff?> EscalateAsync(
        Guid tenantId,
        string userPhone,
        string? clientName,
        string motivo,
        string? contexto,
        CancellationToken ct = default)
    {
        var open = await _handoffRepo.GetOpenByPhoneAsync(tenantId, userPhone, ct);
        if (open != null)
        {
            _logger.LogDebug("[Handoff] Conversación {Tenant}/{Phone} ya escalada, sin repetir", tenantId, userPhone);
            return null;
        }

        var handoff = new Handoff
        {
            IdTenant = tenantId,
            PhoneCliente = Normalize(userPhone),
            Motivo = motivo,
            Contexto = contexto,
            Estado = HandoffState.HumanPending
        };
        await _handoffRepo.AddAsync(handoff, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        await NotifyOwnerAsync(tenantId, userPhone, clientName, motivo, contexto, ct);
        return handoff;
    }

    private async Task NotifyOwnerAsync(
        Guid tenantId,
        string userPhone,
        string? clientName,
        string motivo,
        string? contexto,
        CancellationToken ct)
    {
        var ownerNumber = GetOwnerNumber();
        var tenant = await _tenantRepo.GetByIdAsync(tenantId, ct);
        if (string.IsNullOrWhiteSpace(ownerNumber) || tenant == null)
        {
            // Sin canal del asesor configurado: el ticket queda en la BD (cola/auditoría)
            // pero no hay aviso. No rompe el turno.
            _logger.LogInformation("[Handoff] Escalado sin canal del asesor configurado (cliente {Phone})", userPhone);
            return;
        }

        // El envío al asesor exige el contexto de tenant (mismo patrón que SendRemindersUseCase).
        _tenantContext.SetTenant(
            tenantId,
            calendarProvider: tenant.CalendarProvider ?? "google",
            whatsAppAccessToken: Environment.GetEnvironmentVariable("WhatsApp__AccessToken")
                               ?? Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN")
                               ?? "",
            phoneNumberId: tenant.WhatsAppPhoneNumberId ?? "");

        var clienteRef = string.IsNullOrWhiteSpace(clientName) ? userPhone : $"{clientName} ({userPhone})";
        var tenantRef = tenant.NombreComercial ?? tenant.Nombre;
        var contextoRef = string.IsNullOrWhiteSpace(contexto) ? "—" : contexto;

        var aviso = "⚠️ Escalado a asesor humano\n"
            + $"Tenant: {tenantRef}\n"
            + $"Cliente: {clienteRef}\n"
            + $"Motivo: {motivo}\n\n"
            + $"Acciones del asistente en este turno:\n{contextoRef}\n\n"
            + "Respondé para atender al cliente. Enviá FIN cuando termines y vuelve el asistente.";

        await _messaging.SendTextAsync(ownerNumber, aviso, ct);
        _logger.LogInformation("[Handoff] Escalado a humano notificado para {Phone}", userPhone);
    }

    /// <summary>Resultado de procesar un mensaje entrante del asesor.</summary>
    public enum OwnerReplyResult
    {
        /// <summary>El remitente NO es el asesor: se procesa como mensaje normal del cliente.</summary>
        NotOwner,

        /// <summary>El asesor escribió pero no hay handoff abierto en el tenant.</summary>
        NoOpenHandoff,

        /// <summary>El asesor mandó FIN: ticket cerrado (HumanPending/Active → AiResumed) y cliente avisado.</summary>
        ChatClosed,

        /// <summary>Respuesta del asesor reenviada al cliente (ticket Pending → Active).</summary>
        Forwarded
    }

    /// <summary>
    /// Procesa un mensaje entrante del asesor: toma el handoff abierto más antiguo del tenant,
    /// reenvía su respuesta al cliente (activando el ticket) o cierra con FIN. Devuelve
    /// NotOwner si el remitente no es el asesor (el mensaje sigue el flujo normal del AI).
    /// </summary>
    public async Task<OwnerReplyResult> HandleOwnerReplyAsync(
        Guid tenantId,
        string senderPhone,
        string text,
        CancellationToken ct = default)
    {
        if (!IsOwner(senderPhone))
            return OwnerReplyResult.NotOwner;

        var open = await _handoffRepo.GetOpenByTenantAsync(tenantId, ct);
        var handoff = open.FirstOrDefault();
        if (handoff == null)
        {
            _logger.LogInformation("[Handoff] Mensaje del asesor sin handoff abierto en {Tenant}, ignorado", tenantId);
            return OwnerReplyResult.NoOpenHandoff;
        }

        if (IsCloseCommand(text))
        {
            handoff.Estado = HandoffState.AiResumed;
            handoff.FechaActualizacion = DateTime.UtcNow;
            await _handoffRepo.UpdateAsync(handoff, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            _logger.LogInformation("[Handoff] Ticket {Id} cerrado por el asesor (control vuelve al AI)", handoff.IdHandoff);

            var cierreText = "Tu asesor finalizó la atención. El asistente virtual quedó disponible nuevamente. 😊";
            await SendToClientAsync(
                tenantId,
                handoff.PhoneCliente,
                cierreText,
                ct);
            // Cierra el ciclo del CRM: el aviso de cierre entra en el historial del cliente.
            await PersistAdvisorMessageAsync(tenantId, handoff.PhoneCliente, "assistant", cierreText, ct);
            return OwnerReplyResult.ChatClosed;
        }

        if (handoff.Estado == HandoffState.HumanPending)
            handoff.Estado = HandoffState.HumanActive;
        handoff.UltimoMensajeHumano = text;
        handoff.FechaActualizacion = DateTime.UtcNow;
        await _handoffRepo.UpdateAsync(handoff, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation("[Handoff] Respuesta del asesor reenviada al cliente {Phone}", handoff.PhoneCliente);
        await SendToClientAsync(tenantId, handoff.PhoneCliente, text, ct);
        // Cierra el ciclo del CRM: la respuesta del asesor entra en el historial del cliente
        // con el rol "owner" (asesor humano) para distinguirla de user/assistant.
        await PersistAdvisorMessageAsync(tenantId, handoff.PhoneCliente, "owner", text, ct);
        return OwnerReplyResult.Forwarded;
    }

    private async Task SendToClientAsync(Guid tenantId, string phone, string text, CancellationToken ct)
    {
        var tenant = await _tenantRepo.GetByIdAsync(tenantId, ct);
        if (tenant == null) return;

        _tenantContext.SetTenant(
            tenantId,
            calendarProvider: tenant.CalendarProvider ?? "google",
            whatsAppAccessToken: Environment.GetEnvironmentVariable("WhatsApp__AccessToken")
                               ?? Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN")
                               ?? "",
            phoneNumberId: tenant.WhatsAppPhoneNumberId ?? "");
        await _messaging.SendTextAsync(phone, text, ct);
    }

    /// <summary>
    /// Persiste un mensaje del canal del asesor en el historial durable del cliente (pilar
    /// "Conversaciones" del CRM), keyed por el teléfono del CLIENTE para que aparezca en su
    /// transcripción. El rol distingue el asesor humano ("owner") del resto. Falla silencioso:
    /// un problema al guardar el historial nunca debe romper el turno.
    /// </summary>
    private async Task PersistAdvisorMessageAsync(
        Guid tenantId,
        string phoneCliente,
        string role,
        string content,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            if (content.Length > 4000)
                content = content[..4000];

            await _historyRepo.AddAsync(new ConversationMessage
            {
                IdConversationMessage = Guid.NewGuid(),
                IdTenant = tenantId,
                PhoneCliente = phoneCliente,
                Role = role,
                Content = content
            }, ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Handoff] No se pudo persistir el mensaje del asesor de {Phone}", phoneCliente);
        }
    }
}