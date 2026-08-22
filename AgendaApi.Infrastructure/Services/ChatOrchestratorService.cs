using System.Text.Json;
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

    private const int MaxToolIterations = 5;

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
        string userPhone,
        string messageContent,
        Guid tenantId,
        CancellationToken ct = default,
        string? clientName = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        // Cadena de proveedores: se prueba en orden hasta que uno responde.
        // Groq (gratuito) primero; su respaldo gratis es OpenRouter (gateway con modelos :free),
        // luego OpenAI y Anthropic como fallback de pago.
        var aiProviders = new (string Name, IAiProvider Provider, List<object> Tools)[]
        {
            ("Groq", services.GetRequiredService<GroqProvider>(), AppointmentToolDefinitions.GetOpenAiToolDefinitions()),
            ("OpenRouter", services.GetRequiredService<OpenRouterProvider>(), AppointmentToolDefinitions.GetOpenAiToolDefinitions()),
            ("OpenAI", services.GetRequiredService<OpenAIProvider>(), AppointmentToolDefinitions.GetOpenAiToolDefinitions()),
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

        // GATE DE HANDOFF: si la conversación está escalada a un humano (pendiente o activa),
        // el AI queda congelado: no responde ni ejecuta herramientas mientras el asesor atiende.
        // El control vuelve al AI cuando el asesor cierra el handoff (FIN → AiResumed).
        var conversationKey = ConversationMemoryService.GetKey(tenantId, userPhone);
        var handoffRepo = services.GetRequiredService<IHandoffRepository>();
        var openHandoff = await handoffRepo.GetOpenByPhoneAsync(tenantId, userPhone, ct);
        if (openHandoff != null)
        {
            var waitingText = openHandoff.Estado == Domain.Entities.HandoffState.HumanActive
                ? "Un asesor te está atendiendo. Esperá un momento, por favor. 🙏"
                : "Recibí tu mensaje. Un asesor humano está revisando tu caso y te va a responder por este chat. 🙏";
            _conversationMemory.AddUser(conversationKey, messageContent);
            await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userPhone, "user", messageContent, ct);
            await messaging.SendTextAsync(userPhone, waitingText, ct);
            _conversationMemory.AddAssistant(conversationKey, waitingText);
            await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userPhone, "assistant", waitingText, ct);
            _logger.LogInformation("[Orchestrator] Mensaje de {Phone} en handoff activo ({State}), AI congelado",
                userPhone, openHandoff.Estado);
            return;
        }

        // FAST-PATH DE CONFIRMACIÓN: cuando el cliente responde un token claramente de
        // confirmación (CONFIRMAR / CONFIRMO / "si, confirmo"...), confirmamos la próxima cita
        // PENDIENTE de forma directa y determinista, sin depender de que el modelo elija la
        // herramienta confirm_appointment. En el tier gratuito el modelo a veces contesta en
        // texto "confirma CONFIRMAR" en vez de ejecutar la herramienta, lo que entraba en un
        // loop: el cliente confirma y le vuelven a pedir que confirme. Este fast-path lo evita.
        if (IsConfirmationIntent(messageContent))
        {
            var confirmed = await TryConfirmUpcomingAsync(services, tenantId, userPhone, ct);
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
                await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userPhone, "user", messageContent, ct);
                await messaging.SendTextAsync(userPhone, confirmText);
                _conversationMemory.AddAssistant(conversationKey, confirmText);
                await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userPhone, "assistant", confirmText, ct);
                _logger.LogInformation("[Orchestrator] Cita {Id} confirmada por fast-path CONFIRMAR para {Phone}",
                    confirmed.IdAppointment, userPhone);
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

        _logger.LogInformation("[Orchestrator] Procesando mensaje de {Phone} para tenant {Tenant}",
            userPhone, tenantId);

        // Estado estructurado de la conversación: reserva en curso.
        var pendingBooking = _conversationState.GetPendingBooking(conversationKey);

        // CRM: memoria operativa del cliente. Compila perfil/estado/historial desde la BD
        // (creando el cliente si es su primer contacto) y se inyecta en el system prompt para
        // que ADAM conozca el contexto del cliente al atenderlo, agendar o hacer seguimiento.
        string clientContext = "";
        try
        {
            var clientContextService = services.GetRequiredService<ClientContextService>();
            clientContext = await clientContextService.BuildClientContextAsync(tenantId, userPhone, ct, clientName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CRM] No se pudo compilar el contexto del cliente {Phone}", userPhone);
        }

        var systemPrompt = GetSystemPrompt(userPhone, clientName, tenant, serviceTypes, professionals, pendingBooking, clientContext);

        // Cargar historial previo de la conversación (si existe) para conservar contexto.
        var messages = _conversationMemory.GetHistory(conversationKey, systemPrompt);
        messages.Add(new ChatMessage { Role = "user", Content = messageContent });
        _conversationMemory.AddUser(conversationKey, messageContent);
        await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userPhone, "user", messageContent, ct);

        // Acciones del AI en este turno en texto legible; se incluyen como contexto
        // estructurado en el aviso al asesor si el turno termina en escalado.
        var accionesPrevias = new List<string>();

        // Rastreo de churn de disponibilidad: si el turno se gasta solo probando
        // check_availability y nunca logra crear la cita (p. ej. el negocio está cerrado
        // el día pedido y el modelo sigue cambiando fecha/servicio), la situación es un
        // caso comercial normal — informar al cliente, NO escalar a humano (que congela la IA).
        bool sawAvailabilityProbe = false;
        bool sawCreateAppointment = false;

        for (int iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            _logger.LogDebug("[Orchestrator] Iteracion {Iter}/{Max}", iteration + 1, MaxToolIterations);

            AiToolCallResult result = new() { Success = false };
            string? usedProvider = null;

            // Probar proveedores en orden hasta obtener una respuesta válida
            foreach (var p in aiProviders)
            {
                try
                {
                    result = await p.Provider.GenerateResponseWithToolsAsync(messages, p.Tools, ct);
                    if (result.Success)
                    {
                        usedProvider = p.Name;
                        break;
                    }
                    _logger.LogWarning("[Orchestrator] {Provider} fallo (Success=false): {Error}",
                        p.Name, result.TextContent);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Orchestrator] {Provider} fallo: {Message}", p.Name, ex.Message);
                }
            }

            if (usedProvider == null)
            {
                _logger.LogError("[Orchestrator] Todos los proveedores fallaron: {Error}", result.TextContent);
                const string errorText = "Lo siento, tuve un problema. Por favor intenta mas tarde.";
                // Registrar el turno del asistente ANTES de escalar/enviar: si la escalada o el
                // envío de WhatsApp lanzan una excepción tras entregar, el histórico igual queda
                // persistido (y aparece en el dashboard en vivo).
                _conversationMemory.AddAssistant(conversationKey, errorText);
                await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userPhone, "assistant", errorText, ct);
                await EscalateToHumanAsync(services, tenantId, userPhone, clientName,
                    "Todos los proveedores de IA fallaron al procesar la solicitud del cliente.", accionesPrevias, ct);
                await messaging.SendTextAsync(userPhone, errorText);
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
                _logger.LogInformation("[Orchestrator] Respuesta final del modelo, sin tool calls");

                var responseText = result.TextContent ?? "En que mas puedo ayudarte?";
                await messaging.SendTextAsync(userPhone, responseText);
                _conversationMemory.AddAssistant(conversationKey, responseText);
                await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userPhone, "assistant", responseText, ct);
                return;
            }

            foreach (var toolCall in result.ToolCalls)
            {
                _logger.LogInformation("[Orchestrator] Ejecutando tool: {Name}({Args})",
                    toolCall.Name, toolCall.Arguments);

                if (toolCall.Name == "check_availability") sawAvailabilityProbe = true;
                if (toolCall.Name == "create_appointment") sawCreateAppointment = true;

                var toolResult = await ExecuteToolAsync(toolCall.Name, toolCall.Arguments, services, tenantId, userPhone, clientName, accionesPrevias, ct);
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
        // respondemos la situación en lugar de escalar a humano (que congelaría la IA).
        if (sawAvailabilityProbe && !sawCreateAppointment)
        {
            const string noSlotsText = "No encontré un horario disponible para la fecha que me pediste. 😕 " +
                "¿Quieres que revise otro día u horario, o prefieres que te agregue a la lista de espera " +
                "del servicio y te aviso cuando se libere un cupo?";
            _logger.LogInformation("[Orchestrator] Sin cupos (churn de disponibilidad): respondo sin escalar a humano");
            await messaging.SendTextAsync(userPhone, noSlotsText);
            _conversationMemory.AddAssistant(conversationKey, noSlotsText);
            await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userPhone, "assistant", noSlotsText, ct);
            return;
        }

        const string maxIterText = "Estoy procesando tu solicitud. Un asesor te contactara pronto para confirmar.";
        await EscalateToHumanAsync(services, tenantId, userPhone, clientName,
            "Se alcanzó el máximo de iteraciones AI sin resolver la solicitud del cliente.", accionesPrevias, ct);
        await messaging.SendTextAsync(userPhone, maxIterText);
        _conversationMemory.AddAssistant(conversationKey, maxIterText);
        await PersistMessageAsync(conversationHistory, unitOfWork, tenantId, userPhone, "assistant", maxIterText, ct);
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
    /// Confirma la próxima cita PENDIENTE futura del cliente (sin pasada). Devuelve la cita
    /// confirmada, o null si no hay ninguna pendiente. Falla silencioso: un error aquí no
    /// debe romper el turno (el AI entonces maneja el mensaje normalmente).
    /// </summary>
    private async Task<Domain.Entities.Appointment?> TryConfirmUpcomingAsync(
        IServiceProvider services,
        Guid tenantId,
        string userPhone,
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

            var upcoming = (await appointmentRepo.GetByClientIdAsync(client.IdClient, ct))
                .Where(a => a.FechaInicio >= now && a.Estado == "pending")
                .OrderBy(a => a.FechaInicio)
                .FirstOrDefault();
            if (upcoming == null)
                return null;

            var dto = new Application.DTOs.AppointmentCancelDto
            {
                TenantId = tenantId,
                AppointmentIdentifier = upcoming.IdAppointment.ToString()
            };
            var result = await useCase.ExecuteAsync(dto, ct);
            return result != null ? upcoming : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Orchestrator] Fast-path de confirmación falló para {Phone}: {Msg}", userPhone, ex.Message);
            return null;
        }
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
                "request_human_attention" => await RequestHumanAttentionAsync(args, services, tenantId, userPhone, clientName, accionesPrevias, ct),
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
        var now = DateTime.Now;
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
3. Confirma los datos con el cliente antes de crear una cita — nunca asumas.
3.5 REGLA DE CANCELACIÓN: cuando el cliente pida CANCELAR una cita, cancélala y termina el turno ahí. NO vuelvas a agendar ni reagendes la misma cita a menos que el cliente lo pida explícitamente en un mensaje NUEVO. Cancelar y reagendar en el mismo turno es un error grave.
4. Formatea fechas y horarios de forma clara y amigable (ej: 'jueves 15 de agosto a las 10:00 hs').
5. Si el cliente no especifica un tipo de servicio, preguntale cual desea o sugiere los disponibles.
6. Idioma: responde espanol siempre, en el mismo tono del cliente.
7. Mantene las respuestas concisas — son mensajes de WhatsApp, no correos electronicos.
8. Si hay un error, disculpate y ofrece alternativas.
8.5 RESPONDIENDO AL RECORDATORIO: si el cliente responde CONFIRMAR, usa confirm_appointment (identifica la cita con su WhatsApp y confirma la proxima). Si dice REAGENDAR (o ""cambiar fecha""), preguntale la nueva fecha/hora, usa check_availability y luego reschedule_appointment. Si dice CANCELAR, usa cancel_appointment. Tras CONFIRMAR o CANCELAR termina el turno. Tras REAGENDAR confirma la nueva fecha; y si la cita estaba confirmada, el sistema la deja nuevamente PENDIENTE, asi que pidele al cliente responder CONFIRMAR para re-confirmarla.
8.6 CONFIRMACION DE CITA: cuando crees una cita, queda PENDIENTE hasta que el cliente la confirme. Informale que debe confirmarla y pedile hacerlo. Cuando el cliente confirme (diga 'si', 'confirmo', 'confirmar' o acepte), usa confirm_appointment con su WhatsApp para confirmarla. Tras confirmar, termina el turno y no repitas la pregunta.
8.7 ATENCION HUMANA: si el cliente pide hablar con una persona o asesor humano, presenta un reclamo, una urgencia, o necesita algo que no se puede resolver con las herramientas, usa request_human_attention con el motivo, informale que un asesor se comunicara pronto con el y termina el turno.
8.8 LISTA DE ESPERA: si el cliente quiere un servicio pero NO hay disponibilidad (check_availability no devuelve cupos, o la fecha/hora que quiere esta ocupada o fuera del rango permitido), ofrecele agregarse a la lista de espera con add_to_waitlist (usa el nombre exacto del servicio que pidio). Si acepta, confirma el servicio y, si quiere, el rango de fechas (fecha_desde/fecha_hasta) y el profesional preferido (professional_name) — todos opcionales. Informale que se le avisara por WhatsApp cuando se libere un cupo y termina el turno. Si dice que solo queria probar fechas, no lo agregues.
8.8a CUANDO NO HAY CUPOS, NO SONDEES EN LOOP: si check_availability devuelve 0 cupos para lo que pidio el cliente, NO vuelvas a llamar check_availability con otras fechas u otros servicios en busca de un hueco. Eso apaga el turno. En su lugar, con esa primera respuesta ya informale al cliente que no hay disponibilidad para lo pedido, pregunta si quiere otra FECha/horario o la LISTA DE ESPERA (regla 8.8), y termina el turno. Usa maximo UNA llamada de check_availability salvo que el cliente cambie explicitamente la fecha o el servicio que quiere.

