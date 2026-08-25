using AgendaApi.Application.DTOs;
using AgendaApi.Domain.Ports;
using AgendaApi.Domain.Services;

namespace AgendaApi.Application.UseCases;

/// <summary>
/// Caso de uso: Cancelar TODAS las citas activas (pendientes/confirmadas) y futuras del
/// cliente en UNA sola operación atómica. Reemplaza la emisión de N tool-calls de
/// cancel_appointment (una por cita) que quemaba el presupuesto del turno en round-trips
/// de IA y committeaba estado parcial si el turno expiraba a mitad de camino.
/// Garantía central: la mutación local de TODAS las citas se persiste con EXACTAMENTE UN
/// SaveChangesAsync (un único commit transaccional).
/// </summary>
public class CancelAllAppointmentsUseCase
{
    private readonly IAppointmentRepository _appointmentRepo;
    private readonly ICalendarProviderFactory _providerFactory;
    private readonly IClientRepository _clientRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWaitlistNotifier _waitlistNotifier;

    public CancelAllAppointmentsUseCase(
        IAppointmentRepository appointmentRepo,
        IClientRepository clientRepo,
        ICalendarProviderFactory providerFactory,
        IUnitOfWork unitOfWork,
        IWaitlistNotifier waitlistNotifier)
    {
        _appointmentRepo = appointmentRepo;
        _clientRepo = clientRepo;
        _providerFactory = providerFactory;
        _unitOfWork = unitOfWork;
        _waitlistNotifier = waitlistNotifier;
    }

    /// <summary>Mensaje para el cliente cuando no hay nada activo que cancelar.</summary>
    public const string NoActiveAppointmentsMessage = "No tienes citas activas para cancelar.";

    public async Task<CancelAllAppointmentsResultDto> ExecuteAsync(
        string clientWhatsApp,
        Guid tenantId,
        CancellationToken ct = default)
    {
        // Resolver cliente por WhatsApp (misma resolución que la cancelación individual)
        var client = await _clientRepo.GetByWhatsAppAsync(clientWhatsApp, tenantId, ct);
        if (client == null)
            return NoActive();

        var citas = await _appointmentRepo.GetByClientIdAsync(client.IdClient, ct);

        // "Ahora" del negocio en hora local marcada como UTC (misma convención que
        // CancelAppointmentUseCase): solo cuentan las citas futuras.
        var businessNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById(
                Environment.GetEnvironmentVariable("Calendar__TimeZone") ?? "America/Bogota"));
        var now = DateTime.SpecifyKind(businessNow, DateTimeKind.Utc);

        var activas = citas
            .Where(a => a.FechaInicio >= now
                        && (a.Estado == "pending" || a.Estado == "confirmed"))
            .OrderBy(a => a.FechaInicio)
            .ToList();

        if (activas.Count == 0)
            return NoActive();

        // 1) PRIMERO el calendario externo, cita por cita (misma convención que
        // CancelAppointmentUseCase: try/catch por cita, no-fatal, se colectan los fallos
        // para reportarlos honestamente al cliente).
        var calendarFailures = new List<string>();
        Domain.Ports.ICalendarProvider? provider = null;
        foreach (var cita in activas)
        {
            if (string.IsNullOrEmpty(cita.ExternalEventId))
                continue;

            try
            {
                provider ??= await _providerFactory.GetProviderAsync(cita.IdTenant, ct);
                if (provider != null)
                    await provider.CancelEventAsync(cita.IdTenant, cita.ExternalEventId, null, ct);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[CancelAllAppointments] Error cancelando evento externo {cita.ExternalEventId}: {ex.Message}");
                calendarFailures.Add($"{cita.ExternalEventId} ({cita.FechaInicio:yyyy-MM-dd HH:mm} UTC): {ex.Message}");
            }
        }

        // 2) Recién DESPUÉS se mutan TODAS las entidades locales y se persisten con UN SOLO
        // SaveChangesAsync: o se cancelan todas, o no se cancela ninguna (sin estado parcial).
        foreach (var cita in activas)
        {
            cita.Estado = "cancelled";
            cita.MotivoCancelacion = null;
            cita.FechaActualizacion = DateTime.UtcNow;
            await _appointmentRepo.UpdateAsync(cita, ct);
        }

        // CRM: refrescar estado/próxima cita del cliente con el historial ya actualizado.
        // Se pliega en el MISMO commit (la versión de una sola cita hace su propio save,
        // aquí eso rompería la garantía de commit único).
        ClientStateCalculator.ApplyDerivedState(client, citas, DateTime.UtcNow);
        await _clientRepo.UpdateAsync(client, ct);

        await _unitOfWork.SaveChangesAsync(ct);

        // P1 Lista de espera (fast path): los cupos liberados pueden matchear entradas en
        // espera. Se dispara tras el commit; no-fatal (el job lo reintenta).
        try
        {
            await _waitlistNotifier.ScanAndNotifyAsync(ct);
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[CancelAllAppointments] Error notificando lista de espera: {ex.Message}");
        }

        var message = activas.Count == 1
            ? "Se canceló 1 cita."
            : $"Se cancelaron {activas.Count} citas.";

        if (calendarFailures.Count > 0)
            message += $" ({calendarFailures.Count} no pudieron quitarse del calendario externo).";

        return new CancelAllAppointmentsResultDto
        {
            CancelledCount = activas.Count,
            Message = message,
            CalendarFailures = calendarFailures
        };
    }

    private static CancelAllAppointmentsResultDto NoActive()
    {
        return new CancelAllAppointmentsResultDto
        {
            CancelledCount = 0,
            Message = NoActiveAppointmentsMessage
        };
    }
}
