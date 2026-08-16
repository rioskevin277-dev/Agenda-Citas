using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Infrastructure.AiProviders;

/// <summary>
/// Transcribe audio a texto con Groq (Whisper), compatible con la API de OpenAI
/// (/audio/transcriptions). Mismo patrón de clave que <see cref="GroqProvider"/>
/// (Groq__ApiKey / GROQ_API_KEY).
/// Modelo por defecto: whisper-large-v3-turbo (rápido y barato).
/// </summary>
public class GroqSpeechToTextProvider : ISpeechToTextProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GroqSpeechToTextProvider> _logger;
    private const string ApiUrl = "https://api.groq.com/openai/v1/audio/transcriptions";
    private const string Model = "whisper-large-v3-turbo";

    public GroqSpeechToTextProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<GroqSpeechToTextProvider> logger)
    {
        _httpClient = httpClientFactory.CreateClient("groq-api");
        _logger = logger;
    }

    public async Task<string?> TranscribeAsync(byte[] audioBytes, string mimeType, CancellationToken ct = default)
    {
        if (audioBytes == null || audioBytes.Length == 0)
            return null;

        var apiKey = GetApiKey();

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audioBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(ResolveMediaType(mimeType));
        form.Add(fileContent, "file", ResolveFileName(mimeType));
        form.Add(new StringContent(Model), "model");
        form.Add(new StringContent("es"), "language"); // apps hispanas por defecto (Whisper igual auto-detecta)

        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = form;

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[GroqSTT] Error en API: {StatusCode} - {Response}", response.StatusCode, json);
            return null;
        }

        var data = JsonSerializer.Deserialize<GroqTranscriptionResponse>(json);
        var text = data?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return text;
    }

    /// <summary>Nombre de archivo en el multipart según el MIME (Groq lo usa para elegir el decoder).</summary>
    private static string ResolveFileName(string mimeType)
    {
        // Quitar parámetros de MIME ("audio/ogg; codecs=opus" → "audio/ogg") antes de elegir.
        var baseType = mimeType?.Split(';')[0].Trim().ToLowerInvariant();
        return baseType switch
        {
            "audio/mpeg" or "audio/mp3" => "voice.mp3",
            "audio/mp4" or "audio/aac" or "audio/x-m4a" => "voice.m4a",
            "audio/wav" or "audio/x-wav" or "audio/wave" => "voice.wav",
            "audio/webm" => "voice.webm",
            "audio/mp4a-latm" => "voice.m4a",
            // WhatsApp envía voz como ogg/opus; es el formato estándar
            _ => "voice.ogg"
        };
    }

    private static string ResolveMediaType(string mimeType)
    {
        // Graph devuelve el MIME con parámetros, p. ej. "audio/ogg; codecs=opus".
        // MediaTypeHeaderValue no acepta la parte de parámetros, así que nos quedamos
        // con el tipo base antes del ';'.
        var baseType = mimeType?.Split(';')[0].Trim();
        return string.IsNullOrWhiteSpace(baseType) ? "audio/ogg" : baseType;
    }

    private string GetApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("Groq__ApiKey");
        if (string.IsNullOrWhiteSpace(envKey))
            envKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
            return envKey;

        throw new InvalidOperationException("No se encontró Groq API Key para STT. Configurar Groq__ApiKey");
    }

    private class GroqTranscriptionResponse
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}