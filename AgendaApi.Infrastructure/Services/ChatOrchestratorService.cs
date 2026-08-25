using System.Diagnostics;
using System.Text.Json;
using AgendaApi.Application.Support;
using AgendaApi.Application.Tools;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.AiProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Infrastructure.Services;

public class ChatOrchestratorService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConversationMemoryService _conversationMemory;
    private readonly ConversationStateService _conversationState;
    private readonly ILogger<ChatOrchestratorService> _logger;

    private const int MaxToolIterations = 7;
    private const int MaxReasoningRetries = 2;

    public ChatOrchestratorService(
        IServiceScopeFactory scopeFactory,
        ConversationMemoryService conversationMemory,
        ConversationStateService conversationState,
        ILogger<ChatOrchestratorService> logger)
    {
        _scopeFactory = scopeFactory;
        _conversationMemory = conversationMemory;
        _conversationState = conversationState;
        _logger = logger;
    }

    public async Task ProcessMessageAsync(
        string userId,
        string messageContent,
        Guid tenantId,
        CancellationToken ct = default,
        string? clientName = null,
        string? phone = null,
        string? username = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        // Destino de envío de la respuesta: el teléfono si se conoce (la API de WhatsApp da prioridad
        // a "to" sobre "recipient"); si no, el BSUID (el adaptador lo envía bajo "recipient").
        // Destino de envío: teléfono si lo hay, si no el username global. NUNCA el BSUID (userId):
        // Meta rechaza el user_id como destinatario (campo `recipient` inexistente); solo teléfono o
        // username son valores válidos para `to`.
        var replyTo = string.IsNullOrEmpty(phone) ? (username ?? "") : phone!;

        // La entrega final del turno — persistir la respuesta en el historial (dashboard en vivo)
        // — NO debe quedar sujeta al timeout de 15 s (MessageBufferService).
        // Si la IA tarda cerca del límite, el token de turno (ct) ya está cancelado y el
        // SaveChangesAsync revienta con OperationCanceledException: el mensaje sale por WhatsApp
        // pero nunca se guarda y desaparece del dashboard. Se usa un token de entrega propio
        // (sin reloj) para el persistido/entrega de la respuesta final.
        var deliveryToken = CancellationToken.None;

        // Cadena de proveedores: se prueba en orden hasta que uno responde.
        // OpenRouter (modelos :free) PRIMERO: es más resiliente que Groq, cuya cuota gratuita
        // diaria rate-limitea con 429 y hacía caer turnos al genérico "Lo siento, tuve un
        // problema" (contesta a unos y a otros no, según el momento en que se agota el cupo).
        // Groq queda como respaldo gratis; luego OpenAI y Anthropic como fallback de pago.
        var aiProviders = new (string Name, IAiProvider Provider, List<object> Tools)[]
        {
            // OpenAI out of the chain: its global key is rejected/broken, and a provider that
            // hangs on auth burns the ~30s turn budget across the 2 retry passes (new clients
            // hit the generic "Lo siento, tuve un problema"). OpenRouter + Groq (free) plus
            // Anthropic (paid) cover the fallback without the broken lane.
            ("OpenRouter", services.GetRequiredService<OpenRouterProvider>(), AppointmentToolDefinitions.GetOpenAiToolDefinitions()),
            ("Groq", services.GetRequiredService<GroqProvider>(), AppointmentToolDefinitions.GetOpenAiToolDefinitions()),
            ("Anthropic", services.GetRequiredService<AnthropicProvider>(), AppointmentToolDefinitions.GetAnthropicToolDefinitions())
        };
        var messaging = services.GetRequiredService<IMessagingProvider>();
        var tenantContext = services.GetRequiredService<ITenantContext>();
        var conversationHistory = services.GetRequiredService<IConversationHistoryRepository>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();

        // Cargar datos reales del tenant desde la BD
        var tenantRepo = services.GetRequiredService<ITenantRepository>();
        var tenant = await tenantRepo.GetByIdAsync(tenantId, ct);

        tenantContext.SetTenant(
            tenantId,
            calendarProvider: tenant?.CalendarProvider ?? "google",
            whatsAppAccessToken: Environment.GetEnvironmentVariable("WhatsApp__AccessToken") ?? Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN") ?? "",
            phoneNumberId: tenant?.WhatsAppPhoneNumberId ?? "");

        var conversationKey = ConversationMemoryService.GetKey(tenantId, userId);

        // Reserva en curso negociada EN ESTA conversación (servicio/profesional/fecha). Sirve
        // para anclar la confirmación a lo que se acordó aquí y no a cualquier cita pendiente
        // vieja/huérfana que el cliente tenga en la BD (evita el bug que confirmaba la cita
        // equivocada: ver TryConfirmUpcomingAsync).
        var pendingBooking = _conversationState.GetPendingBooking(conversationKey);

        // FAST-PATH DE CONFIRMACIÓN: cuando el cliente responde un token claramente de
        // confirmación (CONFIRMAR / CONFIRMO / "si, confirmo"...), confirmamos de forma directa
        // y determinista la cita que coincide con la negociación en curso, sin depender de que el
        // modelo elija la herramienta confirm_appointment. En el tier gratuito el modelo a veces
        // contesta en texto "confirma CONFIRMAR" en vez de ejecutar la herramienta, lo que
        // entraba en un loop: el cliente confirma y le vuelven a pedir que confirme. Este
        // fast-path lo evita. Nunca confirma a ciegas una cita pendiente no relacionada.
        if (IsConfirmationIntent(messageContent))
        {
            var confirmed = await TryConfirmUpcomingAsync(services, tenantId, userId, pendingBooking, ct);
            if (confirmed != null)
            {
                string svcName = "tu cita";
                try
                {
                    var svcRepo = services.GetRequiredService<IServiceTypeRepository>();
                    svcName = (await svcRepo.GetByTenantIdAsync(tenantId, ct))
                        .FirstOrDefault(s => s.IdServiceType == confirmed.IdServiceType)?.Nombre ?? "tu cita";
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Orchestrator] No se resolvió el servicio para el fast-path de confirmación");
                }

                var confirmText = $"✓ Cita confirmada: {svcName}\n📅 {confirmed.FechaInicio:dd/MM/yyyy} a las {confirmed.FechaInicio:HH:mm} hs. ¡Te esperamos!";
                _conversationMemory.AddUser(conversationKey, messageContent);
                await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userId, "user", messageContent, ct);
                await messaging.SendTextAsync(replyTo, confirmText);
                _conversationMemory.AddAssistant(conversationKey, confirmText);
                await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userId, "assistant", confirmText, deliveryToken);
                _logger.LogInformation("[Orchestrator] Cita {Id} confirmada por fast-path CONFIRMAR para {From}",
                    confirmed.IdAppointment, userId);
                return;
            }
            // Sin cita pendiente: dejamos que el AI explique (el fast-path no debe romper el turno).
        }

        // Cargar los tipos de servicio reales del tenant para que la IA solo pueda
        // sugerir/agendar servicios que de verdad existen (evita nombres inventados).
        var serviceTypeRepo = services.GetRequiredService<IServiceTypeRepository>();
        var serviceTypes = await serviceTypeRepo.GetByTenantIdAsync(tenantId, ct);

        // Profesionales reales del tenant para que la IA use nombres exactos al asignar quién atiende.
        var professionalRepo = services.GetRequiredService<IProfessionalRepository>();
        var professionals = await professionalRepo.GetActiveByTenantIdAsync(tenantId, ct);

        _logger.LogInformation("[Orchestrator] Procesando mensaje de {From} para tenant {Tenant}",
            userId, tenantId);

        // (pendingBooking ya se obtuvo arriba para el fast-path de confirmación.)
        // CRM: memoria operativa del cliente. Compila perfil/estado/historial desde la BD
        // (creando el cliente si es su primer contacto) y se inyecta en el system prompt para
        // que ADAM conozca el contexto del cliente al atenderlo, agendar o hacer seguimiento.
        string clientContext = "";
        try
        {
            var clientContextService = services.GetRequiredService<ClientContextService>();
            clientContext = await clientContextService.BuildClientContextAsync(tenantId, userId, ct, clientName, phone, username);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CRM] No se pudo compilar el contexto del cliente {From}", userId);
        }

        // Identidad mostrada al AI: el teléfono si se conoce, si no el BSUID.
        var displayIdentity = string.IsNullOrEmpty(phone) ? userId : phone!;
        var systemPrompt = GetSystemPrompt(displayIdentity, clientName, tenant, serviceTypes, professionals, pendingBooking, clientContext);

        // Cargar historial previo de la conversación (si existe) para conservar contexto.
        var messages = _conversationMemory.GetHistory(conversationKey, systemPrompt);
        messages.Add(new ChatMessage { Role = "user", Content = messageContent });
        _conversationMemory.AddUser(conversationKey, messageContent);
        await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userId, "user", messageContent, ct);

        // Acciones del AI en este turno en texto legible (se anotan por tool ejecutada).
        var accionesPrevias = new List<string>();

        // Rastreo de churn de disponibilidad: si el turno se gasta solo probando
        // check_availability y nunca logra crear la cita (p. ej. el negocio está cerrado
        // el día pedido y el modelo sigue cambiando fecha/servicio), la situación es un
        // caso comercial normal — informar al cliente.
        bool sawAvailabilityProbe = false;
        bool sawCreateAppointment = false;
        // Regeneración acotada cuando el modelo responde con su razonamiento interno en vez de
        // un mensaje para el cliente (se fuerza un número limitado de reintentos, ver abajo).
        int reasoningGuard = 0;

        for (int iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            _logger.LogDebug("[Orchestrator] Iteracion {Iter}/{Max}", iteration + 1, MaxToolIterations);

            // Probar la cadena de proveedores con reintentos tolerantes a fallos transitorios
            // (429/rate-limit o timeout de los tier :free). Un tropiezo puntual de un proveedor
            // ya no tumba el turno al genérico "Lo siento, tuve un problema" de inmediato.
            var chainStartedAt = Stopwatch.GetTimestamp();
            var (usedProvider, result, providerLastErrors, attemptsMade) =
                await TryGenerateWithRetryAsync(aiProviders, messages, ct);
            var chainElapsedMs = Stopwatch.GetElapsedTime(chainStartedAt).TotalMilliseconds;

            if (usedProvider == null)
            {
                const string errorText = "Lo siento, tuve un problema. Por favor intenta mas tarde.";
                // Registrar el turno del asistente ANTES de enviar: si el envío de WhatsApp
                // lanza una excepción tras entregar, el histórico igual queda persistido
                // (y aparece en el dashboard en vivo).
                _conversationMemory.AddAssistant(conversationKey, errorText);
                await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userId, "assistant", errorText, deliveryToken);
                await messaging.SendTextAsync(replyTo, errorText);

                // Causa del turno perdido: timeout del presupuesto del turno ("timeout") o cadena
                // de proveedores agotada ("all_providers_failed"). El resumen por proveedor alimenta
                // el log Y el registro durable (GET api/v1/dashboard/failures), legible sin SSH.
                var motivo = ct.IsCancellationRequested ? "timeout" : "all_providers_failed";
                var resumenProveedores = string.Join(" | ", aiProviders.Select(p =>
                    $"{p.Name}: {(providerLastErrors.TryGetValue(p.Name, out var err) ? err : "(sin error registrado)")}"));

                // El timeout del turno (se dispara cuando la IA tarda demasiado, p. ej. por el rate-limit
                // de Groq que hace lenta la generación) no debe confundirse con un fallo de todos
                // los proveedores: se registra y ya se respondió el genérico.
                if (ct.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "[Orchestrator] Turno cancelado por timeout antes de obtener respuesta de la IA: {Attempts} intentos sobre la cadena de proveedores en {Elapsed:F0} ms",
                        attemptsMade, chainElapsedMs);
                }
                else
                {
                    _logger.LogError(
                        "[Orchestrator] Todos los proveedores fallaron tras {Attempts} intentos ({Elapsed:F0} ms). Último error por proveedor: {Resumen}",
                        attemptsMade, chainElapsedMs, resumenProveedores);
                }

                await PersistTurnFailureAsync(services, tenantId, userId, motivo,
                    $"intentos={attemptsMade}; elapsed_ms={chainElapsedMs:F0}; {resumenProveedores}");
                return;
            }
            _logger.LogInformation("[Orchestrator] Respondiendo con {Provider}", usedProvider);

            // El mensaje del asistente SIEMPRE se agrega cuando hay tool_calls (aunque el texto sea vacío),
            // y debe incluir la lista de tool_calls — las APIs de OpenAI/Groq la exigen en la siguiente
            // iteración para poder correlacionar los resultados de las herramientas.
            if (result.ToolCalls is { Count: > 0 })
            {
                var assistantMsg = new ChatMessage { Role = "assistant", Content = result.TextContent ?? "" };
                assistantMsg.ToolCalls.AddRange(result.ToolCalls);
                messages.Add(assistantMsg);
            }
            else if (!string.IsNullOrWhiteSpace(result.TextContent))
            {
                messages.Add(new ChatMessage { Role = "assistant", Content = result.TextContent });
            }

            if (result.FinishReason != "tool_calls" || result.ToolCalls == null || result.ToolCalls.Count == 0)
            {
                var responseText = result.TextContent ?? "En que mas puedo ayudarte?";

                // Fuga de razonamiento: si la "respuesta final" es en realidad el razonamiento
                // interno del modelo (menciona herramientas, "UTC", "el cliente quiere", slots…),
                // NUNCA se envía ni se persiste. Se fuerza una regeneración acotada para que el
                // modelo dé un mensaje real al cliente. (Caso real: un tier :free respondió su
                // chain-of-thought como texto terminal y llegó al cliente/CRM.)
                if (LooksLikeReasoning(responseText))
                {
                    _logger.LogWarning("[Orchestrator] Respuesta final parece razonamiento interno; se descarta (try {Try}): {Text}",
                        reasoningGuard + 1, Truncate(responseText, 180));

                    if (reasoningGuard < MaxReasoningRetries)
                    {
                        reasoningGuard++;
                        messages.Add(new ChatMessage
                        {
                            Role = "user",
                            Content = "IMPORTANTE: responde directamente al cliente con un mensaje amable y final en espanol. " +
                                      "NO describas tu proceso interno ni el contenido de las herramientas, NO menciones UTC, " +
                                      "check_availability, slots ni nombres de herramientas."
                        });
                        continue; // regenerar (el for de proveedores con reintento corre de nuevo)
                    }

                    // Limite de regeneraciones: no mandamos el razonamiento, se degrada elegante.
                    const string fallbackText = "Estoy procesando tu solicitud. Un asesor te contactara pronto para confirmar.";
                    _logger.LogWarning("[Orchestrator] Limite de regeneracion alcanzado; se responde genérico (no se fuga el razonamiento)");
                    await messaging.SendTextAsync(replyTo, fallbackText);
                    _conversationMemory.AddAssistant(conversationKey, fallbackText);
                    await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userId, "assistant", fallbackText, deliveryToken);
                    return;
                }

                _logger.LogInformation("[Orchestrator] Respuesta final del modelo, sin tool calls");
                await messaging.SendTextAsync(replyTo, responseText);
                _conversationMemory.AddAssistant(conversationKey, responseText);
                await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userId, "assistant", responseText, deliveryToken);
                return;
            }

            foreach (var toolCall in result.ToolCalls)
            {
                _logger.LogInformation("[Orchestrator] Ejecutando tool: {Name}({Args})",
                    toolCall.Name, toolCall.Arguments);

                if (toolCall.Name == "check_availability") sawAvailabilityProbe = true;
                if (toolCall.Name == "create_appointment") sawCreateAppointment = true;

                var toolResult = await ExecuteToolAsync(toolCall.Name, toolCall.Arguments, services, tenantId, userId, clientName, accionesPrevias, ct);
                accionesPrevias.Add(ToolActionSummarizer.Summarize(toolCall.Name, toolResult));

                // Rastrear el agendamiento en curso (P3): lo que el cliente dejó a medio armar.
                TrackBookingProgress(conversationKey, toolCall, toolResult);

                messages.Add(new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = toolCall.Id,
                    ToolName = toolCall.Name,
                    Content = toolResult
                });
            }
        }

        _logger.LogWarning("[Orchestrator] Maximo de iteraciones alcanzado ({Max})", MaxToolIterations);

        // Churn de disponibilidad: el turno solo probó check_availability y nunca creó la cita
        // (p. ej. el día pedido el negocio está cerrado y el modelo siguió sondeando otras
        // fechas/servicios). Es un caso comercial normal, no una falla de infraestructura:
        // respondemos la situación directamente.
        if (sawAvailabilityProbe && !sawCreateAppointment)
        {
            const string noSlotsText = "No encontré un horario disponible para la fecha que me pediste. 😕 " +
                "¿Quieres que revise otro día u horario, o prefieres que te agregue a la lista de espera " +
                "del servicio y te aviso cuando se libere un cupo?";
            _logger.LogInformation("[Orchestrator] Sin cupos (churn de disponibilidad): respondo la situación");
            await messaging.SendTextAsync(replyTo, noSlotsText);
            _conversationMemory.AddAssistant(conversationKey, noSlotsText);
            await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userId, "assistant", noSlotsText, deliveryToken);
            return;
        }

        const string maxIterText = "Estoy procesando tu solicitud. Un asesor te contactara pronto para confirmar.";
        await messaging.SendTextAsync(replyTo, maxIterText);
        _conversationMemory.AddAssistant(conversationKey, maxIterText);
        await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userId, "assistant", maxIterText, deliveryToken);
    }

    /// <summary>
    /// Detecta una intención explícita de confirmación de cita (CONFIRMAR, CONFIRMO,
    /// "si, confirmo"...). Evita falsos positivos con negaciones ("no confirmo").
    /// </summary>
    private static bool IsConfirmationIntent(string? content)
    {
        var n = content?.Trim().ToUpperInvariant() ?? "";
        if (n.Length == 0 || n.StartsWith("NO", StringComparison.Ordinal))
            return false;
        return n.StartsWith("CONFIRM", StringComparison.Ordinal)
            || (n.StartsWith("SI", StringComparison.Ordinal) && n.Contains("CONFIRM", StringComparison.Ordinal));
    }

    /// <summary>
    /// Confirma la cita pendiente que coincide con la negociación de ESTA conversación
    /// (<paramref name="pending"/> = reserva en curso). Devuelve la cita confirmada, o null si
    /// no hay una cita inequívoca que confirmar.
    ///
    /// <para>NO confirma "la próxima cita pendiente del cliente" a ciegas: si el cliente
    /// confirmó una negociación que aún no se materializó en una cita pendiente (el modelo
    /// preguntó "¿confirmas?" antes de create_appointment), o si la reserva en curso no
    /// coincide con ninguna cita pendiente, devolvemos null y dejamos que el modelo cree y
    /// confirme la cita correcta. Esto evita confirmar citas viejas/huérfanas de sesiones
    /// previas (bug de confirmación de cita equivocada).</para>
    /// </summary>
    private async Task<Domain.Entities.Appointment?> TryConfirmUpcomingAsync(
        IServiceProvider services,
        Guid tenantId,
        string userPhone,
        PendingBooking? pending,
        CancellationToken ct)
    {
        try
        {
            var clientRepo = services.GetRequiredService<IClientRepository>();
            var appointmentRepo = services.GetRequiredService<IAppointmentRepository>();
            var useCase = services.GetRequiredService<Application.UseCases.ConfirmAppointmentUseCase>();

            var client = await clientRepo.GetByWhatsAppAsync(userPhone, tenantId, ct);
            if (client == null)
                return null;

            // "Ahora" del negocio en hora local marcado como UTC (misma convención que el use case).
            var businessNow = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById(Environment.GetEnvironmentVariable("Calendar__TimeZone") ?? "America/Bogota"));
            var now = DateTime.SpecifyKind(businessNow, DateTimeKind.Utc);

            var pendings = (await appointmentRepo.GetByClientIdAsync(client.IdClient, ct))
                .Where(a => a.FechaInicio >= now && a.Estado == "pending")
                .OrderBy(a => a.FechaInicio)
                .ToList();
            if (pendings.Count == 0)
                return null;

            // Ancla: la reserva en curso describe lo que se negoció en esta conversación.
            if (pending != null
                && (!string.IsNullOrWhiteSpace(pending.ServiceTypeName) || pending.Fecha.HasValue))
            {
                IReadOnlyDictionary<Guid, string>? serviceNames = null;
                if (!string.IsNullOrWhiteSpace(pending.ServiceTypeName))
                {
                    var svcRepo = services.GetRequiredService<IServiceTypeRepository>();
                    serviceNames = (await svcRepo.GetByTenantIdAsync(tenantId, ct))
                        .ToDictionary(s => s.IdServiceType, s => s.Nombre);
                }

                var matches = pendings.Where(a =>
                        (string.IsNullOrWhiteSpace(pending.ServiceTypeName)
                            || (serviceNames!.TryGetValue(a.IdServiceType, out var n) && SameName(n, pending.ServiceTypeName)))
                        && (!pending.Fecha.HasValue
                            || a.FechaInicio.Date == pending.Fecha.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified).Date))
                    .OrderBy(a => a.FechaInicio)
                    .ToList();

                // La negociación no coincide con ninguna cita pendiente real (el modelo aún no
                // materializó create_appointment): NO confirmamos a ciegas una cita no relacionada.
                if (matches.Count == 0)
                    return null;

                // Coinciden varias (mismo servicio/día en distintas horas): confirmar la primera.
                var anchorTarget = matches[0];
                return await ConfirmAsync(useCase, tenantId, anchorTarget, ct);
            }

            // Sin ancla (la cita pendiente ya se creó en esta conversación): preferimos la cita
            // pendiente MÁS RECIÉN CREADA (la agendada en este turno), no la más lejana.
            var target = pendings.OrderByDescending(a => a.FechaCreacion).First();
            return await ConfirmAsync(useCase, tenantId, target, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Orchestrator] Fast-path de confirmación falló para {Phone}: {Msg}", userPhone, ex.Message);
            return null;
        }
    }

    /// <summary>Confirma una cita concreta vía use case y devuelve la cita si se confirmó.</summary>
    private async Task<Domain.Entities.Appointment?> ConfirmAsync(
        Application.UseCases.ConfirmAppointmentUseCase useCase,
        Guid tenantId,
        Domain.Entities.Appointment appointment,
        CancellationToken ct)
    {
        var dto = new Application.DTOs.AppointmentCancelDto
        {
            TenantId = tenantId,
            AppointmentIdentifier = appointment.IdAppointment.ToString()
        };
        var result = await useCase.ExecuteAsync(dto, ct);
        return result != null ? appointment : null;
    }

    /// <summary>Compara nombres de servicio tolerante a mayúsculas, acentos y la palabra "de".</summary>
    private static bool SameName(string a, string b)
    {
        static string Norm(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            var form = s.Normalize(System.Text.NormalizationForm.FormD);
            foreach (var ch in form)
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                        != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            return sb.ToString().ToLowerInvariant().Trim();
        }
        var na = Norm(a);
        var nb = Norm(b);
        return na == nb || na.Contains(nb) || nb.Contains(na);
    }

    /// <summary>
    /// Interpreta una fecha/hora que envía la IA como HORA LOCAL del negocio
    /// (convención "hora local disfrazada de UTC"). Se descarta cualquier offset o zona
    /// (Z, ±HH:MM) que el modelo agregue: el valor wall-clock ES la hora local real.
    /// Antes, pasar 15:00Z (que el modelo cree = 10:00 local) guardaba 15:00 como hora local.
    /// </summary>
    private static DateTime ParseLocalDateTime(string? raw)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            raw?.Trim() ?? "", @"(Z|[+-]\d{2}:?\d{2})$", "");
        // Ya sin offset/zona, el valor wall-clock es la hora local real; se marca Kind=Utc
        // (convención "hora local disfrazada de UTC"). El texto puede venir vacío ("") si el modelo
        // no mandó el campo: no debe explotar el turno.
        if (string.IsNullOrWhiteSpace(cleaned)) return DateTime.MinValue;
        var parsed = DateTime.Parse(cleaned);
        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    /// <summary>
    /// Prueba la cadena de proveedores de IA con reintentos tolerantes a fallos TRANSITORIOS
    /// (429/rate-limit o timeout de los tier :free). Sin esto, un solo tropiezo puntual de todos
    /// los proveedores en el primer pase tumbaba el turno al genérico "Lo siento, tuve un problema".
    /// Además devuelve, por proveedor, el último error visto y cuántos intentos se hicieron: sin
    /// ese rastro, la causa del turno perdido era invisible (el catch anterior era vacío).
    /// </summary>
    private async Task<(string? Provider, AiToolCallResult Result, Dictionary<string, string> LastErrors, int Attempts)> TryGenerateWithRetryAsync(
        (string Name, IAiProvider Provider, List<object> Tools)[] aiProviders,
        List<ChatMessage> messages,
        CancellationToken ct)
    {
        AiToolCallResult result = new() { Success = false };
        string? usedProvider = null;
        var lastErrors = new Dictionary<string, string>();
        int attempts = 0;
        var chainStart = Stopwatch.GetTimestamp();

        // 2 pases: el inicial + un reintento con pausa para dar margen al rate-limit.
        for (int attempt = 0; attempt < 2 && usedProvider == null; attempt++)
        {
            if (attempt > 0)
            {
                try { await Task.Delay(600); } catch { break; }
            }

            foreach (var p in aiProviders)
            {
                attempts++;
                try
                {
                    result = await p.Provider.GenerateResponseWithToolsAsync(messages, p.Tools, ct);
                    if (result.Success)
                    {
                        usedProvider = p.Name;
                        break;
                    }
                    // El proveedor respondió con fallo (4xx/5xx ya manejado por cada adaptador):
                    // queda como su último error para el resumen del turno fallido.
                    lastErrors[p.Name] = Truncate(result.TextContent ?? "(sin contenido)", 300);
                }
                catch (Exception ex)
                {
                    // Antes este catch era vacío y la causa del turno perdido era invisible.
                    // Se registra (proveedor, pase/intento y ms transcurridos) y se guarda el mensaje
                    // para el resumen final si TODOS los proveedores terminan fallando. El control
                    // de flujo no cambia: se reintenta en el siguiente pase/proveedor igual que antes.
                    lastErrors[p.Name] = Truncate(ex.Message, 300);
                    _logger.LogWarning(ex,
                        "[Orchestrator] Proveedor {Provider} falló (pase {Pass}, intento {Attempt}, {Elapsed:F0} ms desde el inicio de la cadena): {Msg}",
                        p.Name, attempt + 1, attempts, Stopwatch.GetElapsedTime(chainStart).TotalMilliseconds, ex.Message);
                }
            }
        }

        return (usedProvider, result, lastErrors, attempts);
    }

    /// <summary>
    /// Heurística para detectar si una "respuesta final" es en realidad razonamiento interno
    /// del modelo (chain-of-thought) que no debe llegar al cliente ni al CRM. Requiere >= 2
    /// señales para reducir falsos positivos.
    /// </summary>
    private static bool LooksLikeReasoning(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.ToLowerInvariant();
        int signals = 0;

        string[] tools = { "check_availability", "create_appointment", "cancel_appointment",
                                 "confirm_appointment", "reschedule_appointment", "list_appointments",
                                 "add_to_waitlist" };
        if (tools.Any(t.Contains)) signals++;
        if (t.Contains("utc")) signals++;
        if (t.Contains("slot")) signals++;
        if (t.Contains("el cliente quiere") || t.Contains("el usuario quiere")) signals++;
        if (t.Contains("muestra dos slots") || t.Contains("los slots")) signals++;
        if (t.StartsWith("el cliente") || t.StartsWith("el cliente quiere")) signals++;

        return signals >= 2;
    }

    /// <summary>Recorta un texto largo para logs (evita volcar mensajes completos).</summary>
    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text[..max] + "…";
    }

    private async Task<string> ExecuteToolAsync(
        string toolName,
        string argumentsJson,
        IServiceProvider services,
        Guid tenantId,
        string userPhone,
        string? clientName,
        IReadOnlyList<string> accionesPrevias,
        CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var args = doc.RootElement;

            return toolName switch
            {
                "check_availability" => await CheckAvailabilityAsync(args, services, tenantId, ct),
                "create_appointment" => await CreateAppointmentAsync(args, services, tenantId, userPhone, clientName, ct),
                "cancel_appointment" => await CancelAppointmentAsync(args, services, tenantId, ct),
                "confirm_appointment" => await ConfirmAppointmentAsync(args, services, tenantId, ct),
                "reschedule_appointment" => await RescheduleAppointmentAsync(args, services, tenantId, ct),
                "list_appointments" => await ListAppointmentsAsync(args, services, tenantId, ct),
                "add_to_waitlist" => await AddToWaitlistAsync(args, services, tenantId, userPhone, clientName, ct),
                _ => "{\"error\":\"Tool desconocida: " + toolName + "\"}"
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[Orchestrator] Error parseando argumentos de {Tool}", toolName);
            return "{\"error\":\"Error parsing arguments: " + ex.Message + "\"}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Orchestrator] Error ejecutando tool {Tool}", toolName);
            return "{\"error\":\"" + ex.Message + "\"}";
        }
    }

    private static string GetSystemPrompt(string userPhone, string? clientName, Domain.Entities.Tenant? business, List<Domain.Entities.ServiceType>? serviceTypes = null, List<Domain.Entities.Professional>? professionals = null, PendingBooking? pendingBooking = null, string? clientContext = null)
    {
        string senderIdentity = string.IsNullOrWhiteSpace(clientName)
            ? $"El cliente que te escribe tiene el WhatsApp {userPhone}."
            : $"El cliente que te escribe se llama {clientName} y su WhatsApp es {userPhone}.";

        // Datos del negocio: nombre comercial, dirección y teléfono reales del tenant. La IA
        // DEBE responder con estos datos exactos (no inventados) ante preguntas del negocio.
        var cultEsp = System.Globalization.CultureInfo.GetCultureInfo("es-ES");
        string negocioNombre = string.IsNullOrWhiteSpace(business?.NombreComercial)
            ? business?.Nombre ?? "el negocio"
            : business.NombreComercial;
        var negocio = new System.Text.StringBuilder();
        negocio.Append("\n\nDATOS DEL NEGOCIO (datos exactos del negocio — úsalos siempre y NO los inventes):\n");
        negocio.Append("- Negocio: ").Append(negocioNombre).Append('\n');
        if (!string.IsNullOrWhiteSpace(business?.Direccion)) negocio.Append("- Dirección: ").Append(business.Direccion).Append('\n');
        if (!string.IsNullOrWhiteSpace(business?.Telefono)) negocio.Append("- Teléfono: ").Append(business.Telefono).Append('\n');
        negocio.Append("- Cuando te pregunten precio, duración u horarios responde SIEMPRE con los datos reales de SERVICIOS DISPONIBLES y de check_availability. Nunca los inventes.");
        string datosNegocio = negocio.ToString();

        // Lista real de servicios disponibles (con precio y duración del tenant): la IA SOLO debe
        // sugerir/agendar estos y responder precios/duración con estos valores exactos.
        // Si no viene, se omite el bloque (por compatibilidad con pruebas).
        string serviciosDisponibles = serviceTypes is { Count: > 0 }
            ? @"

SERVICIOS DISPONIBLES (lista exacta del negocio — estos son los UNICOS servicios, con su precio y duración reales. NO inventes ni modifiques nombres, precios ni duraciones, NO agregues servicios que no esten aqui):
" + string.Join("\n", serviceTypes.Where(s => s.Activo).Select(s => FormatServiceLine(s, cultEsp))) + @"
- Cuando el cliente pida un servicio que NO este en esta lista, informale que no esta disponible y sugiere los que si hay. NUNCA intentes agendar un servicio fuera de esta lista. Cuando pregunten el precio o la duración, responde con el dato real de esta lista."
            : "";

        // Profesionales disponibles: la IA debe usar nombres EXACTOS de esta lista al asignar
        // quién atiende la cita (igual que con los servicios).
        string profesionalesDisponibles = professionals is { Count: > 0 }
            ? @"

PROFESIONALES (lista exacta del negocio — quiénes atienden citas):
" + string.Join("\n", professionals.Where(p => p.Activo).Select(p => $"- {p.Nombre}")) + @"
- Si el cliente pide un profesional o pregunta con quién será atendido, pregunta cuál prefiere y usa SIEMPRE un nombre de esta lista exacta en create_appointment."
            : "";

        // Reserva en curso: lo que el cliente dejó a medio agendar en esta conversación (P3).
        string reservaEnCurso = FormatPendingBooking(pendingBooking);

        // Inyectar la fecha actual: los LLM no conocen la fecha real y tienden a hibernar el año
        // (en producción llegaron a generar 2024 para "el viernes"). Con la fecha como referencia
        // calculan horizontes futuros correctos. Se usa cultura es-ES para nombres de día/mes fijos.
        var cult = System.Globalization.CultureInfo.GetCultureInfo("es-ES");
        // La fecha/hora que se inyecta al LLM como "hoy" debe ser la del NEGOCIO (env
        // Calendar__TimeZone), no la del reloj del contenedor (que suele estar en UTC y
        // adelanta el día tras las 19:00 local, haciendo que el modelo calcule mal el día
        // de la semana). BusinessClock aplica el huso del negocio y "disfraza de UTC".
        var now = BusinessClock.Now;
        string hoy = now.ToString("dddd, dd 'de' MMMM 'de' yyyy", cult).ToLowerInvariant();
        string fechaReferencia = $@"

FECHA ACTUAL (OBLIGATORIO USARLA COMO REFERENCIA):
- Hoy es {hoy}.
- Cuando el cliente diga 'hoy', 'manana', un dia de la semana (p/ej. 'viernes') o una hora sin fecha completa, calcula la fecha REAL futura partiendo de hoy. Usa SIEMPRE el ano actual ({now.Year}) o el siguiente si la fecha ya paso.
- JAMAS uses anos pasados (como 2024) ni inventes fechas. Si no puedes resolver el dia correctamente, pregunta al cliente en lugar de asumir.
";
        return @"Eres un asistente virtual de agendamiento de citas para un negocio local. Tu funcion es ayudar a los clientes a agendar, consultar, reprogramar o cancelar citas a traves de WhatsApp.

REGLAS IMPORTANTES:
1. Se amable y profesional — Saluda al cliente, presentate como el asistente virtual del negocio.
2. Siempre verifica disponibilidad ANTES de agendar — usa check_availability primero.
3. Asegurate de conocer servicio, fecha y horario con el cliente antes de crear una cita — nunca asumas datos que no te hayan dicho. 'Confirmar datos' NO significa esperar la confirmacion definitiva del cliente para crear: la creacion la haces al acordar los datos (ver regla 8.6), y la confirmacion definitiva del cliente llega DESPUES con CONFIRMAR.
3.5 REGLA DE CANCELACIÓN: cuando el cliente pida CANCELAR una cita, cancélala y termina el turno ahí. NO vuelvas a agendar ni reagendes la misma cita a menos que el cliente lo pida explícitamente en un mensaje NUEVO. Cancelar y reagendar en el mismo turno es un error grave.
4. Formatea fechas y horarios de forma clara y amigable (ej: 'jueves 15 de agosto a las 10:00 hs').
5. Si el cliente no especifica un tipo de servicio, preguntale cual desea o sugiere los disponibles.
6. Idioma: responde espanol siempre, en el mismo tono del cliente.
7. Mantene las respuestas concisas — son mensajes de WhatsApp, no correos electronicos.
8. Si hay un error, disculpate y ofrece alternativas.
8.5 RESPONDIENDO AL RECORDATORIO: si el cliente responde CONFIRMAR, usa confirm_appointment (identifica la cita con su WhatsApp y confirma la proxima). Si dice REAGENDAR (o ""cambiar fecha""), preguntale la nueva fecha/hora, usa check_availability y luego reschedule_appointment. Si dice CANCELAR, usa cancel_appointment. Tras CONFIRMAR o CANCELAR termina el turno. Tras REAGENDAR confirma la nueva fecha; y si la cita estaba confirmada, el sistema la deja nuevamente PENDIENTE, asi que pidele al cliente responder CONFIRMAR para re-confirmarla.
8.6 CONFIRMACION DE CITA: CREA LA CITA ANTES DE PEDIR LA CONFIRMACION DEFINITIVA. Una vez que el cliente acuerde servicio, fecha y horario, llama ANTES create_appointment para materializar la reserva (queda PENDIENTE en la base). NADA de preguntar ''¿confirmas?'' si todavia no creaste la cita: la creacion SIEMPRE precede a la pregunta. Luego informale que quedo reservada como provisional y pedile que responda CONFIRMAR para confirmarla definitivamente. Cuando el cliente responda 'si', 'confirmo', 'confirmar' o acepte, confirma la cita (o el sistema la confirma de forma automatica al responder CONFIRMAR). Tras confirmar, termina el turno y no repitas la pregunta.
8.8 LISTA DE ESPERA: si el cliente quiere un servicio pero NO hay disponibilidad (check_availability no devuelve cupos, o la fecha/hora que quiere esta ocupada o fuera del rango permitido), ofrecele agregarse a la lista de espera con add_to_waitlist (usa el nombre exacto del servicio que pidio). Si acepta, confirma el servicio y, si quiere, el rango de fechas (fecha_desde/fecha_hasta) y el profesional preferido (professional_name) — todos opcionales. Informale que se le avisara por WhatsApp cuando se libere un cupo y termina el turno. Si dice que solo queria probar fechas, no lo agregues.
8.8a CUANDO NO HAY CUPOS, NO SONDEES EN LOOP: si check_availability devuelve 0 cupos para lo que pidio el cliente, NO vuelvas a llamar check_availability con otras fechas u otros servicios en busca de un hueco. Eso apaga el turno. En su lugar, con esa primera respuesta ya informale al cliente que no hay disponibilidad para lo pedido, pregunta si quiere otra FECha/horario o la LISTA DE ESPERA (regla 8.8), y termina el turno. Usa maximo UNA llamada de check_availability salvo que el cliente cambie explicitamente la fecha o el servicio que quiere.

CLIENTE ACTUAL:
" + senderIdentity + @"
" + fechaReferencia + @"

HORARIOS EN HORA LOCAL (IMPORTANTE):
- Las herramientas check_availability, create_appointment, reschedule_appointment y list_appointments trabajan SIEMPRE en hora LOCAL del negocio.
- Los horarios que devuelve check_availability ya están en hora local: usa el MISMO valor (sin sumar ni restar horas) al llamar create_appointment o reschedule_appointment.
- NUNCA conviertas entre UTC y hora local. Pasa a create_appointment la misma hora local que acordaste con el cliente.
- Cuando menciones el dia de la semana, verificalo contra la FECHA ACTUAL de arriba y contra el calendario real: por ejemplo, si hoy es 'martes 25', manana es 'miercoles 26'. Si tienes duda, escribe solo la fecha en formato dd/mm/aaaa.

REGLAS CRITICAS SOBRE LOS DATOS DEL CLIENTE:
- El WhatsApp del cliente SIEMPRE es el numero real " + userPhone + @". NUNCA lo inventes ni uses numeros de ejemplo (como 1234567890).
- Cuando la herramienta create_appointment pida client_whatsapp, usa SIEMPRE " + userPhone + @".
- Usa el nombre del cliente solo si lo confirmo en la conversacion; si no lo sabes, no lo inventes.
" + datosNegocio + serviciosDisponibles + profesionalesDisponibles + @"

HERRAMIENTAS DISPONIBLES:
- check_availability: Consultar horarios disponibles
- create_appointment: Agendar una cita
- cancel_appointment: Cancelar una cita existente
- confirm_appointment: Confirmar una cita (cliente respondio CONFIRMAR)
- reschedule_appointment: Reprogramar una cita
- list_appointments: Listar las citas del cliente
- add_to_waitlist: Agregar al cliente a la lista de espera de un servicio (cuando no haya disponibilidad y el cliente lo acepte)" + (clientContext ?? "") + reservaEnCurso;
    }

    private static string FormatServiceLine(Domain.Entities.ServiceType s, System.Globalization.CultureInfo cult)
    {
        var detalle = new List<string>();
        if (s.DuracionMinutos > 0) detalle.Add($"{s.DuracionMinutos} min");
        if (s.Precio.HasValue) detalle.Add($"${s.Precio.Value.ToString("N0", cult)}");
        return detalle.Count > 0 ? $"- {s.Nombre} ({string.Join(", ", detalle)})" : $"- {s.Nombre}";
    }

    // Tool Handlers

    private async Task<string> CheckAvailabilityAsync(
        JsonElement args,
        IServiceProvider services,
        Guid tenantId,
        CancellationToken ct)
    {
        var useCase = services.GetRequiredService<Application.UseCases.CheckAvailabilityUseCase>();

        var fechaInicio = DateOnly.FromDateTime(
            DateTime.Parse(args.GetProperty("fecha_inicio").GetString()!));
        var fechaFin = DateOnly.FromDateTime(
            DateTime.Parse(args.GetProperty("fecha_fin").GetString()!));

        var dto = new Application.DTOs.AvailabilityQueryDto
        {
            TenantId = tenantId,
            FechaInicio = fechaInicio,
            FechaFin = fechaFin,
            ServiceTypeName = args.TryGetProperty("service_type_name", out var st) ? st.GetString() : null,
            ProfessionalName = args.TryGetProperty("professional_name", out var prof) ? prof.GetString() : null
        };

        var slots = await useCase.ExecuteAsync(dto, ct);

        var result = new
        {
            success = true,
            // Los horarios de este resultado están en HORA LOCAL del negocio (la app agenda
            // y guarda siempre en hora local). El modelo debe pasarlos a create_appointment
            // TAL CUAL, sin convertirlos a UTC (convertir añadía +5 h por interpretarlos como UTC).
            timezone = "hora local del negocio (" +
                (Environment.GetEnvironmentVariable("Calendar__TimeZone") ?? "America/Bogota") +
                ") — usa estas horas tal cual, NO las conviertas a UTC",
            slots = slots.Select(s => new
            {
                start = s.Start.ToString("yyyy-MM-ddTHH:mm:ss"),
                end = s.End.ToString("yyyy-MM-ddTHH:mm:ss"),
                serviceType = s.ServiceTypeName
            }),
            total_slots = slots.Count
        };

        return JsonSerializer.Serialize(result);
    }

    private async Task<string> CreateAppointmentAsync(
        JsonElement args,
        IServiceProvider services,
        Guid tenantId,
        string userPhone,
        string? clientName,
        CancellationToken ct)
    {
        var useCase = services.GetRequiredService<Application.UseCases.CreateAppointmentUseCase>();

        // El cliente que está escribiendo ES el dueño de la cita: su WhatsApp es el del remitente
        // real, sin excepciones (los LLM tienden a inventar números como 1234567890, lo que rompe
        // la entrega). El nombre sólo se usa si viene de la conversación confirmada.
        string? modelName = args.TryGetProperty("client_name", out var n) ? n.GetString() : null;
        var resolvedName = !string.IsNullOrWhiteSpace(modelName)
            ? modelName
            : (!string.IsNullOrWhiteSpace(clientName) ? clientName : userPhone);

        var dto = new Application.DTOs.AppointmentCreateDto
        {
            TenantId = tenantId,
            ClientWhatsApp = userPhone,
            ClientName = resolvedName!,
            ServiceTypeName = args.GetProperty("service_type_name").GetString()!,
            ProfessionalName = args.TryGetProperty("professional_name", out var prof) ? prof.GetString() : null,
            FechaInicio = ParseLocalDateTime(args.GetProperty("fecha_inicio").GetString()!),
            Notas = args.TryGetProperty("notas", out var notas) ? notas.GetString() : null
        };

        var response = await useCase.ExecuteAsync(dto, ct);

        if (response == null)
        {
            return JsonSerializer.Serialize(new { success = false, error = "No se pudo agendar la cita." });
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            appointment = new
            {
                id = response.Id,
                serviceType = response.ServiceTypeName,
                professional = response.ProfessionalName,
                start = response.FechaInicio.ToString("yyyy-MM-ddTHH:mm:ss"),
                end = response.FechaFin.ToString("yyyy-MM-ddTHH:mm:ss"),
                status = response.Status,
                clientName = response.ClientName
            }
        });
    }

    private async Task<string> CancelAppointmentAsync(
        JsonElement args,
        IServiceProvider services,
        Guid tenantId,
        CancellationToken ct)
    {
        var useCase = services.GetRequiredService<Application.UseCases.CancelAppointmentUseCase>();
        var identifier = args.GetProperty("appointment_identifier").GetString()!;
        var motivo = args.TryGetProperty("motivo", out var m) ? m.GetString() : null;

        var dto = new Application.DTOs.AppointmentCancelDto
        {
            AppointmentIdentifier = identifier,
            TenantId = tenantId,
            Motivo = motivo
        };

        var response = await useCase.ExecuteAsync(dto, ct);

        return JsonSerializer.Serialize(new
        {
            success = response != null,
            appointment = response == null ? null : new
            {
                id = response.Id,
                status = response.Status
            }
        });
    }

    private async Task<string> ConfirmAppointmentAsync(
        JsonElement args,
        IServiceProvider services,
        Guid tenantId,
        CancellationToken ct)
    {
        var useCase = services.GetRequiredService<Application.UseCases.ConfirmAppointmentUseCase>();
        var identifier = args.GetProperty("appointment_identifier").GetString()!;

        var dto = new Application.DTOs.AppointmentCancelDto
        {
            AppointmentIdentifier = identifier,
            TenantId = tenantId
        };

        var response = await useCase.ExecuteAsync(dto, ct);

        return JsonSerializer.Serialize(new
        {
            success = response != null,
            appointment = response == null ? null : new
            {
                id = response.Id,
                status = response.Status
            }
        });
    }

    /// <summary>
    /// P1 Lista de espera: agrega al cliente (el remitente) a la lista de espera de un servicio.
    /// Resuelve servicio/profesional por nombre, crea el cliente si no existe, y hace dedup
    /// (una sola entrada activa por cliente+servicio). Rechaza si el cliente ya tiene una cita
    /// pendiente/confirmada futura del mismo servicio (no tiene sentido esperar si ya está agendado).
    /// </summary>
    private async Task<string> AddToWaitlistAsync(
        JsonElement args,
        IServiceProvider services,
        Guid tenantId,
        string userPhone,
        string? clientName,
        CancellationToken ct)
    {
        var serviceTypeRepo = services.GetRequiredService<IServiceTypeRepository>();
        var professionalRepo = services.GetRequiredService<IProfessionalRepository>();
        var clientRepo = services.GetRequiredService<IClientRepository>();
        var appointmentRepo = services.GetRequiredService<IAppointmentRepository>();
        var waitlistRepo = services.GetRequiredService<IWaitlistEntryRepository>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();

        var serviceName = args.GetProperty("service_type_name").GetString();
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return JsonSerializer.Serialize(new { success = false, error = "Falta el nombre del servicio." });
        }

        // Resolver servicio (por nombre exacto o parcial, misma semántica que el flujo de create).
        var servicesLst = await serviceTypeRepo.GetByTenantIdAsync(tenantId, ct);
        var service = servicesLst.FirstOrDefault(s => s.Activo && s.Nombre.Contains(serviceName, StringComparison.OrdinalIgnoreCase));
        if (service == null)
        {
            return JsonSerializer.Serialize(new { success = false, error = "El servicio solicitado no está disponible." });
        }

        // Resolver profesional opcional.
        Domain.Entities.Professional? professional = null;
        var profName = args.TryGetProperty("professional_name", out var profArg) ? profArg.GetString() : null;
        if (!string.IsNullOrWhiteSpace(profName))
        {
            professional = await professionalRepo.GetActiveByTenantAndNameAsync(tenantId, profName, ct);
            if (professional == null)
            {
                return JsonSerializer.Serialize(new { success = false, error = "El profesional solicitado no existe." });
            }
        }

        // Preferencia de ventana de fechas (opcional).
        DateTime? desde = null, hasta = null;
        if (args.TryGetProperty("fecha_desde", out var d) && d.GetString() is { Length: > 0 } ds && DateTime.TryParse(ds, out var dp))
            desde = dp;
        if (args.TryGetProperty("fecha_hasta", out var h) && h.GetString() is { Length: > 0 } hs && DateTime.TryParse(hs, out var hp))
            hasta = hp;
        if (desde.HasValue && hasta.HasValue && hasta < desde)
            (desde, hasta) = (hasta, desde);

        // Cliente: el remitente real. Se crea si es primer contacto (misma convención que el CRM).
        var client = await clientRepo.GetByWhatsAppAsync(userPhone, tenantId, ct);
        if (client == null)
        {
            client = new Domain.Entities.Client
            {
                IdTenant = tenantId,
                // La identidad canónica puede ser un BSUID (contiene '.', ej "US.123…") o un
                // teléfono legacy. Se almacena en la columna correcta para no guardar BSUID como
                // número. Mismo criterio que ClientContextService.ResolveOrCreateAsync.
                UserId = userPhone.Contains('.') ? userPhone : null,
                WhatsApp = userPhone.Contains('.') ? "" : userPhone,
                Nombre = !string.IsNullOrWhiteSpace(clientName) ? clientName : null,
                Estado = "nuevo"
            };
            client = await clientRepo.CreateAsync(client, ct);
        }

        // Dedup: no duplicar la entrada activa del mismo cliente+servicio.
        var existing = await waitlistRepo.GetActiveByClientAndServiceAsync(tenantId, client.IdClient, service.IdServiceType, ct);
        if (existing != null)
        {
            return JsonSerializer.Serialize(new { success = true, already_waitlisted = true, message = "El cliente ya está en la lista de espera de este servicio." });
        }

        // Rechazar si el cliente ya tiene cita pendiente/confirmada futura del mismo servicio.
        var clientAppointments = await appointmentRepo.GetByClientIdAsync(client.IdClient, ct);
        var businessNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById(Environment.GetEnvironmentVariable("Calendar__TimeZone") ?? "America/Bogota"));
        var now = DateTime.SpecifyKind(businessNow, DateTimeKind.Utc);
        var hasUpcoming = clientAppointments.Any(a => a.IdServiceType == service.IdServiceType
                                                      && a.FechaInicio >= now
                                                      && (a.Estado == "pending" || a.Estado == "confirmed"));
        if (hasUpcoming)
        {
            return JsonSerializer.Serialize(new { success = false, already_booked = true, message = "El cliente ya tiene una cita para este servicio." });
        }

        var entry = new Domain.Entities.WaitlistEntry
        {
            IdTenant = tenantId,
            IdClient = client.IdClient,
            IdServiceType = service.IdServiceType,
            IdProfessional = professional?.IdProfessional,
            FechaDesde = desde,
            FechaHasta = hasta
        };
        await waitlistRepo.CreateAsync(entry, ct);
        await unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation("[Waitlist] {Phone} agregado a lista de espera del servicio {Service}",
            userPhone, service.Nombre);

        return JsonSerializer.Serialize(new
        {
            success = true,
            message = "El cliente quedó en la lista de espera. Se le avisará por WhatsApp cuando se libere un cupo.",
            waitlist_id = entry.IdWaitlistEntry
        });
    }

    private async Task<string> RescheduleAppointmentAsync(
        JsonElement args,
        IServiceProvider services,
        Guid tenantId,
        CancellationToken ct)
    {
        var useCase = services.GetRequiredService<Application.UseCases.RescheduleAppointmentUseCase>();

        // El modelo puede pasar el ID real de la cita o, más a menudo, el WhatsApp del
        // cliente (tiende a inventar IDs). Si lo pasado no es un GUID válido, se resuelve
        // por WhatsApp en el caso de uso.
        var idArg = args.GetProperty("appointment_id").GetString()!;
        var parsedId = Guid.TryParse(idArg, out var realId) ? realId : Guid.Empty;

        var dto = new Application.DTOs.AppointmentRescheduleDto
        {
            AppointmentId = parsedId,
            AppointmentIdentifier = parsedId == Guid.Empty ? idArg : null,
            TenantId = tenantId,
            NuevaFechaInicio = ParseLocalDateTime(args.GetProperty("nueva_fecha_inicio").GetString()!)
        };

        var response = await useCase.ExecuteAsync(dto, ct);

        return JsonSerializer.Serialize(new
        {
            success = response != null,
            appointment = response == null ? null : new
            {
                id = response.Id,
                newStart = response.FechaInicio.ToString("yyyy-MM-ddTHH:mm:ss"),
                status = response.Status
            }
        });
    }

    private async Task<string> ListAppointmentsAsync(
        JsonElement args,
        IServiceProvider services,
        Guid tenantId,
        CancellationToken ct)
    {
        var useCase = services.GetRequiredService<Application.UseCases.ListAppointmentsUseCase>();
        var whatsapp = args.GetProperty("client_whatsapp").GetString()!;
        var estado = args.TryGetProperty("estado", out var e) ? e.GetString() : "upcoming";

        var appointments = await useCase.ExecuteAsync(whatsapp, tenantId, estado, ct);

        return JsonSerializer.Serialize(new
        {
            success = true,
            total = appointments.Count,
            appointments = appointments.Select(a => new
            {
                id = a.Id,
                serviceType = a.ServiceTypeName,
                start = a.FechaInicio.ToString("yyyy-MM-ddTHH:mm:ss"),
                end = a.FechaFin.ToString("yyyy-MM-ddTHH:mm:ss"),
                status = a.Status,
                clientName = a.ClientName
            })
        });
    }

    /// <summary>
    /// Rastrea el agendamiento en curso (P3): guarda servicio/profesional/fecha cuando el
    /// cliente busca disponibilidad y limpia el estado cuando la reserva se concreta o cancela.
    /// </summary>
    private void TrackBookingProgress(string conversationKey, ToolCall toolCall, string toolResultJson)
    {
        try
        {
            switch (toolCall.Name)
            {
                case "create_appointment":
                    if (IsToolSuccess(toolResultJson))
                        _conversationState.SetPendingBooking(conversationKey, null);
                    break;
                case "cancel_appointment":
                    _conversationState.SetPendingBooking(conversationKey, null);
                    break;
                case "check_availability":
                    GuardPendingBookingFromAvailability(conversationKey, toolCall.Arguments);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Orchestrator] No se pudo rastrear la reserva en curso ({Tool})", toolCall.Name);
        }
    }

    private static bool IsToolSuccess(string toolResultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolResultJson);
            return doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    private void GuardPendingBookingFromAvailability(string conversationKey, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return;

        using var doc = JsonDocument.Parse(argumentsJson);
        var args = doc.RootElement;

        string? servicio = args.TryGetProperty("service_type_name", out var svc) ? svc.GetString() : null;
        string? profesional = args.TryGetProperty("professional_name", out var prof) ? prof.GetString() : null;
        DateOnly? fecha = null;
        if (args.TryGetProperty("fecha_inicio", out var f)
            && !string.IsNullOrWhiteSpace(f.GetString())
            && DateOnly.TryParse(f.GetString(), out var parsedFecha))
            fecha = parsedFecha;

        if (string.IsNullOrWhiteSpace(servicio) && string.IsNullOrWhiteSpace(profesional) && !fecha.HasValue)
            return;

        _conversationState.SetPendingBooking(conversationKey, new PendingBooking(servicio, profesional, fecha));
    }

    private static string FormatPendingBooking(PendingBooking? pending)
    {
        if (pending == null
            || (string.IsNullOrWhiteSpace(pending.ServiceTypeName)
                && string.IsNullOrWhiteSpace(pending.ProfessionalName)
                && !pending.Fecha.HasValue))
            return "";

        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(pending.ServiceTypeName))
            partes.Add($"servicio {pending.ServiceTypeName}");
        if (!string.IsNullOrWhiteSpace(pending.ProfessionalName))
            partes.Add($"con {pending.ProfessionalName}");
        if (pending.Fecha.HasValue)
            partes.Add($"para el {pending.Fecha.Value:dd/MM/yyyy}");

        return @"

