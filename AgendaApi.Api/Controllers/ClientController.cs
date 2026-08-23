using AgendaApi.Domain.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgendaApi.Api.Controllers;

/// <summary>
/// CRM del dueño: gestión de los clientes del tenant. Permite listar, ver el detalle
/// (perfil + historial + conversaciones) y actualizar nombre/email/tags/estado/notas.
/// El estado manual (vip/blacklist) no es sobreescrito por el cálculo derivado.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/clients")]
public class ClientController : ControllerBase
{
    private static readonly HashSet<string> EstadosValidos = new(StringComparer.OrdinalIgnoreCase)
    {
        "nuevo", "frecuente", "inactivo", "no_show", "vip", "blacklist"
    };

    private readonly IClientRepository _clientRepo;
    private readonly IAppointmentRepository _appointmentRepo;
    private readonly IConversationHistoryRepository _conversationRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;

    public ClientController(
        IClientRepository clientRepo,
        IAppointmentRepository appointmentRepo,
        IConversationHistoryRepository conversationRepo,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext)
    {
        _clientRepo = clientRepo;
        _appointmentRepo = appointmentRepo;
        _conversationRepo = conversationRepo;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    /// <summary>Listar clientes del tenant (filtro opcional ?q=).</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? q = null, CancellationToken ct = default)
    {
        if (!_tenantContext.IsSet)
            return Unauthorized(new { error = "Tenant no configurado" });

        var clients = await _clientRepo.GetByTenantIdAsync(_tenantContext.TenantId, q, ct);
        return Ok(clients.Select(c => new
        {
            id = c.IdClient,
            nombre = c.Nombre,
            whatsapp = c.WhatsApp,
            email = c.Email,
            estado = c.Estado,
            tags = c.Tags,
            notas = c.Notas,
            activo = c.Activo,
            ultimaInteraccion = c.UltimaInteraccion,
            proximaCita = c.ProximaCita
        }));
    }

    /// <summary>Detalle del cliente: perfil + historial de citas + últimas conversaciones.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        if (!_tenantContext.IsSet)
            return Unauthorized(new { error = "Tenant no configurado" });

        var client = await _clientRepo.GetByIdAsync(id, ct);
        if (client == null || client.IdTenant != _tenantContext.TenantId)
            return NotFound(new { error = "Cliente no encontrado" });

        var appointments = await _appointmentRepo.GetByClientIdAsync(client.IdClient, ct);
        // La conversación se keyea por la identidad canónica del cliente (BSUID si lo tiene, si no teléfono).
        var conversations = await _conversationRepo.GetRecentAsync(_tenantContext.TenantId, client.UserId ?? client.WhatsApp, 20, ct);

        return Ok(new
        {
            cliente = new
            {
                client.IdClient,
                client.IdTenant,
                client.Nombre,
                client.Email,
                client.WhatsApp,
                client.UserId,
                client.Username,
                client.Estado,
                client.Tags,
                client.Notas,
                client.Activo,
                client.UltimaInteraccion,
                client.ProximaCita,
                client.FechaCreacion
            },
            citas = appointments.Select(a => new
            {
                a.IdAppointment,
                servicio = a.ServiceType?.Nombre,
                a.FechaInicio,
                a.FechaFin,
                a.Estado,
                profesional = a.Professional?.Nombre
            }),
            conversaciones = conversations.Select(c => new
            {
                c.Role,
                c.Content,
                c.FechaCreacion
            })
        });
    }

    /// <summary>
    /// Actualizar el perfil del cliente (nombre/email/tags/estado/notas). Estado validado
    /// contra la allow-list; los estados manuales (vip/blacklist) prevalecen sobre el derivado.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateClientRequest request, CancellationToken ct)
    {
        if (!_tenantContext.IsSet)
            return Unauthorized(new { error = "Tenant no configurado" });

        var client = await _clientRepo.GetByIdAsync(id, ct);
        if (client == null || client.IdTenant != _tenantContext.TenantId)
            return NotFound(new { error = "Cliente no encontrado" });

        if (!string.IsNullOrWhiteSpace(request.Estado) && !EstadosValidos.Contains(request.Estado))
            return BadRequest(new { error = $"Estado '{request.Estado}' no es válido. Válidos: {string.Join(", ", EstadosValidos)}" });

        // Solo sobrescribe los campos que vengan definidos (null = no tocar).
        if (!string.IsNullOrEmpty(request.Nombre)) client.Nombre = request.Nombre;
        if (!string.IsNullOrEmpty(request.Email)) client.Email = request.Email;
        if (!string.IsNullOrEmpty(request.Tags)) client.Tags = request.Tags;
        if (!string.IsNullOrEmpty(request.Notas)) client.Notas = request.Notas;
        if (!string.IsNullOrWhiteSpace(request.Estado)) client.Estado = request.Estado.ToLowerInvariant();

        client.FechaActualizacion = DateTime.UtcNow;
        await _clientRepo.UpdateAsync(client, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return Ok(new
        {
            client.IdClient,
            client.Nombre,
            client.Email,
            client.WhatsApp,
            client.Estado,
            client.Tags,
            client.Notas
        });
    }
}

public record UpdateClientRequest
{
    public string? Nombre { get; init; }
    public string? Email { get; init; }
    public string? Tags { get; init; }
    public string? Estado { get; init; }
    public string? Notas { get; init; }
}