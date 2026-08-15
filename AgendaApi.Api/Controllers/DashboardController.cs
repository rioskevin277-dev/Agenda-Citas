using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgendaApi.Api.Controllers;

/// <summary>
/// Dashboard operativo del dueño: resume los KPIs del tenant (totales de citas por estado,
/// tasas de cumplimiento, cartera de clientes, ocupación por profesional, serie de demanda
/// y waitlist). Se calcula on-the-fly en cada request con los datos que ya existen.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly GetDashboardStatsUseCase _useCase;
    private readonly ITenantContext _tenantContext;

    public DashboardController(GetDashboardStatsUseCase useCase, ITenantContext tenantContext)
    {
        _useCase = useCase;
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// Resumen operativo del tenant. Rango opcional ?fechaDesde=&amp;fechaHasta=
    /// (ISO YYYY-MM-DD); si se omite, últimos 30 días del reloj del negocio.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(
        [FromQuery] DateTime? fechaDesde,
        [FromQuery] DateTime? fechaHasta,
        CancellationToken ct = default)
    {
        if (!_tenantContext.IsSet)
            return Unauthorized(new { error = "Tenant no configurado" });

        var resumen = await _useCase.ExecuteAsync(_tenantContext.TenantId, fechaDesde, fechaHasta, ct);
        return Ok(resumen);
    }
}