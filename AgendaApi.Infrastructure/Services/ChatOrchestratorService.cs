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
    private readonly ILogger<ChatOrchestratorService> _logger;

    private const int MaxToolIterations = 5;

    public ChatOrchestratorService(
        IServiceScopeFactory scopeFactory,
        ConversationMemoryService conversationMemory,
        ILogger<ChatOrchestratorService> logger)
    {
        _scopeFactory = scopeFactory;
        _conversationMemory = conversationMemory;
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
        // Groq (gratuito) primero, luego OpenAI y Anthropic como fallback.
        var aiProviders = new (string Name, IAiProvider Provider, List<object> Tools)[]
        {
            ("Groq", services.GetRequiredService<GroqProvider>(), AppointmentToolDefinitions.GetOpenAiToolDefinitions()),
            ("OpenAI", services.GetRequiredService<OpenAIProvider>(), AppointmentToolDefinitions.GetOpenAiToolDefinitions()),
            ("Anthropic", services.GetRequiredService<AnthropicProvider>(), AppointmentToolDefinitions.GetAnthropicToolDefinitions())
        };
        var messaging = services.GetRequiredService<IMessagingProvider>();
        var tenantContext = services.GetRequiredService<ITenantContext>();

        // Cargar datos reales del tenant desde la BD
        var tenantRepo = services.GetRequiredService<ITenantRepository>();
        var tenant = await tenantRepo.GetByIdAsync(tenantId, ct);

        tenantContext.SetTenant(
            tenantId,
            calendarProvider: tenant?.CalendarProvider ?? "google",
            whatsAppAccessToken: Environment.GetEnvironmentVariable("WhatsApp__AccessToken") ?? Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN") ?? "",
            phoneNumberId: tenant?.WhatsAppPhoneNumberId ?? "");

        // Cargar los tipos de servicio reales del tenant para que la IA solo pueda
        // sugerir/agendar servicios que de verdad existen (evita nombres inventados).
        var serviceTypeRepo = services.GetRequiredService<IServiceTypeRepository>();
        var serviceTypes = await serviceTypeRepo.GetByTenantIdAsync(tenantId, ct);

        _logger.LogInformation("[Orchestrator] Procesando mensaje de {Phone} para tenant {Tenant}",
            userPhone, tenantId);

        var systemPrompt = GetSystemPrompt(userPhone, clientName, serviceTypes);

        // Cargar historial previo de la conversación (si existe) para conservar contexto.
        var conversationKey = ConversationMemoryService.GetKey(tenantId, userPhone);
        var messages = _conversationMemory.GetHistory(conversationKey, systemPrompt);
        messages.Add(new ChatMessage { Role = "user", Content = messageContent });
        _conversationMemory.AddUser(conversationKey, messageContent);

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
                await messaging.SendTextAsync(userPhone, errorText);
                _conversationMemory.AddAssistant(conversationKey, errorText);
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
                return;
            }

            foreach (var toolCall in result.ToolCalls)
            {
                _logger.LogInformation("[Orchestrator] Ejecutando tool: {Name}({Args})",
                    toolCall.Name, toolCall.Arguments);

                var toolResult = await ExecuteToolAsync(toolCall.Name, toolCall.Arguments, services, tenantId, userPhone, clientName, ct);

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
        const string maxIterText = "Estoy procesando tu solicitud. Un asesor te contactara pronto para confirmar.";
        await messaging.SendTextAsync(userPhone, maxIterText);
        _conversationMemory.AddAssistant(conversationKey, maxIterText);
    }

    private async Task<string> ExecuteToolAsync(
        string toolName,
        string argumentsJson,
        IServiceProvider services,
        Guid tenantId,
        string userPhone,
        string? clientName,
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

    private static string GetSystemPrompt(string userPhone, string? clientName, List<Domain.Entities.ServiceType>? serviceTypes = null)
    {
        string senderIdentity = string.IsNullOrWhiteSpace(clientName)
            ? $"El cliente que te escribe tiene el WhatsApp {userPhone}."
            : $"El cliente que te escribe se llama {clientName} y su WhatsApp es {userPhone}.";

        // Lista real de servicios disponibles: la IA SOLO debe sugerir/agendar estos.
        // Si no viene, se omite el bloque (por compatibilidad con pruebas).
        string serviciosDisponibles = serviceTypes is { Count: > 0 }
            ? @"

SERVICIOS DISPONIBLES (lista exacta del negocio — NO inventes ni modifiques estos nombres, NO agregues servicios que no esten aqui):
" + string.Join("\n", serviceTypes.Where(s => s.Activo).Select(s => $"- {s.Nombre}")) + @"
- Cuando el cliente pida un servicio que NO este en esta lista, informale que no esta disponible y sugiere los que si hay. NUNCA intentes agendar un servicio fuera de esta lista."
            : "";

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
8.5 RESPONDIENDO AL RECORDATORIO: si el cliente responde CONFIRMAR, usa confirm_appointment (identifica la cita con su WhatsApp y confirma la proxima). Si dice REAGENDAR (o ""cambiar fecha""), preguntale la nueva fecha/hora, usa check_availability y luego reschedule_appointment. Si dice CANCELAR, usa cancel_appointment. Tras CONFIRMAR o CANCELAR termina el turno; tras REAGENDAR confirma la nueva fecha.

CLIENTE ACTUAL:
" + senderIdentity + @"
" + fechaReferencia + @"

REGLAS CRITICAS SOBRE LOS DATOS DEL CLIENTE:
- El WhatsApp del cliente SIEMPRE es el numero real " + userPhone + @". NUNCA lo inventes ni uses numeros de ejemplo (como 1234567890).
- Cuando la herramienta create_appointment pida client_whatsapp, usa SIEMPRE " + userPhone + @".
- Usa el nombre del cliente solo si lo confirmo en la conversacion; si no lo sabes, no lo inventes.
" + serviciosDisponibles + @"

HERRAMIENTAS DISPONIBLES:
- check_availability: Consultar horarios disponibles
- create_appointment: Agendar una cita
- cancel_appointment: Cancelar una cita existente
- confirm_appointment: Confirmar una cita (cliente respondio CONFIRMAR)
- reschedule_appointment: Reprogramar una cita
- list_appointments: Listar las citas del cliente";
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
            ServiceTypeName = args.TryGetProperty("service_type_name", out var st) ? st.GetString() : null
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
}
