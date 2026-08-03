using AgendaApi.Domain.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgendaApi.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/tenants")]
public class TenantController : ControllerBase
{
    private readonly ITenantRepository _tenantRepo;
    private readonly ICalendarConnectionRepository _connectionRepo;
    private readonly IServiceTypeRepository _serviceTypeRepo;
    private readonly ITokenEncryptionService _tokenEncryption;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<TenantController> _logger;

    public TenantController(
        ITenantRepository tenantRepo,
        ICalendarConnectionRepository connectionRepo,
        IServiceTypeRepository serviceTypeRepo,
        ITokenEncryptionService tokenEncryption,
        IUnitOfWork unitOfWork,
        ILogger<TenantController> logger)
    {
        _tenantRepo = tenantRepo;
        _connectionRepo = connectionRepo;
        _serviceTypeRepo = serviceTypeRepo;
        _tokenEncryption = tokenEncryption;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var tenants = await _tenantRepo.GetAllActiveAsync(ct);
        return Ok(tenants.Select(t => new
        {
            t.IdTenant,
            t.Nombre,
            t.CalendarProvider,
            t.Activo
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTenantRequest request, CancellationToken ct)
    {
        var tenant = new Domain.Entities.Tenant
        {
            IdTenant = Guid.NewGuid(),
            Nombre = request.Nombre,
            NombreComercial = request.NombreComercial,
            Correo = request.Correo,
            Telefono = request.Telefono,
            CalendarProvider = request.CalendarProvider ?? "google",
            FechaCreacion = DateTime.UtcNow,
            FechaActualizacion = DateTime.UtcNow,
            Activo = true,
            WhatsAppPhoneNumberId = request.WhatsAppPhoneNumberId
        };

        tenant = await _tenantRepo.CreateAsync(tenant, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        _logger.LogInformation("[Tenant] Nuevo tenant creado: {Id} - {Nombre}", tenant.IdTenant, tenant.Nombre);

        return CreatedAtAction(nameof(GetAll), new { id = tenant.IdTenant }, new
        {
            tenant.IdTenant,
            tenant.Nombre,
            tenant.CalendarProvider
        });
    }

    [HttpPost("{id:guid}/calendar-connection")]
    public async Task<IActionResult> SetCalendarConnection(Guid id, [FromBody] SetCalendarConnectionRequest request, CancellationToken ct)
    {
        var tenant = await _tenantRepo.GetByIdAsync(id, ct);
        if (tenant == null)
            return NotFound(new { error = "Tenant no encontrado" });

        var encryptedAccessToken = _tokenEncryption.Encrypt(request.AccessToken);
        var encryptedRefreshToken = _tokenEncryption.Encrypt(request.RefreshToken);

        var connection = new Domain.Entities.CalendarConnection
        {
            IdCalendarConnection = Guid.NewGuid(),
            IdTenant = id,
            AccountEmail = request.AccountEmail,
            AccessTokenEncrypted = encryptedAccessToken,
            RefreshTokenEncrypted = encryptedRefreshToken,
            TokenExpiresAt = request.TokenExpiresAt,
            CalendarId = request.CalendarId ?? "primary",
            FechaCreacion = DateTime.UtcNow,
            FechaActualizacion = DateTime.UtcNow,
            Activo = true
        };

        await _connectionRepo.CreateAsync(connection, ct);

        if (!string.IsNullOrEmpty(request.Provider))
        {
            tenant.CalendarProvider = request.Provider;
            tenant.FechaActualizacion = DateTime.UtcNow;
            await _tenantRepo.UpdateAsync(tenant, ct);
        }

        await _unitOfWork.SaveChangesAsync(ct);
        _logger.LogInformation("[Tenant] Conexion de calendario configurada para tenant {Id}: {Provider}",
            id, request.Provider ?? tenant.CalendarProvider);

        return Ok(new { message = "Conexion de calendario configurada exitosamente" });
    }

    [HttpGet("{id:guid}/service-types")]
    public async Task<IActionResult> GetServiceTypes(Guid id, CancellationToken ct)
    {
        var tenant = await _tenantRepo.GetByIdAsync(id, ct);
        if (tenant == null)
            return NotFound(new { error = "Tenant no encontrado" });

        var serviceTypes = await _serviceTypeRepo.GetByTenantIdAsync(id, ct);
        return Ok(serviceTypes.Select(s => new
        {
            s.IdServiceType,
            s.Nombre,
            s.Descripcion,
            s.DuracionMinutos,
            s.BufferMinutos,
            s.Precio,
            s.Activo
        }));
    }

    [HttpPost("{id:guid}/service-types")]
    public async Task<IActionResult> AddServiceType(Guid id, [FromBody] AddServiceTypeRequest request, CancellationToken ct)
    {
        var tenant = await _tenantRepo.GetByIdAsync(id, ct);
        if (tenant == null)
            return NotFound(new { error = "Tenant no encontrado" });

        var serviceType = new Domain.Entities.ServiceType
        {
            IdServiceType = Guid.NewGuid(),
            IdTenant = id,
            Nombre = request.Nombre,
            Descripcion = request.Descripcion,
            DuracionMinutos = request.DuracionMinutos,
            BufferMinutos = request.BufferMinutos,
            Precio = request.Precio,
            Activo = true,
            FechaCreacion = DateTime.UtcNow
        };

        await _serviceTypeRepo.CreateAsync(serviceType, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation("[Tenant] Tipo de servicio agregado: {Nombre} al tenant {Id} (duracion: {Duracion}min, buffer: {Buffer}min)",
            request.Nombre, id, request.DuracionMinutos, request.BufferMinutos);

        return Ok(new
        {
            message = "Tipo de servicio agregado",
            id = serviceType.IdServiceType,
            nombre = serviceType.Nombre,
            duracionMinutos = serviceType.DuracionMinutos,
            bufferMinutos = serviceType.BufferMinutos
        });
    }
}

public record CreateTenantRequest
{
    public string Nombre { get; init; } = string.Empty;
    public string? NombreComercial { get; init; }
    public string? Correo { get; init; }
    public string? Telefono { get; init; }
    public string? CalendarProvider { get; init; }
    public string? WhatsAppPhoneNumberId { get; init; }
}

public record SetCalendarConnectionRequest
{
    public string? AccountEmail { get; init; }
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
    public DateTime? TokenExpiresAt { get; init; }
    public string? CalendarId { get; init; }
    public string? Provider { get; init; }
}

public record AddServiceTypeRequest
{
    public string Nombre { get; init; } = string.Empty;
    public string? Descripcion { get; init; }
    public int DuracionMinutos { get; init; }
    public int BufferMinutos { get; init; }
    public decimal? Precio { get; init; }
}
