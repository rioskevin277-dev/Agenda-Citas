namespace AgendaApi.Application.DTOs;

/// <summary>
/// Resumen operativo del tenant (dashboard). Se calcula on-the-fly desde los repositorios
/// existentes en cada request: totales de citas por estado, tasas de cumplimiento,
/// composición de la cartera de clientes, ocupación futura por profesional, serie de
/// demanda en el tiempo y waitlist (activa + cumplidas).
/// </summary>
public record DashboardSummaryDto
{
    public PeriodoDashboardDto Periodo { get; init; } = null!;
    public TotalesCitasDto Totales { get; init; } = null!;
    public TasasDto Tasas { get; init; } = null!;
    public CarteraClientesDto CarteraClientes { get; init; } = null!;
    public List<OcupacionProfesionalDto> OcupacionPorProfesional { get; init; } = new();
    public List<DemandaDiaDto> SerieDemanda { get; init; } = new();
    public WaitlistDto Waitlist { get; init; } = null!;
}

public record PeriodoDashboardDto
{
    public DateTime Desde { get; init; }
    public DateTime Hasta { get; init; }
}

public record TotalesCitasDto
{
    public int Citas { get; init; }
    public int Pending { get; init; }
    public int Confirmed { get; init; }
    public int Cancelled { get; init; }
    public int Completed { get; init; }
    public int NoShow { get; init; }
}

public record TasasDto
{
    /// <summary>% de citas cumplidas sobre las cerrradas (completed / (completed + cancelled + no_show)).</summary>
    public double Cumplimiento { get; init; }
    /// <summary>% de inasistencias sobre las cerrradas en fecha (no_show / (completed + no_show)).</summary>
    public double Inasistencias { get; init; }
}

public record CarteraClientesDto
{
    public int Nuevo { get; init; }
    public int Frecuente { get; init; }
    public int Inactivo { get; init; }
    public int NoShow { get; init; }
    public int Vip { get; init; }
    public int Blacklist { get; init; }
}

public record OcupacionProfesionalDto
{
    public string Profesional { get; init; } = string.Empty;
    /// <summary>Citas pendientes/confirmadas futuras a nombre de ese profesional.</summary>
    public int CitasProximas { get; init; }
}

public record DemandaDiaDto
{
    public DateOnly Fecha { get; init; }
    /// <summary>Citas creadas ese día (por FechaCreacion).</summary>
    public int Creadas { get; init; }
    /// <summary>Citas realizadas ese día (estado completed, por FechaInicio).</summary>
    public int Realizadas { get; init; }
}

public record WaitlistDto
{
    /// <summary>Entradas de lista de espera aún activas.</summary>
    public int Activa { get; init; }
    /// <summary>Entradas de lista de espera ya cumplidas (el cliente reservó).</summary>
    public int Cumplidas { get; init; }
}