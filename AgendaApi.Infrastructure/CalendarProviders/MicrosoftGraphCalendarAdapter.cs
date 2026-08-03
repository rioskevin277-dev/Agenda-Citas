using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Infrastructure.CalendarProviders;

/// <summary>
/// Adaptador real para Microsoft Graph API (Outlook Calendar).
/// Usa REST API directamente (no SDK).
/// </summary>
public class MicrosoftGraphCalendarAdapter : ICalendarProvider
{
    public string ProviderName => "microsoft";

    private readonly HttpClient _httpClient;
    private readonly ICalendarConnectionRepository _connectionRepo;
    private readonly ITokenEncryptionService _tokenEncryption;
    private readonly ILogger<MicrosoftGraphCalendarAdapter> _logger;

    private const string GraphApiBase = "https://graph.microsoft.com/v1.0";
    private const string OAuthTokenUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token";

    public MicrosoftGraphCalendarAdapter(
        IHttpClientFactory httpClientFactory,
        ICalendarConnectionRepository connectionRepo,
        ITokenEncryptionService tokenEncryption,
        ILogger<MicrosoftGraphCalendarAdapter> logger)
    {
        _httpClient = httpClientFactory.CreateClient("microsoft-graph");
        _connectionRepo = connectionRepo;
        _tokenEncryption = tokenEncryption;
        _logger = logger;
    }

    public async Task<List<TimeSlot>> GetAvailabilityAsync(
        Guid tenantId,
        DateOnly fechaInicio,
        DateOnly fechaFin,
        CancellationToken ct = default)
    {
        var connection = await GetValidConnectionAsync(tenantId, ct);
        var accessToken = _tokenEncryption.Decrypt(connection.AccessTokenEncrypted);

        var startDateTime = fechaInicio.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToString("o");
        var endDateTime = fechaFin.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc).ToString("o");

        var url = $"{GraphApiBase}/me/calendar/calendarView" +
                  $"?startDateTime={Uri.EscapeDataString(startDateTime)}" +
                  $"&endDateTime={Uri.EscapeDataString(endDateTime)}";

        var (json, _) = await SendWithRefreshAsync(tenantId, HttpMethod.Get, url, null, ct);

        var data = JsonSerializer.Deserialize<GraphEventsResponse>(json);
        if (data?.Value == null || data.Value.Count == 0)
            return new List<TimeSlot>();

