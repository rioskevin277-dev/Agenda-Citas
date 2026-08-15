using AgendaApi.Application.DTOs;
using AgendaApi.Domain.Ports;

namespace AgendaApi.Application.UseCases;

/// <summary>
/// Caso de uso: Resumen operativo del tenant (dashboard). Calcula on-the-fly los KPIs a partir
/// de los repositorios existentes, sin tocar el hot path de citas ni agregar tablas/caché.
/// Cada request compone: totales por estado, tasas de cumplimiento, cartera de clientes,
/// ocupación futura por profesional, serie de demanda diaria y waitlist (activa + cumplidas).
/// </summary>
public class GetDashboardStatsUseCase
{
    // Estados de cita (coinciden con el dominio).
    private const string EstadoCompleted = "completed";
    private const string EstadoNoShow = "no_show";
    private const string EstadoCancelled = "cancelled";

    // Rango por defecto: últimos 30 días del reloj del negocio.
    private static readonly TimeSpan DefaultRango = TimeSpan.FromDays(30);

    private readonly IAppointmentRepository _appointmentRepo;
    private readonly IClientRepository _clientRepo;
    private readonly IProfessionalRepository _professionalRepo;
    private readonly IWaitlistEntryRepository _waitlistRepo;

    public GetDashboardStatsUseCase(
        IAppointmentRepository appointmentRepo,
        IClientRepository clientRepo,
        IProfessionalRepository professionalRepo,
        IWaitlistEntryRepository waitlistRepo)
    {
        _appointmentRepo = appointmentRepo;
        _clientRepo = clientRepo;
        _professionalRepo = professionalRepo;
        _waitlistRepo = waitlistRepo;
    }

    public async Task<DashboardSummaryDto> ExecuteAsync(
        Guid tenantId,
        DateTime? desde,
        DateTime? hasta,
        CancellationToken ct = default)
    {
        // Misma convención de reloj del negocio que AppointmentRepository: "ahora" local del
        // negocio (Calendar__TimeZone, por defecto America/Bogota) marcado como UTC. El rango
        // faltante se resuelve desde ese reloj.
        var now = BusinessNow();
        var desdeDate = (desde ?? now.Add(DefaultRango.Negate())).Date;
        var hastaDate = (hasta ?? now).Date;
        if (hastaDate < desdeDate) hastaDate = desdeDate;

        // ── Citas del tenant (filtradas/agrupadas en memoria; no se tocan los Include) ──
        var appointments = await _appointmentRepo.GetByTenantIdAsync(tenantId, ct: ct);
        var enRango = appointments
            .Where(a => a.FechaInicio.Date >= desdeDate && a.FechaInicio.Date <= hastaDate)
            .ToList();

        var totales = BuildTotales(enRango);
        var tasas = BuildTasas(totales);
        var serieDemanda = BuildSerieDemanda(appointments, desdeDate, hastaDate);
        var ocupacion = await BuildOcupacionAsync(tenantId, now, ct);

        // ── Cartera de clientes ──
        var clients = await _clientRepo.GetByTenantIdAsync(tenantId, ct: ct);
        var cartera = BuildCartera(clients);

        // ── Waitlist (activa + cumplidas) ──
        var waitlistActiva = await _waitlistRepo.GetActiveByTenantAsync(tenantId, ct);
        var waitlistCumplidas = await _waitlistRepo.GetFulfilledByTenantAsync(tenantId, ct);

        return new DashboardSummaryDto
        {
            Periodo = new PeriodoDashboardDto { Desde = desdeDate, Hasta = hastaDate },
            Totales = totales,
            Tasas = tasas,
            CarteraClientes = cartera,
            OcupacionPorProfesional = ocupacion,
            SerieDemanda = serieDemanda,
            Waitlist = new WaitlistDto
            {
                Activa = waitlistActiva.Count,
                Cumplidas = waitlistCumplidas
            }
        };
    }

    private static TotalesCitasDto BuildTotales(List<Domain.Entities.Appointment> enRango)
    {
        var tp = new TotalesCitasDto
        {
            Citas = enRango.Count,
            Pending = enRango.Count(a => a.Estado == "pending"),
            Confirmed = enRango.Count(a => a.Estado == "confirmed"),
            Cancelled = enRango.Count(a => a.Estado == EstadoCancelled),
            Completed = enRango.Count(a => a.Estado == EstadoCompleted),
            NoShow = enRango.Count(a => a.Estado == EstadoNoShow)
        };
        return tp;
    }