CLIENTE ACTUAL:
" + senderIdentity + @"
" + fechaReferencia + @"

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
- add_to_waitlist: Agregar al cliente a la lista de espera de un servicio (cuando no haya disponibilidad y el cliente lo acepte)
- request_human_attention: Escalar al cliente a un asesor humano (cuando lo pida o no se pueda resolver)" + (clientContext ?? "") + reservaEnCurso;
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
            slots = slots.Select(s => new
            {
                start = s.Start.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                end = s.End.ToString("yyyy-MM-ddTHH:mm:ssZ"),
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
            FechaInicio = DateTime.Parse(args.GetProperty("fecha_inicio").GetString()!),
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
                start = response.FechaInicio.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                end = response.FechaFin.ToString("yyyy-MM-ddTHH:mm:ssZ"),
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
                WhatsApp = userPhone,
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
            NuevaFechaInicio = DateTime.Parse(args.GetProperty("nueva_fecha_inicio").GetString()!)
        };

        var response = await useCase.ExecuteAsync(dto, ct);

        return JsonSerializer.Serialize(new
        {
            success = response != null,
            appointment = response == null ? null : new
            {
                id = response.Id,
                newStart = response.FechaInicio.ToString("yyyy-MM-ddTHH:mm:ssZ"),
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
                start = a.FechaInicio.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                end = a.FechaFin.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                status = a.Status,
                clientName = a.ClientName
            })
        });
    }

    private async Task<string> RequestHumanAttentionAsync(
        JsonElement args,
        IServiceProvider services,
        Guid tenantId,
        string userPhone,
        string? clientName,
        IReadOnlyList<string> accionesPrevias,
        CancellationToken ct)
    {
        var motivo = args.TryGetProperty("motivo", out var m) ? m.GetString() : null;
        if (string.IsNullOrWhiteSpace(motivo))
            motivo = "El cliente pidió atención humana.";

        await EscalateToHumanAsync(services, tenantId, userPhone, clientName, motivo!, accionesPrevias, ct);

        return JsonSerializer.Serialize(new
        {
            success = true,
            escalado = true,
            mensaje = "El cliente fue escalado a un asesor humano: comunícale que un asesor se comunicará pronto con él y termina el turno."
        });
    }

    /// <summary>
    /// Escala la conversación a un humano. Delega en <see cref="HandoffService"/>: crea el
    /// ticket durable (dedup mientras haya uno abierto) y, si hay un número de asesor
    /// configurado (Notificaciones__WhatsAppDueno), le envía el aviso con el contexto
    /// estructurado del turno. Al crear el ticket, el GATE de ProcessMessageAsync congela
    /// el AI hasta que el asesor cierre el handoff (FIN). Nunca rompe el turno del cliente.
    /// </summary>
    private async Task EscalateToHumanAsync(
        IServiceProvider services,
        Guid tenantId,
        string userPhone,
        string? clientName,
        string motivo,
        IReadOnlyList<string>? accionesPrevias,
        CancellationToken ct)
    {
        try
        {
            var handoffService = services.GetRequiredService<HandoffService>();
            var contexto = accionesPrevias is { Count: > 0 }
                ? string.Join("\n", accionesPrevias)
                : null;
            var handoff = await handoffService.EscalateAsync(tenantId, userPhone, clientName, motivo, contexto, ct);
            if (handoff != null)
                _logger.LogInformation("[Orchestrator] Escalado a humano registrado ({Id}) para {Phone}",
                    handoff.IdHandoff, userPhone);
            else
                _logger.LogDebug("[Orchestrator] Conversación {Tenant}/{Phone} ya escalada, sin repetir", tenantId, userPhone);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Orchestrator] Error notificando escalado a humano para {User}", userPhone);
        }
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
