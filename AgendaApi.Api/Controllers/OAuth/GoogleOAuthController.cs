using System.Text.Json;
using System.Text.Json.Serialization;
using AgendaApi.Domain.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgendaApi.Api.Controllers.OAuth;

/// <summary>
/// Controlador para el flujo OAuth2 de Google Calendar.
/// Inicia la autorización y recibe el callback con el código de autorización.
/// </summary>
[ApiController]
[Route("api/v1/oauth/google")]
[AllowAnonymous]
public class GoogleOAuthController : ControllerBase
{
    private readonly ITenantRepository _tenantRepo;
    private readonly ICalendarConnectionRepository _connectionRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITokenEncryptionService _tokenEncryption;
    private readonly ILogger<GoogleOAuthController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    // Google devuelve las propiedades en snake_case (access_token, expires_in, ...).
    // System.Text.Json es sensible a mayúsculas por defecto, así que sin esto
    // las respuestas se deserializaban vacías y fallaba el intercambio de tokens.
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    public GoogleOAuthController(
        ITenantRepository tenantRepo,
        ICalendarConnectionRepository connectionRepo,
        IUnitOfWork unitOfWork,
        ITokenEncryptionService tokenEncryption,
        ILogger<GoogleOAuthController> logger,
        IHttpClientFactory httpClientFactory)
    {
        _tenantRepo = tenantRepo;
        _connectionRepo = connectionRepo;
        _unitOfWork = unitOfWork;
        _tokenEncryption = tokenEncryption;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// GET /api/v1/oauth/google/authorize?tenantId={tenantId}
    /// Inicia el flujo OAuth redirigiendo al usuario a Google.
    /// </summary>
    [HttpGet("authorize")]
    public IActionResult Authorize([FromQuery] Guid tenantId)
    {
        var clientId = Environment.GetEnvironmentVariable("GoogleOAuth__ClientId")
                       ?? throw new InvalidOperationException("GoogleOAuth__ClientId no configurado");

        var redirectUri = $"{Request.Scheme}://{Request.Host}/api/v1/oauth/google/callback";

        var scopes = "https://www.googleapis.com/auth/calendar%20https://www.googleapis.com/auth/calendar.events%20https://www.googleapis.com/auth/userinfo.email";
        var state = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { tenantId })));

        var authUrl = $"https://accounts.google.com/o/oauth2/v2/auth" +
                      $"?client_id={clientId}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                      $"&response_type=code" +
                      $"&scope={scopes}" +
                      $"&access_type=offline" +
                      $"&prompt=consent" +
                      $"&state={Uri.EscapeDataString(state)}";

        _logger.LogInformation("[GoogleOAuth] Redirigiendo a autorización para tenant {TenantId}", tenantId);

        return Redirect(authUrl);
    }

    /// <summary>
    /// GET /api/v1/oauth/google/callback?code={code}&state={state}
    /// Google redirige aquí después de la autorización.
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
            // Decodificar state para obtener tenantId
            var stateJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(state));
            var stateData = JsonSerializer.Deserialize<OAuthState>(stateJson, WebJsonOptions);
            var tenantId = stateData?.TenantId ?? Guid.Empty;

            _logger.LogInformation("[GoogleOAuth] Callback recibido para tenant {TenantId}", tenantId);

            var clientId = Environment.GetEnvironmentVariable("GoogleOAuth__ClientId")
                           ?? throw new InvalidOperationException("GoogleOAuth__ClientId no configurado");
            var clientSecret = Environment.GetEnvironmentVariable("GoogleOAuth__ClientSecret")
                               ?? throw new InvalidOperationException("GoogleOAuth__ClientSecret no configurado");

            var redirectUri = $"{Request.Scheme}://{Request.Host}/api/v1/oauth/google/callback";

            // Intercambiar código por tokens
            var httpClient = _httpClientFactory.CreateClient();
            var tokenBody = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("redirect_uri", redirectUri),
                new KeyValuePair<string, string>("grant_type", "authorization_code")
            });

            var response = await httpClient.PostAsync("https://oauth2.googleapis.com/token", tokenBody, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode,
                    new { error = "Google rechazó el intercambio de tokens", status = (int)response.StatusCode, detail = json });

            var tokenData = JsonSerializer.Deserialize<GoogleTokenResponse>(json, WebJsonOptions);

            if (tokenData == null || string.IsNullOrEmpty(tokenData.AccessToken))
                return BadRequest(new { error = "Error obteniendo tokens de Google", detail = json });

            // Obtener info del usuario para el accountEmail (best-effort: si falla,
            // se guarda la conexión igualmente, el email es solo informativo).
            GoogleUserInfo? userInfo = null;
            try
            {
                var userInfoRequest = new HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get,
                    "https://www.googleapis.com/oauth2/v2/userinfo");
                userInfoRequest.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenData.AccessToken);
                var userInfoResponse = await httpClient.SendAsync(userInfoRequest, ct);
                if (userInfoResponse.IsSuccessStatusCode)
                {
                    var userInfoJson = await userInfoResponse.Content.ReadAsStringAsync(ct);
                    userInfo = JsonSerializer.Deserialize<GoogleUserInfo>(userInfoJson, WebJsonOptions);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GoogleOAuth] No se pudo obtener la info del usuario, continúa sin email");
            }

            // Guardar o actualizar conexión
            var existing = await _connectionRepo.GetByTenantIdAsync(tenantId, ct);
            if (existing != null)
            {
                existing.AccessTokenEncrypted = _tokenEncryption.Encrypt(tokenData.AccessToken);
                existing.RefreshTokenEncrypted = _tokenEncryption.Encrypt(tokenData.RefreshToken ?? existing.RefreshTokenEncrypted);
                existing.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.ExpiresIn);
                existing.AccountEmail = userInfo?.Email ?? existing.AccountEmail;
                existing.CalendarId = "primary";
                existing.FechaActualizacion = DateTime.UtcNow;
                await _connectionRepo.UpdateAsync(existing, ct);
            }
            else
            {
                var connection = new Domain.Entities.CalendarConnection
                {
                    IdCalendarConnection = Guid.NewGuid(),
                    IdTenant = tenantId,
                    AccountEmail = userInfo?.Email,
                    AccessTokenEncrypted = _tokenEncryption.Encrypt(tokenData.AccessToken),
                    RefreshTokenEncrypted = _tokenEncryption.Encrypt(tokenData.RefreshToken ?? ""),
                    TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.ExpiresIn),
                    CalendarId = "primary",
                    Activo = true,
                    FechaCreacion = DateTime.UtcNow,
                    FechaActualizacion = DateTime.UtcNow
                };
                await _connectionRepo.CreateAsync(connection, ct);
            }

            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("[GoogleOAuth] Conexión de Google Calendar configurada para tenant {TenantId}", tenantId);

            return Ok(new
            {
                message = "Google Calendar conectado exitosamente",
                tenantId,
                accountEmail = userInfo?.Email
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GoogleOAuth] Error en callback OAuth");
            return StatusCode(500, new { error = "Error configurando Google Calendar", detail = ex.Message });
        }
    }

    private class OAuthState
    {
        [JsonPropertyName("tenantId")]
        public Guid TenantId { get; set; }
    }

    private class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private class GoogleUserInfo
    {
        [JsonPropertyName("email")]
        public string? Email { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