    private static TasasDto BuildTasas(TotalesCitasDto totales)
    {
        var cerradas = totales.Completed + totales.Cancelled + totales.NoShow;
        var enFecha = totales.Completed + totales.NoShow;
        return new TasasDto
        {
            // % de cumplimiento sobre las citas cerradas (citas cumplidas / citas cerradas).
            Cumplimiento = cerradas == 0 ? 0 : Math.Round((double)totales.Completed / cerradas, 4),
            // % de inasistencias sobre las citas que llegaron a su fecha (no_show / completadas+no_show).
            Inasistencias = enFecha == 0 ? 0 : Math.Round((double)totales.NoShow / enFecha, 4)
        };
    }

    private static List<DemandaDiaDto> BuildSerieDemanda(
        List<Domain.Entities.Appointment> appointments, DateTime desdeDate, DateTime hastaDate)
    {
        var serie = new List<DemandaDiaDto>();
        var desde = DateOnly.FromDateTime(desdeDate);
        var hasta = DateOnly.FromDateTime(hastaDate);

        for (var dia = desde; dia <= hasta; dia = dia.AddDays(1))
        {
            var diaDt = dia.ToDateTime(TimeOnly.MinValue);
            serie.Add(new DemandaDiaDto
            {
                Fecha = dia,
                // Creadas: citas cuya FechaCreacion cayó ese día (demanda entrante).
                Creadas = appointments.Count(a => a.FechaCreacion.Date == diaDt),
                // Realizadas: citas completadas cuya FechaInicio cayó ese día.
                Realizadas = appointments.Count(a => a.Estado == EstadoCompleted && a.FechaInicio.Date == diaDt)
            });
        }

        return serie;
    }

    private async Task<List<OcupacionProfesionalDto>> BuildOcupacionAsync(
        Guid tenantId, DateTime now, CancellationToken ct)
    {
        // Ocupación futura: citas pendientes/confirmadas que aún no llegan.
        var proximas = (await _appointmentRepo.GetByTenantIdAsync(tenantId, ct: ct))
            .Where(a => (a.Estado == "pending" || a.Estado == "confirmed") && a.FechaInicio >= now)
            .ToList();

        if (proximas.Count == 0)
            return new List<OcupacionProfesionalDto>();

        var profesionales = await _professionalRepo.GetActiveByTenantIdAsync(tenantId, ct);
        var nombrePorId = profesionales.ToDictionary(p => p.IdProfessional, p => p.Nombre);

        return proximas
            .GroupBy(a => a.IdProfessional)
            .OrderByDescending(g => g.Count())
            .Select(g => new OcupacionProfesionalDto
            {
                // Sin profesional (cita "del negocio") → agrupar bajo "General".
                Profesional = g.Key.HasValue && nombrePorId.TryGetValue(g.Key.Value, out var nombre)
                    ? nombre
                    : "General",
                CitasProximas = g.Count()
            })
            .ToList();
    }

    private static CarteraClientesDto BuildCartera(List<Domain.Entities.Client> clients)
    {
        var cartera = new CarteraClientesDto();
        foreach (var cliente in clients)
        {
            switch (cliente.Estado.ToLowerInvariant())
            {
                case "nuevo": cartera = cartera with { Nuevo = cartera.Nuevo + 1 }; break;
                case "frecuente": cartera = cartera with { Frecuente = cartera.Frecuente + 1 }; break;
                case "inactivo": cartera = cartera with { Inactivo = cartera.Inactivo + 1 }; break;
                case "no_show": cartera = cartera with { NoShow = cartera.NoShow + 1 }; break;
                case "vip": cartera = cartera with { Vip = cartera.Vip + 1 }; break;
                case "blacklist": cartera = cartera with { Blacklist = cartera.Blacklist + 1 }; break;
            }
        }
        return cartera;
    }

    /// <summary>"Ahora" del negocio (Calendar__TimeZone) marcado como UTC — misma convención
    /// que AppointmentRepository.GetMissingExternalEventsAsync.</summary>
    private static DateTime BusinessNow()
    {
        var tzName = Environment.GetEnvironmentVariable("Calendar__TimeZone") ?? "America/Bogota";
        var tz = TimeZoneInfo.FindSystemTimeZoneById(tzName);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz), DateTimeKind.Utc);
    }
}