        return data.Value
            .Where(e => e.Start != null && e.End != null)
            .Select(e => new TimeSlot
            {
                FechaInicio = DateTime.Parse(e.Start!.DateTime),
                FechaFin = DateTime.Parse(e.End!.DateTime),
                Disponible = false,
                ExternalEventId = e.Id
            })
            .ToList();
    }

    public async Task<string> CreateEventAsync(
        Appointment appointment,
        CancellationToken ct = default)
    {
        var eventBody = new
        {
            subject = $"Cita: {appointment.Notas ?? "Agendada"}",
            body = new { contentType = "text", content = appointment.Notas ?? "" },
            start = new { dateTime = appointment.FechaInicio.ToString("o"), timeZone = "UTC" },
            end = new { dateTime = appointment.FechaFin.ToString("o"), timeZone = "UTC" }
        };

        var url = $"{GraphApiBase}/me/calendar/events";
        var body = JsonSerializer.Serialize(eventBody);
        var (json, _) = await SendWithRefreshAsync(appointment.IdTenant, HttpMethod.Post, url, body, ct);

        var created = JsonSerializer.Deserialize<GraphEvent>(json);
        _logger.LogInformation("[MSGraph] Evento creado: {Id}", created?.Id);
        return created?.Id ?? $"msft_{appointment.IdAppointment}";
    }

    public async Task UpdateEventAsync(
        Appointment appointment,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(appointment.ExternalEventId))
            throw new InvalidOperationException("ExternalEventId no disponible");

        var eventBody = new
        {
            subject = $"Cita: {appointment.Notas ?? "Actualizada"}",
            start = new { dateTime = appointment.FechaInicio.ToString("o"), timeZone = "UTC" },
            end = new { dateTime = appointment.FechaFin.ToString("o"), timeZone = "UTC" }
        };

        var url = $"{GraphApiBase}/me/calendar/events/{appointment.ExternalEventId}";
        var body = JsonSerializer.Serialize(eventBody);
        var (_, _) = await SendWithRefreshAsync(appointment.IdTenant, HttpMethod.Patch, url, body, ct);
    }

    public async Task CancelEventAsync(Guid tenantId, string externalEventId, string? motivo, CancellationToken ct = default)
    {
        var url = $"{GraphApiBase}/me/calendar/events/{externalEventId}";
        var (_, _) = await SendWithRefreshAsync(tenantId, HttpMethod.Delete, url, null, ct);

        _logger.LogInformation("[MSGraph] Evento cancelado: {Id}", externalEventId);
    }

    // ─── Delta Sync ────────────────────────────────────────────

    public async Task<List<ExternalCalendarChange>> GetChangesAsync(Guid tenantId, string syncToken, CancellationToken ct = default)
    {
        // Si syncToken es una URL completa (nuevo formato: @odata.deltaLink completo),
        // úsala directamente. Si está vacío, empieza fresco. Si es un token corto
        // (old format, extraído de admin previo), reconstruye la URL.
        var url = string.IsNullOrEmpty(syncToken)
            ? $"{GraphApiBase}/me/calendar/events/delta"
            : syncToken.StartsWith("https://")
                ? syncToken
                : $"{GraphApiBase}/me/calendar/events/delta?$deltaToken={Uri.EscapeDataString(syncToken)}";

        var changes = new List<ExternalCalendarChange>();
        string? deltaLink = null;

        // Pueden haber múltiples páginas; seguir @odata.nextLink hasta el final
        while (!string.IsNullOrEmpty(url))
        {
            var (json, _) = await SendWithRefreshAsync(tenantId, HttpMethod.Get, url, null, ct);
            var data = JsonSerializer.Deserialize<GraphDeltaResponse>(json);

            if (data?.Value != null)
            {
                foreach (var item in data.Value)
                {
                    if (item.IsDeleted || item.Status == "cancelled")
                    {
                        changes.Add(new ExternalCalendarChange
                        {
                            ExternalEventId = item.Id,
                            Tipo = "deleted",
                            Summary = item.Subject
                        });
                    }
                    else
                    {
                        changes.Add(new ExternalCalendarChange
                        {
                            ExternalEventId = item.Id,
                            Tipo = "updated",
                            FechaInicio = item.Start != null ? DateTime.Parse(item.Start.DateTime) : null,
                            FechaFin = item.End != null ? DateTime.Parse(item.End.DateTime) : null,
                            Summary = item.Subject
                        });
                    }
                }
            }

            // Guardar el deltaLink de la última página para la próxima iteración
            if (!string.IsNullOrEmpty(data?.ODataDeltaLink))
                deltaLink = data.ODataDeltaLink;

            // Seguir paginación si hay @odata.nextLink
            url = data?.ODataNextLink ?? null!;
        }

        // Almacenar el deltaLink COMPLETO (URL completa) para la próxima consulta
        if (!string.IsNullOrEmpty(deltaLink))
        {
            await UpdateSyncTokenAsync(tenantId, deltaLink, ct);
            _logger.LogInformation("[MSGraph] DeltaLink actualizado (full URL)");
        }
        else
        {
            _logger.LogWarning("[MSGraph] Delta sync completado sin deltaLink — puede requerir resincronización completa");
        }

        _logger.LogInformation("[MSGraph] Delta sync: {Count} cambios", changes.Count);
        return changes;
    }

    // ─── Watch / Subscribe ─────────────────────────────────────

    public async Task<(string ChannelId, string? ResourceId, DateTime ExpiresAt)> SubscribeToChangesAsync(
        Guid tenantId, string webhookUrl, CancellationToken ct = default)
    {
        var connection = await GetValidConnectionAsync(tenantId, ct);
        var accessToken = _tokenEncryption.Decrypt(connection.AccessTokenEncrypted);

        var channelId = Guid.NewGuid().ToString();
        var expiration = DateTime.UtcNow.AddMinutes(4230); // MS permite máx ~4230 min (~3 días)

        var subBody = new
        {
            changeType = "created,updated,deleted",
            notificationUrl = webhookUrl,
            lifecycleNotificationUrl = webhookUrl,
            resource = "/me/calendar/events",
            expirationDateTime = expiration.ToString("o"),
            clientState = tenantId.ToString()
        };

        var url = $"{GraphApiBase}/subscriptions";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(subBody),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            accessToken = await RefreshAndUpdateTokenAsync(connection, ct);
            request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(subBody),
                Encoding.UTF8,
                "application/json");
            response = await _httpClient.SendAsync(request, ct);
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[MSGraph] Error creando subscription: {Status} - {Body}", response.StatusCode, json);
            throw new Exception($"Error creando subscription: {response.StatusCode}");
        }

        var subData = JsonSerializer.Deserialize<GraphSubscriptionResponse>(json);

        // Almacenar channelId en la conexión
        if (connection != null)
        {
            connection.SyncChannelId = channelId;
            connection.SyncResourceId = subData?.Id; // MS no tiene resourceId, usamos subscription ID
            connection.SyncChannelExpiresAt = expiration;
            connection.FechaActualizacion = DateTime.UtcNow;
            await _connectionRepo.UpdateAsync(connection, ct);
        }

        _logger.LogInformation("[MSGraph] Subscription creada: id={SubId}, channel={ChannelId}, exp={Expires}",
            subData?.Id, channelId, expiration);
        return (channelId, subData?.Id, expiration);
    }

    public async Task<string> RefreshAccessTokenAsync(Guid tenantId, string refreshToken, CancellationToken ct = default)
    {
        return await RefreshMicrosoftTokenAsync(refreshToken, ct);
    }

    // ─── Private Helpers ──────────────────────────────────────────

    private async Task<CalendarConnection> GetValidConnectionAsync(Guid tenantId, CancellationToken ct)
    {
        var connection = await _connectionRepo.GetByTenantIdAsync(tenantId, ct);
        if (connection == null || !connection.Activo)
            throw new InvalidOperationException("Conexión de calendario no configurada");
        return connection;
    }

    private async Task<(string json, int statusCode)> SendWithRefreshAsync(
        Guid tenantId, HttpMethod method, string url, string? body, CancellationToken ct)
    {
        var connection = await GetValidConnectionAsync(tenantId, ct);
        var accessToken = _tokenEncryption.Decrypt(connection.AccessTokenEncrypted);

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (method == HttpMethod.Get)
            request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        if (body != null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("[MSGraph] Token expirado, refrescando...");
            accessToken = await RefreshAndUpdateTokenAsync(connection, ct);

            request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (method == HttpMethod.Get)
                request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
            if (body != null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            response = await _httpClient.SendAsync(request, ct);
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return (json, (int)response.StatusCode);
    }

    private async Task<string> RefreshAndUpdateTokenAsync(CalendarConnection connection, CancellationToken ct)
    {
        var refreshToken = _tokenEncryption.Decrypt(connection.RefreshTokenEncrypted);
        var newAccessToken = await RefreshMicrosoftTokenAsync(refreshToken, ct);

        connection.AccessTokenEncrypted = _tokenEncryption.Encrypt(newAccessToken);
        connection.TokenExpiresAt = DateTime.UtcNow.AddHours(1);
        connection.FechaActualizacion = DateTime.UtcNow;
        await _connectionRepo.UpdateAsync(connection, ct);

        return newAccessToken;
    }

    private async Task UpdateSyncTokenAsync(Guid tenantId, string deltaToken, CancellationToken ct)
    {
        var connection = await _connectionRepo.GetByTenantIdAsync(tenantId, ct);
        if (connection != null)
        {
            connection.SyncToken = deltaToken;
            connection.FechaActualizacion = DateTime.UtcNow;
            await _connectionRepo.UpdateAsync(connection, ct);
        }
    }

    private async Task<string> RefreshMicrosoftTokenAsync(string refreshToken, CancellationToken ct)
    {
        var clientId = Environment.GetEnvironmentVariable("MicrosoftOAuth__ClientId")
                       ?? throw new InvalidOperationException("MicrosoftOAuth__ClientId no configurado");
        var clientSecret = Environment.GetEnvironmentVariable("MicrosoftOAuth__ClientSecret")
                           ?? throw new InvalidOperationException("MicrosoftOAuth__ClientSecret no configurado");

        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
            new KeyValuePair<string, string>("refresh_token", refreshToken),
            new KeyValuePair<string, string>("scope", "https://graph.microsoft.com/Calendars.ReadWrite")
        });

        var response = await _httpClient.PostAsync(OAuthTokenUrl, body, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var data = JsonSerializer.Deserialize<GraphTokenResponse>(json);

        _logger.LogInformation("[MSGraph] Token refrescado exitosamente");
        return data?.AccessToken ?? throw new InvalidOperationException("Error refrescando token de Microsoft");
    }

    // ─── DTOs ─────────────────────────────────────────────────────

    private class GraphEventsResponse
    {
        [JsonPropertyName("value")] public List<GraphEvent>? Value { get; set; }
    }

    private class GraphDeltaResponse
    {
        [JsonPropertyName("value")] public List<GraphDeltaEvent>? Value { get; set; }
        [JsonPropertyName("@odata.nextLink")] public string? ODataNextLink { get; set; }
        [JsonPropertyName("@odata.deltaLink")] public string? ODataDeltaLink { get; set; }
    }

    private class GraphEvent
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("subject")] public string? Subject { get; set; }
        [JsonPropertyName("start")] public GraphEventTime? Start { get; set; }
        [JsonPropertyName("end")] public GraphEventTime? End { get; set; }
    }

    private class GraphDeltaEvent
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("subject")] public string? Subject { get; set; }
        [JsonPropertyName("isDeleted")] public bool IsDeleted { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("start")] public GraphEventTime? Start { get; set; }
        [JsonPropertyName("end")] public GraphEventTime? End { get; set; }
    }

    private class GraphEventTime
    {
        [JsonPropertyName("dateTime")] public string DateTime { get; set; } = string.Empty;
        [JsonPropertyName("timeZone")] public string TimeZone { get; set; } = string.Empty;
    }

    private class GraphTokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private class GraphSubscriptionResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("expirationDateTime")] public string? ExpirationDateTime { get; set; }
    }
}
