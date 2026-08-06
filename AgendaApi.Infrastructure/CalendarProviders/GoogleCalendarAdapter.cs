using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Infrastructure.CalendarProviders;

public class GoogleCalendarAdapter : ICalendarProvider
{
    public string ProviderName => "google";

    private readonly HttpClient _httpClient;
    private readonly ICalendarConnectionRepository _connectionRepo;
    private readonly ITokenEncryptionService _tokenEncryption;
    private readonly ILogger<GoogleCalendarAdapter> _logger;

    private const string CalendarApiBase = "https://www.googleapis.com/calendar/v3";
    private const string OAuthTokenUrl = "https://oauth2.googleapis.com/token";
    private const string DefaultCalendarId = "primary";

    // Zona horaria del negocio (Colombia por defecto, UTC-5, sin horario de verano).
    // Internamente la app guarda las horas locales tratadas como si fueran UTC
    // (las reglas 09:00–18:00 se almacenan como 09:00Z). Google Calendar, en cambio,
    // trabaja con instantes UTC reales. Por eso hay que convertir en la frontera:
    // instante real (Google) -> hora local al leer, y hora local -> instante real
    // al crear/actualizar eventos. Configurable vía "Calendar__TimeZone".
    private static readonly TimeZoneInfo BusinessTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(
            Environment.GetEnvironmentVariable("Calendar__TimeZone") ?? "America/Bogota");

    /// <summary>
    /// Google devuelve instantes UTC (o con offset). Convertimos ese instante absoluto
    /// a la hora local del negocio, pero la guardamos "disfrazada de UTC" para que cuadre
    /// con el resto de la app (que trata horas locales como UTC). Retorna un DateTime
    /// cuyo valor es la hora local, con Kind=Utc.
    /// </summary>
    private static DateTime FromGoogleInstant(string? dateTime, string? dateOnly)
    {
        if (!string.IsNullOrEmpty(dateTime))
        {
            // DateTime.Parse interpreta el offset del string y devuelve el instante
            // en la zona local de la máquina (el contenedor corre en UTC).
            var instant = DateTime.Parse(dateTime);
            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(instant, DateTimeKind.Utc), BusinessTimeZone);
            return DateTime.SpecifyKind(local, DateTimeKind.Utc);
        }

