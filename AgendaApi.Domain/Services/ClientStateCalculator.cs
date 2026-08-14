using AgendaApi.Domain.Entities;

namespace AgendaApi.Domain.Services;

/// <summary>
/// Calcula el estado derivado del cliente (nuevo/frecuente/inactivo/no_show) y su próxima
/// cita a partir del historial de citas. Fuente única de verdad del CRM: lo usan tanto
/// <c>ClientContextService</c> (contexto para la IA) como los use cases de citas al cambiar
/// una cita. No pisa estados manuales del dueño (vip/blacklist).
/// </summary>
public static class ClientStateCalculator
{
    // Estados asignados a mano por el dueño, jamás derivados.
    private static readonly HashSet<string> EstadosManuales = new(StringComparer.OrdinalIgnoreCase)
    {
        "vip", "blacklist"
    };

    /// <summary>
    /// Deriva el estado del cliente y la próxima cita futura no cancelada a partir del
    /// historial. Jerarquía: no_show (2+ inasistencias) > inactivo (90+ días sin interacción)
    /// > frecuente (3+ citas completadas) > nuevo. Si el dueño asignó un estado manual
    /// (vip/blacklist), no se sobreescribe.
    /// </summary>
    public static void ApplyDerivedState(Client client, IEnumerable<Appointment>? appointments, DateTime now)
    {
        var lista = appointments as IReadOnlyList<Appointment> ?? appointments?.ToList() ?? new List<Appointment>();

        var pendientesFuturas = lista
            .Where(a => a.Estado is "pending" or "confirmed" && a.FechaInicio >= now)
            .ToList();

        // Próxima cita: la más próxima de las pendientes/confirmadas futuras.
        client.ProximaCita = pendientesFuturas.Count == 0
            ? null
            : pendientesFuturas.OrderBy(a => a.FechaInicio).First().FechaInicio;

        // Si el dueño puso un estado manual (no derivado), no lo sobreescribimos.
        if (EstadosManuales.Contains(client.Estado ?? ""))
            return;

        var concretadas = lista.Count(a => a.Estado == "completed");
        var noShow = lista.Count(a => a.Estado == "no_show");
        var inactivoDias = client.UltimaInteraccion.HasValue
            ? (now - client.UltimaInteraccion.Value).TotalDays
            : 0;

        // Jerarquía de estados derivados: la condición más severa gana.
        if (noShow >= 2)
            client.Estado = "no_show";
        else if (inactivoDias > 90)
            client.Estado = "inactivo";
        else if (concretadas >= 3)
            client.Estado = "frecuente";
        else
            client.Estado = "nuevo";
    }

    /// <summary>Devuelve el estado en texto legible para el prompt de la IA.</summary>
    public static string TraducirEstado(string estado) => estado switch
    {
        "nuevo" => "Cliente nuevo (primera interacción o pocas citas)",
        "frecuente" => "Cliente frecuente (3+ citas concretadas)",
        "inactivo" => "Cliente inactivo (sin interacción hace 90+ días)",
        "no_show" => "Cliente con inasistencias recurrentes",
        "vip" => "Cliente VIP (marcado por el dueño)",
        "blacklist" => "Cliente en lista negra (marcado por el dueño)",
        _ => estado
    };
}
