namespace AgendaApi.Application.Tools;

/// <summary>
/// Definiciones de herramientas para el modelo de IA (OpenAI function-calling format).
/// Reutiliza el mismo patrón de ProductToolDefinitions en AdamApi.
/// Estas herramientas definen qué puede hacer el agente conversacional.
/// </summary>
public static class AppointmentToolDefinitions
{
    /// <summary>
    /// Devuelve las herramientas en formato OpenAI (function-calling).
    /// </summary>
    public static List<object> GetOpenAiToolDefinitions()
    {
        return new List<object>
        {
            CheckAvailabilityTool(),
            CreateAppointmentTool(),
            CancelAppointmentTool(),
            RescheduleAppointmentTool(),
            ListAppointmentsTool()
        };
    }

    /// <summary>
    /// Devuelve las herramientas en formato Anthropic (input_schema).
    /// </summary>
    public static List<object> GetAnthropicToolDefinitions()
    {
        return new List<object>
        {
            CheckAvailabilityToolAnthropic(),
            CreateAppointmentToolAnthropic(),
            CancelAppointmentToolAnthropic(),
            RescheduleAppointmentToolAnthropic(),
            ListAppointmentsToolAnthropic()
        };
    }

    // ─── OpenAI format ───────────────────────────────────────────────

