using AgendaApi.Domain.Entities;

namespace AgendaApi.Application.Rules;

/// <summary>
/// Resuelve las ventanas de horario activas de una fecha para un profesional (o para el negocio).
/// Compartido entre el read path (CheckAvailabilityUseCase) y el write path (BookingPolicy)
/// para que ambos validen con la MISMA semántica.
///
/// Precedencia (la específica sobre la genérica, igual que AvailabilityException vs AvailabilityRule):
/// 1. Excepción de negocio (IdProfessional == null) de DÍA COMPLETO — el local cerrado bloquea a todos.
/// 2. Excepción personal (del profesional) de DÍA COMPLETO — P no agenda.
/// 3. Excepción personal con horario especial — P usa ese horario.
/// 4. Reglas personales de P del día — P usa las suyas.
/// 5. Excepción de negocio con horario especial — aplica ese horario a quien no tenga personalizado.
/// 6. Reglas de negocio del día — fallback.
///
/// Con las listas personales vacías el resultado es idéntico al comportamiento del negocio (sin personalizar).
/// </summary>
public static class AvailabilityResolver
{
    public sealed record DayWindow(TimeSpan HoraInicio, TimeSpan HoraFin);

    public static List<DayWindow> ResolveDayWindows(
        DateOnly date,
        List<AvailabilityRule> businessRules,
        List<AvailabilityException> businessExceptions,
        List<AvailabilityRule> personalRules,
        List<AvailabilityException> personalExceptions)
    {
        var windows = new List<DayWindow>();

        // 1. Negocio cerrado: nadie agenda ese día
        if (businessExceptions.Any(e => DateOnly.FromDateTime(e.Fecha) == date && e.DiaCompleto))
            return windows;

        var businessDayExc = businessExceptions.Where(e => DateOnly.FromDateTime(e.Fecha) == date).ToList();
        var personalDayExc = personalExceptions.Where(e => DateOnly.FromDateTime(e.Fecha) == date).ToList();

        // 2. Profesional cerrado ese día
        if (personalDayExc.Any(e => e.DiaCompleto))
            return windows;

        // 3. Horario especial personal
        var personalSpecial = personalDayExc
            .Where(e => !e.DiaCompleto && e.HoraInicio.HasValue && e.HoraFin.HasValue)
            .ToList();
        if (personalSpecial.Count > 0)
            return personalSpecial.Select(e => new DayWindow(e.HoraInicio!.Value, e.HoraFin!.Value)).ToList();

        var dayOfWeek = ((int)date.DayOfWeek == 0) ? 7 : (int)date.DayOfWeek;

        // 4. Reglas personales del día
        var personalRulesDay = personalRules.Where(r => r.DiaSemana == dayOfWeek && r.Activo).ToList();
        if (personalRulesDay.Count > 0)
            return personalRulesDay.Select(r => new DayWindow(r.HoraInicio, r.HoraFin)).ToList();

        // 5. Horario especial del negocio
        var businessSpecial = businessDayExc
            .Where(e => !e.DiaCompleto && e.HoraInicio.HasValue && e.HoraFin.HasValue)
            .ToList();
        if (businessSpecial.Count > 0)
            return businessSpecial.Select(e => new DayWindow(e.HoraInicio!.Value, e.HoraFin!.Value)).ToList();

        // 6. Reglas de negocio = fallback
        return businessRules
            .Where(r => r.DiaSemana == dayOfWeek && r.Activo)
            .Select(r => new DayWindow(r.HoraInicio, r.HoraFin))
            .ToList();
    }
}