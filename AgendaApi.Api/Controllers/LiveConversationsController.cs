using System.Security.Cryptography;
using System.Text;
using AgendaApi.Domain.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgendaApi.Api.Controllers;

/// <summary>
/// Dashboard de conversaciones en vivo (WhatsApp + ADAM) para el dueño.
///
/// Muestra TODOS los tenants (gate de superusuario) etiquetando cada mensaje con el nombre del
/// negocio. NO usa JWT: se protege con una clave secreta (Dashboard:Key / DASHBOARD_KEY, con
/// fallback a MASTER_KEY) para poder abrirse directo en el navegador detrás del túnel.
///
/// Lectura pura de <c>conversation_messages</c>; no toca el flujo de procesamiento de mensajes.
/// Los endpoints exigen la clave en el query (no en header) para que el fetch del navegador sea directo.
/// </summary>
[ApiController]
[Route("api/v1/dashboard")]
// Se sale del AuthorizeFilter global (Program.cs): este dashboard se autentica por CLAVE
// (DASHBOARD_KEY/MASTER_KEY) para poder abrirse directo en el navegador sin JWT.
[AllowAnonymous]
public class LiveConversationsController : ControllerBase
{
    private readonly IConversationHistoryRepository _conversationRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<LiveConversationsController> _logger;

    // Cap de filas por poll para no traer rangos enormes. El cliente incrementa con ?after=.
    private const int DefaultLimit = 200;

    public LiveConversationsController(
        IConversationHistoryRepository conversationRepo,
        ITenantRepository tenantRepo,
        IConfiguration configuration,
        IWebHostEnvironment env,
        ILogger<LiveConversationsController> logger)
    {
        _conversationRepo = conversationRepo;
        _tenantRepo = tenantRepo;
        _configuration = configuration;
        _env = env;
        _logger = logger;
    }

    /// <summary>Sirve la página HTML del dashboard en vivo.</summary>
    [HttpGet("conversations/page")]
    public IActionResult Page([FromQuery] string? key)
    {
        // La página en sí es HTML/JS sin datos: si no viene clave (o viene vacía), renderiza el
        // archivo y el propio HTML le pide la clave al usuario (la guarda en sessionStorage).
        // El que de verdad valida la clave es el endpoint /conversations (que expone los datos).
        if (!string.IsNullOrWhiteSpace(key) && !IsValidKey(key))
            return Unauthorized(new { error = "Clave de dashboard inválida" });

        var path = Path.Combine(_env.WebRootPath ?? "wwwroot", "live-dashboard.html");
        if (!System.IO.File.Exists(path))
            return NotFound(new { error = "dashboard no encontrado" });

        return PhysicalFile(path, "text/html");
    }

    /// <summary>
    /// Poll incremental de mensajes. Con ?after= devuelve los mensajes con FechaCreacion >= after
    /// (cronológicos); sin it, el historial reciente (DefaultLimit). La clave es obligatoria.
    /// </summary>
    [HttpGet("conversations")]
    public async Task<IActionResult> List(
        [FromQuery] string key,
        [FromQuery] DateTime? after = null,
        [FromQuery] int limit = DefaultLimit,
        CancellationToken ct = default)
    {
        if (!IsValidKey(key))
            return Unauthorized(new { error = "Clave de dashboard inválida" });

        limit = Math.Clamp(limit, 1, 500);

        var messages = after.HasValue
            ? await _conversationRepo.GetSinceAsync(after.Value, limit, ct)
            : await _conversationRepo.GetLatestAsync(limit, ct);

        if (messages.Count == 0)
            return Ok(new { messages = new object[0] });

        // Resolver nombres de negocio en un solo viaje a la BD.
        var tenants = (await _tenantRepo.GetAllActiveAsync(ct))
            .ToDictionary(t => t.IdTenant, t => string.IsNullOrWhiteSpace(t.NombreComercial) ? t.Nombre : t.NombreComercial);

        var dtos = messages.Select(m => new
        {
            id = m.IdConversationMessage,
            time = m.FechaCreacion,
            tenantId = m.IdTenant,
            tenantName = tenants.TryGetValue(m.IdTenant, out var n) ? n : m.IdTenant.ToString()[..8],
            phone = m.PhoneCliente,
            role = m.Role,
            content = m.Content
        });

        return Ok(new { messages = dtos });
    }

    private bool IsValidKey(string? clientKey)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
            return false;

        var expected = ResolveDashboardKey();
        if (string.IsNullOrEmpty(expected))
        {
            _logger.LogWarning("[Dashboard] DASHBOARD_KEY/MASTER_KEY no configurada — dashboard deshabilitado");
            return false;
        }

        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(clientKey);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Primera fuente no vacía: Dashboard:Key, luego DASHBOARD_KEY, luego MASTER_KEY
    /// (o TokenEncryption__MasterKey, que es como se pasa en el contenedor de producción).
    /// Se salta valores en blanco para que una DASHBOARD_KEY vacía caiga al MASTER_KEY.</summary>
    private string? ResolveDashboardKey()
    {
        foreach (var candidate in new[]
                 {
                     _configuration["Dashboard:Key"],
                     Environment.GetEnvironmentVariable("DASHBOARD_KEY"),
                     Environment.GetEnvironmentVariable("MASTER_KEY"),
                     _configuration["TokenEncryption:MasterKey"],
                     Environment.GetEnvironmentVariable("TokenEncryption__MasterKey")
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }
        return null;
    }
}