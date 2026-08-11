namespace AgendaApi.Application.Support;

/// <summary>
/// Reloj del negocio. Las citas se guardan en hora local del negocio "disfrazada de UTC"
/// (un evento de las 14:00 en Colombia se almacena como 14:00Z), así que el "ahora" también
/// se convierte al huso horario del negocio (env Calendar__TimeZone) y se marca como UTC.
/// Misma convención que AppointmentRepository y los use cases de confirmar/reagendar.
/// </summary>
public static class BusinessClock
{
    public static DateTime Now
    {
        get
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById(
                    Environment.GetEnvironmentVariable("Calendar__TimeZone") ?? "America/Bogota"));
            return DateTime.SpecifyKind(local, DateTimeKind.Utc);
        }
    }
}