RESERVA EN CURSO: el cliente estaba armando una cita (" + string.Join(" ", partes) + @") y no la completó. Si el cliente retoma el tema de agendar, reconocé lo que tenía en marcha y retomá pidiendo solo lo que falta (idealmente el horario). NO des la cita por hecha: usá check_availability y luego create_appointment.";
    }

    /// <summary>
    /// Persiste la causa del turno fallido (timeout o todos los proveedores de IA fallaron) para
    /// poder diagnosticarla en producción vía GET api/v1/dashboard/failures sin depender de los
    /// logs del contenedor. Falla silencioso: registrar la causa NUNCA debe alterar ni bloquear
    /// la respuesta al cliente.
    /// </summary>
    private async Task PersistTurnFailureAsync(
        IServiceProvider services,
        Guid tenantId,
        string userId,
        string motivo,
        string detalle)
    {
        try
        {
            // Token PROPIO (no el del turno): cuando se llega acá por timeout el token del turno
            // ya está cancelado y todo SaveChanges con él revienta. Techo de 5 s para no colgar
            // el turno si la BD está lenta.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var turnFailureRepo = services.GetRequiredService<ITurnFailureRepository>();
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();
            await turnFailureRepo.AddAsync(new Domain.Entities.TurnFailure
            {
                IdTurnFailure = Guid.NewGuid(),
                IdTenant = tenantId,
                PhoneCliente = userId ?? "",
                Motivo = motivo,
                Detalle = detalle.Length > 2000 ? detalle[..2000] : detalle
            }, timeoutCts.Token);
            await unitOfWork.SaveChangesAsync(timeoutCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Orchestrator] No se pudo persistir el registro de fallo de turno ({Motivo})", motivo);
        }
    }

    /// <summary>
    /// Persiste un turno de la conversación en el historial durable (CRM). Falla silencioso:
    /// un problema al guardar el historial nunca debe romper el turno del cliente.
    /// </summary>
    private async Task PersistMessageAsync(
        IConversationHistoryRepository history,
        IUnitOfWork unitOfWork,
        Guid tenantId,
        string userPhone,
        string role,
        string content,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            if (content.Length > 4000)
                content = content[..4000];

            await history.AddAsync(new Domain.Entities.ConversationMessage
            {
                IdConversationMessage = Guid.NewGuid(),
                IdTenant = tenantId,
                PhoneCliente = userPhone,
                Role = role,
                Content = content
            }, ct);
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Falla silencioso para no romper el turno del cliente, pero se registra en el
            // log real (no Debug) para que un fallo de persistencia sea visible al diagnosticar.
            _logger.LogWarning(ex, "[CRM] No se pudo persistir el turno de conversación de {Phone}", userPhone);
        }
    }
}