    private static object CheckAvailabilityTool()
    {
        return new
        {
            type = "function",
            function = new
            {
                name = "check_availability",
                description = "Consulta los horarios disponibles del negocio para un rango de fechas. " +
                              "Devuelve slots libres que pueden ser agendados. " +
                              "Usar SIEMPRE antes de agendar una cita para verificar que el horario esté libre.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        fecha_inicio = new
                        {
                            type = "string",
                            description = "Fecha de inicio en formato YYYY-MM-DD (ej: 2026-08-15)"
                        },
                        fecha_fin = new
                        {
                            type = "string",
                            description = "Fecha de fin en formato YYYY-MM-DD (ej: 2026-08-15)"
                        },
                        service_type_name = new
                        {
                            type = "string",
                            description = "Opcional. Nombre del tipo de servicio para filtrar disponibilidad " +
                                          "(ej: Corte de pelo, Consulta, Baño). Si se omite, muestra toda la disponibilidad."
                        }
                    },
                    required = new[] { "fecha_inicio", "fecha_fin" }
                }
            }
        };
    }

    private static object CreateAppointmentTool()
    {
        return new
        {
            type = "function",
            function = new
            {
                name = "create_appointment",
                description = "Agenda una nueva cita para un cliente en el horario especificado. " +
                              "PREVIAMENTE se debe haber llamado a check_availability para verificar disponibilidad. " +
                              "El cliente debe proporcionar: nombre, número de WhatsApp (sin +), " +
                              "tipo de servicio deseado, y fecha/hora preferida. " +
                              "Si el cliente no da un tipo de servicio específico, preguntar.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        client_whatsapp = new
                        {
                            type = "string",
                            description = "Número de WhatsApp del cliente (solo dígitos, ej: 521234567890)"
                        },
                        client_name = new
                        {
                            type = "string",
                            description = "Nombre del cliente"
                        },
                        service_type_name = new
                        {
                            type = "string",
                            description = "Nombre del tipo de servicio a agendar (ej: Corte de pelo, Consulta)"
                        },
                        fecha_inicio = new
                        {
                            type = "string",
                            description = "Fecha y hora de inicio en formato ISO 8601 (ej: 2026-08-15T10:00:00Z)"
                        },
                        notas = new
                        {
                            type = "string",
                            description = "Notas opcionales para la cita (ej: 'prefiere atención con María')"
                        }
                    },
                    required = new[] { "client_whatsapp", "client_name", "service_type_name", "fecha_inicio" }
                }
            }
        };
    }

    private static object CancelAppointmentTool()
    {
        return new
        {
            type = "function",
            function = new
            {
                name = "cancel_appointment",
                description = "Cancela una cita existente. Se necesita el ID de la cita o los datos del cliente " +
                              "para identificar la cita. Preguntar al cliente qué cita desea cancelar si hay varias. " +
                              "Se puede proporcionar un motivo opcional.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        appointment_identifier = new
                        {
                            type = "string",
                            description = "ID de la cita, o datos del cliente para identificar la cita a cancelar " +
                                          "(ej: número de WhatsApp). Si se pasa un número, se cancela la próxima cita del cliente."
                        },
                        motivo = new
                        {
                            type = "string",
                            description = "Motivo de la cancelación (opcional)"
                        }
                    },
                    required = new[] { "appointment_identifier" }
                }
            }
        };
    }

    private static object RescheduleAppointmentTool()
    {
        return new
        {
            type = "function",
            function = new
            {
                name = "reschedule_appointment",
                description = "Reprograma una cita existente a una nueva fecha/hora. " +
                              "Se necesita confirmar con el cliente: qué cita desea mover y a qué nueva fecha/hora. " +
                              "Usar check_availability primero para verificar que el nuevo horario esté libre.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        appointment_id = new
                        {
                            type = "string",
                            description = "ID de la cita a reprogramar"
                        },
                        nueva_fecha_inicio = new
                        {
                            type = "string",
                            description = "Nueva fecha y hora de inicio en formato ISO 8601 (ej: 2026-08-20T14:00:00Z)"
                        }
                    },
                    required = new[] { "appointment_id", "nueva_fecha_inicio" }
                }
            }
        };
    }

    private static object ListAppointmentsTool()
    {
        return new
        {
            type = "function",
            function = new
            {
                name = "list_appointments",
                description = "Lista las citas de un cliente. Puede filtrar por estado. " +
                              "Usar cuando el cliente pregunte 'qué citas tengo', 'muéstrame mis citas', etc. " +
                              "Si no se especifica estado, muestra las próximas citas pendientes/confirmadas.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        client_whatsapp = new
                        {
                            type = "string",
                            description = "Número de WhatsApp del cliente"
                        },
                        estado = new
                        {
                            type = "string",
                            description = "Filtrar por estado: pending, confirmed, cancelled, completed, " +
                                          "o 'upcoming' para las próximas (pendientes + confirmadas). Por defecto: upcoming",
                            enumValues = new[] { "pending", "confirmed", "cancelled", "completed", "upcoming" }
                        }
                    },
                    required = new[] { "client_whatsapp" }
                }
            }
        };
    }

    // ─── Anthropic format ──────────────────────────────────────────

    private static object CheckAvailabilityToolAnthropic()
    {
        return new
        {
            name = "check_availability",
            description = "Consulta los horarios disponibles del negocio para un rango de fechas. " +
                          "Usar SIEMPRE antes de agendar una cita para verificar disponibilidad.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    fecha_inicio = new { type = "string", description = "Fecha inicio (YYYY-MM-DD)" },
                    fecha_fin = new { type = "string", description = "Fecha fin (YYYY-MM-DD)" },
                    service_type_name = new { type = "string", description = "Tipo de servicio (opcional)" }
                },
                required = new[] { "fecha_inicio", "fecha_fin" }
            }
        };
    }

    private static object CreateAppointmentToolAnthropic()
    {
        return new
        {
            name = "create_appointment",
            description = "Agenda una nueva cita. Previo uso de check_availability.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    client_whatsapp = new { type = "string", description = "WhatsApp del cliente" },
                    client_name = new { type = "string", description = "Nombre del cliente" },
                    service_type_name = new { type = "string", description = "Tipo de servicio" },
                    fecha_inicio = new { type = "string", description = "Fecha/hora inicio (ISO 8601)" },
                    notas = new { type = "string", description = "Notas opcionales" }
                },
                required = new[] { "client_whatsapp", "client_name", "service_type_name", "fecha_inicio" }
            }
        };
    }

    private static object CancelAppointmentToolAnthropic()
    {
        return new
        {
            name = "cancel_appointment",
            description = "Cancela una cita existente.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    appointment_identifier = new { type = "string", description = "ID de cita o WhatsApp del cliente" },
                    motivo = new { type = "string", description = "Motivo opcional" }
                },
                required = new[] { "appointment_identifier" }
            }
        };
    }

    private static object RescheduleAppointmentToolAnthropic()
    {
        return new
        {
            name = "reschedule_appointment",
            description = "Reprograma una cita existente.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    appointment_id = new { type = "string", description = "ID de la cita" },
                    nueva_fecha_inicio = new { type = "string", description = "Nueva fecha/hora (ISO 8601)" }
                },
                required = new[] { "appointment_id", "nueva_fecha_inicio" }
            }
        };
    }

    private static object ListAppointmentsToolAnthropic()
    {
        return new
        {
            name = "list_appointments",
            description = "Lista las citas de un cliente.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    client_whatsapp = new { type = "string", description = "WhatsApp del cliente" },
                    estado = new { type = "string", description = "Filtrar: pending|confirmed|cancelled|completed|upcoming. Default: upcoming" }
                },
                required = new[] { "client_whatsapp" }
            }
        };
    }
}
