using AgendaApi.Domain.Ports;
using System.Security.Claims;

namespace AgendaApi.Api.Middleware;

/// <summary>
/// Middleware que enriquece el contexto con el tenant actual.
/// Mismo patrón que TenantEnricherMiddleware de AdamApi.
/// Para requests autenticados: extrae IdEmpresa del JWT.
/// Para webhooks de WhatsApp: se resuelve dinámicamente en el controlador.
/// </summary>
public class TenantEnricherMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantEnricherMiddleware> _logger;

    public TenantEnricherMiddleware(RequestDelegate next, ILogger<TenantEnricherMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var idTenantClaim = context.User.FindFirst("IdTenant")?.Value;
            var isSuperUsuario = context.User.FindFirst("Rol")?.Value == "superadmin";

            if (!isSuperUsuario && !string.IsNullOrEmpty(idTenantClaim) && Guid.TryParse(idTenantClaim, out var idTenant))
            {
                if (!tenantContext.IsSet)
                {
                    tenantContext.SetTenant(
                        idTenant,
                        calendarProvider: null,
                        whatsAppAccessToken: null,
                        phoneNumberId: null
                    );

                    _logger.LogDebug("[TenantEnricher] Tenant set: {TenantId}", idTenant);
                }
            }
        }

        await _next(context);
    }
}
