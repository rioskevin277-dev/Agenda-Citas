using System.Text.Json;
using AgendaApi.Domain.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgendaApi.Api.Controllers.OAuth;

/// <summary>
/// Controlador para el flujo OAuth2 de Microsoft 365 (Outlook Calendar).
/// Inicia la autorización y recibe el callback con el código de autorización.
/// </summary>
[ApiController]
[Route("api/v1/oauth/microsoft")]
[AllowAnonymous]
public class MicrosoftOAuthController : ControllerBase
{
    private readonly ITenantRepository _tenantRepo;
    private readonly ICalendarConnectionRepository _connectionRepo;
    private readonly ITokenEncryptionService _tokenEncryption;
    private readonly ILogger<MicrosoftOAuthController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public MicrosoftOAuthController(
        ITenantRepository tenantRepo,
        ICalendarConnectionRepository connectionRepo,
        ITokenEncryptionService tokenEncryption,
        ILogger<MicrosoftOAuthController> logger,
        IHttpClientFactory httpClientFactory)
    {
        _tenantRepo = tenantRepo;
        _connectionRepo = connectionRepo;
        _tokenEncryption = tokenEncryption;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// GET /api/v1/oauth/microsoft/authorize?tenantId={tenantId}
    /// Inicia el flujo OAuth redirigiendo al usuario a Microsoft.
    /// </summary>
    [HttpGet("authorize")]
    public IActionResult Authorize([FromQuery] Guid tenantId)
    {
        var clientId = Environment.GetEnvironmentVariable("MicrosoftOAuth__ClientId")
                       ?? throw new InvalidOperationException("MicrosoftOAuth__ClientId no configurado");

        var redirectUri = $"{Request.Scheme}://{Request.Host}/api/v1/oauth/microsoft/callback";
        var state = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { tenantId })));

        var scopes = "https://graph.microsoft.com/Calendars.ReadWrite%20offline_access";
        var authUrl = $"https://login.microsoftonline.com/common/oauth2/v2.0/authorize" +
                      $"?client_id={clientId}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                      $"&response_type=code" +
                      $"&scope={scopes}" +
                      $"&state={Uri.EscapeDataString(state)}";

        _logger.LogInformation("[MicrosoftOAuth] Redirigiendo a autorización para tenant {TenantId}", tenantId);

        return Redirect(authUrl);
    }

    /// <summary>
    /// GET /api/v1/oauth/microsoft/callback?code={code}&state={state}
    /// Microsoft redirige aquí después de la autorización.
    /// Intercambia el código por tokens y los guarda cifrados.
    /// </summary>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string code,
        [FromQuery] string state,
        CancellationToken ct)
    {
        try
        {
            var stateJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(state));
            var stateData = JsonSerializer.Deserialize<OAuthState>(stateJson);
            var tenantId = stateData?.TenantId ?? Guid.Empty;

            _logger.LogInformation("[MicrosoftOAuth] Callback recibido para tenant {TenantId}", tenantId);

            var clientId = Environment.GetEnvironmentVariable("MicrosoftOAuth__ClientId")
                           ?? throw new InvalidOperationException("MicrosoftOAuth__ClientId no configurado");
            var clientSecret = Environment.GetEnvironmentVariable("MicrosoftOAuth__ClientSecret")
                               ?? throw new InvalidOperationException("MicrosoftOAuth__ClientSecret no configurado");

            var redirectUri = $"{Request.Scheme}://{Request.Host}/api/v1/oauth/microsoft/callback";

            // Intercambiar código por tokens
            var httpClient = _httpClientFactory.CreateClient();
            var tokenBody = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("redirect_uri", redirectUri),
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("scope", "https://graph.microsoft.com/Calendars.ReadWrite offline_access")
            });

            var response = await httpClient.PostAsync(
                "https://login.microsoftonline.com/common/oauth2/v2.0/token", tokenBody, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var tokenData = JsonSerializer.Deserialize<MicrosoftTokenResponse>(json);

            if (tokenData == null || string.IsNullOrEmpty(tokenData.AccessToken))
                return BadRequest(new { error = "Error obteniendo tokens de Microsoft" });

            // Obtener info del usuario
            var userInfoRequest = new HttpRequestMessage(
                System.Net.Http.HttpMethod.Get,
                "https://graph.microsoft.com/v1.0/me");
            userInfoRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenData.AccessToken);
            var userInfoResponse = await httpClient.SendAsync(userInfoRequest, ct);
            userInfoResponse.EnsureSuccessStatusCode();
            var userInfoJson = await userInfoResponse.Content.ReadAsStringAsync(ct);
            var userInfo = JsonSerializer.Deserialize<MicrosoftUserInfo>(userInfoJson);

            // Guardar o actualizar conexión
            var existing = await _connectionRepo.GetByTenantIdAsync(tenantId, ct);
            if (existing != null)
            {
                existing.AccessTokenEncrypted = _tokenEncryption.Encrypt(tokenData.AccessToken);
                existing.RefreshTokenEncrypted = _tokenEncryption.Encrypt(tokenData.RefreshToken ?? existing.RefreshTokenEncrypted);
                existing.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.ExpiresIn);
                existing.AccountEmail = userInfo?.Mail ?? userInfo?.UserPrincipalName ?? existing.AccountEmail;
                existing.FechaActualizacion = DateTime.UtcNow;
                await _connectionRepo.UpdateAsync(existing, ct);
            }
            else
            {
                var connection = new Domain.Entities.CalendarConnection
                {
                    IdCalendarConnection = Guid.NewGuid(),
                    IdTenant = tenantId,
                    AccountEmail = userInfo?.Mail ?? userInfo?.UserPrincipalName,
                    AccessTokenEncrypted = _tokenEncryption.Encrypt(tokenData.AccessToken),
                    RefreshTokenEncrypted = _tokenEncryption.Encrypt(tokenData.RefreshToken ?? ""),
                    TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.ExpiresIn),
                    Activo = true,
                    FechaCreacion = DateTime.UtcNow,
                    FechaActualizacion = DateTime.UtcNow
                };
                await _connectionRepo.CreateAsync(connection, ct);
            }

            _logger.LogInformation("[MicrosoftOAuth] Conexión de Outlook Calendar configurada para tenant {TenantId}", tenantId);

            return Ok(new
            {
                message = "Microsoft 365 Calendar conectado exitosamente",
                tenantId,
                accountEmail = userInfo?.Mail ?? userInfo?.UserPrincipalName
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MicrosoftOAuth] Error en callback OAuth");
            return StatusCode(500, new { error = "Error configurando Microsoft 365 Calendar", detail = ex.Message });
        }
    }

    private class OAuthState
    {
        public Guid TenantId { get; set; }
    }

    private class MicrosoftTokenResponse
    {
        public string AccessToken { get; set; } = string.Empty;
        public string? RefreshToken { get; set; }
        public int ExpiresIn { get; set; }
    }

    private class MicrosoftUserInfo
    {
        public string? Mail { get; set; }
        public string? UserPrincipalName { get; set; }
        public string? DisplayName { get; set; }
    }
}
