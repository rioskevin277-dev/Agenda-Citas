namespace AgendaApi.Domain.Ports;

/// <summary>
/// Resultado de la validación de una solicitud de reserva.
/// </summary>
public class BookingValidationResult
{
    public bool IsValid { get; init; }
    public string? Reason { get; init; }

    public static BookingValidationResult Ok() => new() { IsValid = true };

    public static BookingValidationResult Fail(string reason) => new() { IsValid = false, Reason = reason };
}

/// <summary>
/// Puerto del motor de reglas de agenda: valida que una solicitud de reserva
/// cumpla las reglas reales del negocio (horarios laborales, excepciones/feriados,
/// conflictos con citas locales y bloqueos del calendario externo).
/// </summary>
public interface IBookingPolicy
{
    /// <summary>
    /// Valida un intervalo [fechaInicio, fechaFin) contra las reglas del tenant.
    /// </summary>
    /// <param name="tenantId">Tenant del negocio.</param>
    /// <param name="fechaInicio">Inicio de la reserva (hora local del negocio "disfrazada de UTC").</param>
    /// <param name="fechaFin">Fin de la reserva.</param>
    /// <param name="excludeAppointmentId">Cita a ignorar en el chequeo de conflicto (para reprogramación).</param>
    /// <param name="capacidad">Citas simultáneas permitidas en el mismo horario (1 = una a la vez).</param>
    Task<BookingValidationResult> ValidateAsync(
        Guid tenantId,
        DateTime fechaInicio,
        DateTime fechaFin,
        Guid? excludeAppointmentId = null,
        int capacidad = 1,
        CancellationToken ct = default);
}