        if (!string.IsNullOrEmpty(dateOnly))
            return DateOnly.Parse(dateOnly).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        return DateTime.MinValue;
    }

    /// <summary>
    /// Convierte una fecha interna de la app (hora local "disfrazada de UTC") al instante
    /// UTC real que espera Google. Se usa al crear o actualizar eventos.
    /// </summary>
    private static string LocalDateTimeToGoogleIso(DateTime local)
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(local, DateTimeKind.Unspecified), BusinessTimeZone);
        return utc.ToString("o");
    }
        public GoogleCalendarAdapter(
        IHttpClientFactory httpClientFactory,
        ICalendarConnectionRepository connectionRepo,
        ITokenEncryptionService tokenEncryption,
        ILogger<GoogleCalendarAdapter> logger)
    {
        _httpClient = httpClientFactory.CreateClient("google-calendar");
        _connectionRepo = connectionRepo;
        _tokenEncryption = tokenEncryption;
        _logger = logger;
    }

    public async Task<List<TimeSlot>> GetAvailabilityAsync(
        Guid tenantId, DateOnly fechaInicio, DateOnly fechaFin, CancellationToken ct = default)
    {
        var (accessToken, calendarId) = await GetAuthAsync(tenantId, ct);
        var timeMin = fechaInicio.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToString("o");
        var timeMax = fechaFin.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc).ToString("o");

        var url = $"{CalendarApiBase}/calendars/{Uri.EscapeDataString(calendarId)}/events" +
                  $"?timeMin={Uri.EscapeDataString(timeMin)}" +
                  $"&timeMax={Uri.EscapeDataString(timeMax)}" +
                  $"&singleEvents=true&orderBy=startTime";

        var (json, status) = await SendWithRefreshAsync(tenantId, HttpMethod.Get, url, null, ct);
        if (status == 401) (json, status) = await RetryWithRefreshAsync(tenantId, HttpMethod.Get, url, null, ct);

        var data = JsonSerializer.Deserialize<GoogleEventsResponse>(json);
        if (data?.Items == null) return new List<TimeSlot>();

        return data.Items
            .Where(e => e.Start != null && e.End != null)
            .Select(e => new TimeSlot
            {
                FechaInicio = FromGoogleInstant(e.Start!.DateTime, e.Start.Date),
                FechaFin = FromGoogleInstant(e.End!.DateTime, e.End.Date),
                Disponible = false,
                ExternalEventId = e.Id
            })
            .ToList();
    }

    public async Task<string> CreateEventAsync(Appointment appointment, CancellationToken ct = default)
    {
        var (accessToken, calendarId) = await GetAuthAsync(appointment.IdTenant, ct);
        var body = JsonSerializer.Serialize(new
        {
            summary = $"Cita: {appointment.Notas ?? "Agendada"}",
            description = appointment.Notas ?? "",
            start = new { dateTime = LocalDateTimeToGoogleIso(appointment.FechaInicio), timeZone = "UTC" },
            end = new { dateTime = LocalDateTimeToGoogleIso(appointment.FechaFin), timeZone = "UTC" }
        });

        var url = $"{CalendarApiBase}/calendars/{Uri.EscapeDataString(calendarId)}/events";
        var (json, status) = await SendWithRefreshAsync(appointment.IdTenant, HttpMethod.Post, url, body, ct);
        if (status == 401) (json, status) = await RetryWithRefreshAsync(appointment.IdTenant, HttpMethod.Post, url, body, ct);

        var created = JsonSerializer.Deserialize<GoogleEvent>(json);
        _logger.LogInformation("[GoogleCalendar] Evento creado: {Id}", created?.Id);
        return created?.Id ?? $"google_{appointment.IdAppointment}";
    }

    public async Task UpdateEventAsync(Appointment appointment, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(appointment.ExternalEventId))
            throw new InvalidOperationException("ExternalEventId no disponible");

        var (accessToken, calendarId) = await GetAuthAsync(appointment.IdTenant, ct);
        var body = JsonSerializer.Serialize(new
        {
            summary = $"Cita: {appointment.Notas ?? "Actualizada"}",
            start = new { dateTime = LocalDateTimeToGoogleIso(appointment.FechaInicio), timeZone = "UTC" },
            end = new { dateTime = LocalDateTimeToGoogleIso(appointment.FechaFin), timeZone = "UTC" }
        });

        var url = $"{CalendarApiBase}/calendars/{Uri.EscapeDataString(calendarId)}/events/{appointment.ExternalEventId}";
        var (_, status) = await SendWithRefreshAsync(appointment.IdTenant, HttpMethod.Put, url, body, ct);
        if (status == 401) (_, status) = await RetryWithRefreshAsync(appointment.IdTenant, HttpMethod.Put, url, body, ct);
    }

    public async Task CancelEventAsync(Guid tenantId, string externalEventId, string? motivo, CancellationToken ct = default)
    {
        var (_, calendarId) = await GetAuthAsync(tenantId, ct);
        var url = $"{CalendarApiBase}/calendars/{Uri.EscapeDataString(calendarId)}/events/{externalEventId}";
        var (_, status) = await SendWithRefreshAsync(tenantId, HttpMethod.Delete, url, null, ct);
        if (status == 401) (_, status) = await RetryWithRefreshAsync(tenantId, HttpMethod.Delete, url, null, ct);

        _logger.LogInformation("[GoogleCalendar] Evento cancelado: {Id}", externalEventId);
    }

    // ─── Delta Sync ────────────────────────────────────────────

    public async Task<List<ExternalCalendarChange>> GetChangesAsync(Guid tenantId, string syncToken, CancellationToken ct = default)
    {
        var (accessToken, calendarId) = await GetAuthAsync(tenantId, ct);

        // Sin syncToken (primer delta) omitimos el parámetro para que Google haga un full
// sync y devuelva el nextSyncToken inicial. Con token vacío explícito Google responde 400.
var url = $"{CalendarApiBase}/calendars/{Uri.EscapeDataString(calendarId)}/events" +
          (string.IsNullOrEmpty(syncToken)
              ? "?singleEvents=true"
              : $"?syncToken={Uri.EscapeDataString(syncToken)}&singleEvents=true");

        var (json, status) = await SendWithRefreshAsync(tenantId, HttpMethod.Get, url, null, ct);
        if (status == 401) (json, status) = await RetryWithRefreshAsync(tenantId, HttpMethod.Get, url, null, ct);

        var data = JsonSerializer.Deserialize<GoogleEventsResponse>(json);
        var changes = new List<ExternalCalendarChange>();

        if (data?.Items == null) return changes;

        foreach (var item in data.Items)
        {
            // Google marca eventos eliminados con "status": "cancelled"
            if (item.Status == "cancelled")
            {
                changes.Add(new ExternalCalendarChange
                {
                    ExternalEventId = item.Id,
                    Tipo = "deleted",
                    Summary = item.Summary
                });
            }
            else
            {
                changes.Add(new ExternalCalendarChange
                {
                    ExternalEventId = item.Id,
                    Tipo = "updated",
                    FechaInicio = item.Start != null ? FromGoogleInstant(item.Start.DateTime, item.Start.Date) : null,
                    FechaFin = item.End != null ? FromGoogleInstant(item.End.DateTime, item.End.Date) : null,
                    Summary = item.Summary
                });
            }
        }

        // Guardar nextSyncToken para próxima vez
        if (!string.IsNullOrEmpty(data.NextSyncToken))
        {
            await UpdateSyncTokenAsync(tenantId, data.NextSyncToken, ct);
            _logger.LogInformation("[GoogleCalendar] SyncToken actualizado: {Token}", data.NextSyncToken[..Math.Min(50, data.NextSyncToken.Length)]);
        }

        _logger.LogInformation("[GoogleCalendar] Delta sync: {Count} cambios desde último token", changes.Count);
        return changes;
    }

    // ─── Watch / Subscribe ─────────────────────────────────────

    public async Task<(string ChannelId, string? ResourceId, DateTime ExpiresAt)> SubscribeToChangesAsync(
        Guid tenantId, string webhookUrl, CancellationToken ct = default)
    {
        var (accessToken, calendarId) = await GetAuthAsync(tenantId, ct);

        var channelId = Guid.NewGuid().ToString();
        var body = JsonSerializer.Serialize(new
        {
            id = channelId,
            type = "webhook",
            address = webhookUrl,
            token = tenantId.ToString()
        });

        var url = $"{CalendarApiBase}/calendars/{Uri.EscapeDataString(calendarId)}/events/watch";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[GoogleCalendar] Error creando watch: {Status} - {Body}", response.StatusCode, json);
            throw new Exception($"Error creando watch channel: {response.StatusCode}");
        }

        var watchData = JsonSerializer.Deserialize<GoogleWatchResponse>(json);
        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(watchData?.Expiration ?? 0).UtcDateTime;

        // Almacenar channelId y resourceId en la conexión
        var connection = await _connectionRepo.GetByTenantIdAsync(tenantId, ct);
        if (connection != null)
        {
            connection.SyncChannelId = channelId;
            connection.SyncResourceId = watchData?.ResourceId;
            connection.SyncChannelExpiresAt = expiresAt;
            connection.FechaActualizacion = DateTime.UtcNow;
            await _connectionRepo.UpdateAsync(connection, ct);
        }

        _logger.LogInformation("[GoogleCalendar] Watch creado: channel={ChannelId}, resource={ResourceId}, exp={Expires}", channelId, watchData?.ResourceId, expiresAt);
        return (channelId, watchData?.ResourceId, expiresAt);
    }

    public async Task<string> RefreshAccessTokenAsync(Guid tenantId, string refreshToken, CancellationToken ct = default)
    {
        return await ExchangeRefreshTokenAsync(refreshToken, ct);
    }

    // ─── Private Helpers ───────────────────────────────────────

    private async Task<(string accessToken, string calendarId)> GetAuthAsync(Guid tenantId, CancellationToken ct)
    {
        var connection = await _connectionRepo.GetByTenantIdAsync(tenantId, ct)
            ?? throw new InvalidOperationException("Conexión de calendario no configurada para este tenant");
        if (!connection.Activo) throw new InvalidOperationException("Conexión de calendario inactiva");

        var accessToken = _tokenEncryption.Decrypt(connection.AccessTokenEncrypted);
        return (accessToken, connection.CalendarId ?? DefaultCalendarId);
    }

    private async Task<(string json, int statusCode)> SendWithRefreshAsync(
        Guid tenantId, HttpMethod method, string url, string? body, CancellationToken ct)
    {
        var connection = await _connectionRepo.GetByTenantIdAsync(tenantId, ct);
        if (connection == null) return ("", 404);

        var accessToken = _tokenEncryption.Decrypt(connection.AccessTokenEncrypted);
        var request = BuildRequest(method, url, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return (json, (int)response.StatusCode);
    }

    private async Task<(string json, int statusCode)> RetryWithRefreshAsync(
        Guid tenantId, HttpMethod method, string url, string? body, CancellationToken ct)
    {
        var connection = await _connectionRepo.GetByTenantIdAsync(tenantId, ct);
        if (connection == null) return ("", 404);

        var refreshToken = _tokenEncryption.Decrypt(connection.RefreshTokenEncrypted);
        var newAccessToken = await ExchangeRefreshTokenAsync(refreshToken, ct);

        connection.AccessTokenEncrypted = _tokenEncryption.Encrypt(newAccessToken);
        connection.TokenExpiresAt = DateTime.UtcNow.AddHours(1);
        connection.FechaActualizacion = DateTime.UtcNow;
        await _connectionRepo.UpdateAsync(connection, ct);

        var request = BuildRequest(method, url, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newAccessToken);

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return (json, (int)response.StatusCode);
    }

    private async Task UpdateSyncTokenAsync(Guid tenantId, string syncToken, CancellationToken ct)
    {
        var connection = await _connectionRepo.GetByTenantIdAsync(tenantId, ct);
        if (connection != null)
        {
            connection.SyncToken = syncToken;
            connection.FechaActualizacion = DateTime.UtcNow;
            await _connectionRepo.UpdateAsync(connection, ct);
        }
    }

    private async Task<string> ExchangeRefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        var clientId = Environment.GetEnvironmentVariable("GoogleOAuth__ClientId")
            ?? throw new InvalidOperationException("GoogleOAuth__ClientId no configurado");
        var clientSecret = Environment.GetEnvironmentVariable("GoogleOAuth__ClientSecret")
            ?? throw new InvalidOperationException("GoogleOAuth__ClientSecret no configurado");

        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
            new KeyValuePair<string, string>("refresh_token", refreshToken)
        });

        var response = await _httpClient.PostAsync(OAuthTokenUrl, body, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var data = JsonSerializer.Deserialize<GoogleTokenResponse>(json);

        _logger.LogInformation("[GoogleCalendar] Token refrescado exitosamente");
        return data?.AccessToken ?? throw new InvalidOperationException("Error refrescando token de Google");
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url, string? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (body != null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    // ─── DTOs ───────────────────────────────────────────────────

    private class GoogleEventsResponse
    {
        [JsonPropertyName("items")] public List<GoogleEvent>? Items { get; set; }
        [JsonPropertyName("nextSyncToken")] public string? NextSyncToken { get; set; }
    }

    private class GoogleEvent
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("start")] public GoogleEventTime? Start { get; set; }
        [JsonPropertyName("end")] public GoogleEventTime? End { get; set; }
    }

    private class GoogleEventTime
    {
        [JsonPropertyName("dateTime")] public string? DateTime { get; set; }
        [JsonPropertyName("date")] public string? Date { get; set; }
    }

    private class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private class GoogleWatchResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("resourceId")] public string ResourceId { get; set; } = string.Empty;
        [JsonPropertyName("expiration")] public long Expiration { get; set; }
    }
}
