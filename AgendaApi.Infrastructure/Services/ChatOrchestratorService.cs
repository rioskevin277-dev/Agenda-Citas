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
    private readonly ILogger<ChatOrchestratorService> _logger;

    private const int MaxToolIterations = 5;

    public ChatOrchestratorService(
        IServiceScopeFactory scopeFactory,
        ILogger<ChatOrchestratorService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ProcessMessageAsync(
        string userPhone,
        string messageContent,
        Guid tenantId,
        CancellationToken ct = default)
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

        _logger.LogInformation("[Orchestrator] Procesando mensaje de {Phone} para tenant {Tenant}",
            userPhone, tenantId);

        var systemPrompt = GetSystemPrompt();

        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = systemPrompt },
            new() { Role = "user", Content = messageContent }
        };

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
                await messaging.SendTextAsync(userPhone, "Lo siento, tuve un problema. Por favor intenta mas tarde.");
                return;
            }
            _logger.LogInformation("[Orchestrator] Respondiendo con {Provider}", usedProvider);

            if (!string.IsNullOrWhiteSpace(result.TextContent))
            {
                messages.Add(new ChatMessage { Role = "assistant", Content = result.TextContent });
            }

            if (result.FinishReason != "tool_calls" || result.ToolCalls == null || result.ToolCalls.Count == 0)
            {
                _logger.LogInformation("[Orchestrator] Respuesta final del modelo, sin tool calls");

                var responseText = result.TextContent ?? "En que mas puedo ayudarte?";
                await messaging.SendTextAsync(userPhone, responseText);
                return;
            }

            foreach (var toolCall in result.ToolCalls)
            {
                _logger.LogInformation("[Orchestrator] Ejecutando tool: {Name}({Args})",
                    toolCall.Name, toolCall.Arguments);

                var toolResult = await ExecuteToolAsync(toolCall.Name, toolCall.Arguments, services, tenantId, ct);

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
        await messaging.SendTextAsync(userPhone,
            "Estoy procesando tu solicitud. Un asesor te contactara pronto para confirmar.");
    }

    private async Task<string> ExecuteToolAsync(
        string toolName,
        string argumentsJson,
        IServiceProvider services,
        Guid tenantId,
        CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var args = doc.RootElement;

            return toolName switch
            {
                "check_availability" => await CheckAvailabilityAsync(args, services, tenantId, ct),
                "create_appointment" => await CreateAppointmentAsync(args, services, tenantId, ct),
                "cancel_appointment" => await CancelAppointmentAsync(args, services, tenantId, ct),
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

    private static string GetSystemPrompt()
    {
        return @"Eres un asistente virtual de agendamiento de citas para un negocio local. Tu funcion es ayudar a los clientes a agendar, consultar, reprogramar o cancelar citas a traves de WhatsApp.

REGLAS IMPORTANTES:
1. Se amable y profesional — Saluda al cliente, presentate como el asistente virtual del negocio.
2. Siempre verifica disponibilidad ANTES de agendar — usa check_availability primero.
3. Confirma los datos con el cliente antes de crear una cita — nunca asumas.
4. Formatea fechas y horarios de forma clara y amigable (ej: 'jueves 15 de agosto a las 10:00 hs').
5. Si el cliente no especifica un tipo de servicio, preguntale cual desea.
6. Idioma: responde SIEMPRE en espanol, en el mismo tono del cliente.
7. Mantene las respuestas concisas — son mensajes de WhatsApp, no correos electronicos.
8. Si hay un error, disculpate y ofrece alternativas.

HERRAMIENTAS DISPONIBLES:
- check_availability: Consultar horarios disponibles
- create_appointment: Agendar una cita
- cancel_appointment: Cancelar una cita existente
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
        CancellationToken ct)
    {
        var useCase = services.GetRequiredService<Application.UseCases.CreateAppointmentUseCase>();
        var dto = new Application.DTOs.AppointmentCreateDto
        {
            TenantId = tenantId,
            ClientWhatsApp = args.GetProperty("client_whatsapp").GetString()!,
            ClientName = args.GetProperty("client_name").GetString()!,
            ServiceTypeName = args.GetProperty("service_type_name").GetString()!,
            FechaInicio = DateTime.Parse(args.GetProperty("fecha_inicio").GetString()!),
            Notas = args.TryGetProperty("notas", out var n) ? n.GetString() : null
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

    private async Task<string> RescheduleAppointmentAsync(
        JsonElement args,
        IServiceProvider services,
        Guid tenantId,
        CancellationToken ct)
    {
        var useCase = services.GetRequiredService<Application.UseCases.RescheduleAppointmentUseCase>();
        var dto = new Application.DTOs.AppointmentRescheduleDto
        {
            AppointmentId = Guid.Parse(args.GetProperty("appointment_id").GetString()!),
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
