using System.Globalization;
using System.Text.Json;

namespace AgendaApi.Infrastructure.Services;

/// <summary>
/// Convierte el resultado de una herramienta del orquestador en una línea legible en español
/// para incluirlas como contexto en el aviso de escalado a humano (qué hizo el AI en el turno
/// antes de entregar la conversación).
/// </summary>
public static class ToolActionSummarizer
{
    public static string Summarize(string toolName, string toolResultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolResultJson);
            var root = doc.RootElement;
            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;

            return toolName switch
            {
                "check_availability" => success
                    ? $"Consultó disponibilidad: {IntField(root, "total_slots")} horario(s) ofrecido(s)."
                    : $"Fallo al consultar disponibilidad: {error ?? "sin detalle"}",
                "create_appointment" => success
                    ? $"Agendó una cita: {AppointmentSummary(root)}."
                    : $"Fallo al agendar la cita: {error ?? "sin detalle"}",
                "cancel_appointment" => success
                    ? $"Canceló la cita {ShortId(root)}."
                    : $"Fallo al cancelar la cita: {error ?? "sin detalle"}",
                "confirm_appointment" => success
                    ? $"Confirmó la cita {ShortId(root)}."
                    : $"Fallo al confirmar la cita: {error ?? "sin detalle"}",
                "reschedule_appointment" => success
                    ? $"Reprogramó una cita a {AppointmentStart(root)}."
                    : $"Fallo al reprogramar la cita: {error ?? "sin detalle"}",
                "list_appointments" => success
                    ? $"Consultó las citas del cliente: {IntField(root, "total")} encontrada(s)."
                    : $"Fallo al listar citas: {error ?? "sin detalle"}",
                "request_human_attention" => "Solicitó atención humana (inicio del escalado).",
                _ => $"Ejecutó la herramienta {toolName}."
            };
        }
        catch
        {
            return $"Ejecutó la herramienta {toolName}.";
        }
    }

    private static int IntField(JsonElement root, string prop)
    {
        if (root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            return n;
        return 0;
    }

    private static string ShortId(JsonElement root)
    {
        if (root.TryGetProperty("appointment", out var app)
            && app.TryGetProperty("id", out var id))
            return id.GetString()?[..8] ?? "";
        return "";
    }

    private static string AppointmentSummary(JsonElement root)
    {
        if (!root.TryGetProperty("appointment", out var app))
            return "sin detalle";
        var servicio = app.TryGetProperty("serviceType", out var st) ? st.GetString() : null;
        var profesional = app.TryGetProperty("professional", out var p) ? p.GetString() : null;
        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(servicio)) partes.Add(servicio!);
        if (!string.IsNullOrWhiteSpace(profesional)) partes.Add($"con {profesional}");
        partes.Add($"para el {StartOf(app)}");
        var estado = app.TryGetProperty("status", out var st2) ? st2.GetString() : null;
        if (!string.IsNullOrWhiteSpace(estado)) partes.Add($"(estado {estado})");
        return string.Join(" ", partes);
    }

    private static string AppointmentStart(JsonElement root)
    {
        if (root.TryGetProperty("appointment", out var app))
            return StartOf(app);
        return "fecha no disponible";
    }

    private static string StartOf(JsonElement container)
    {
        if ((container.TryGetProperty("start", out var st) || container.TryGetProperty("newStart", out st))
            && st.GetString() is { } raw)
        {
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt))
                return dt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            return raw;
        }
        return "fecha no disponible";
    }
}