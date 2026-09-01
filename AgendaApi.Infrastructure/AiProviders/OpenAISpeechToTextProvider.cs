using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Infrastructure.AiProviders;

/// <summary>
/// Transcribe audio a texto con OpenAI (Whisper, endpoint /audio/transcriptions).
/// Reutiliza la misma API key que <see cref="OpenAIProvider"/> (OpenAI__ApiKey / OPENAI_API_KEY),
/// ya validada y en uso por el proveedor principal de chat, evitando depender de una key de
/// Groq separada que estaba como placeholder y rompía la transcripción (Unauthorized).
/// Modelo por defecto: whisper-1 (compatible con el endpoint de transcripción de OpenAI).
/// </summary>
public class OpenAISpeechToTextProvider : ISpeechToTextProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAISpeechToTextProvider> _logger;
    private const string ApiUrl = "https://api.openai.com/v1/audio/transcriptions";
    private const string Model = "whisper-1";

    public OpenAISpeechToTextProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<OpenAISpeechToTextProvider> logger)
    {
        _httpClient = httpClientFactory.CreateClient("openai-api");
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
            _logger.LogError("[OpenAI STT] Error en API: {StatusCode} - {Response}", response.StatusCode, json);
            return null;
        }

        var data = JsonSerializer.Deserialize<OpenAiTranscriptionResponse>(json);
        var text = data?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return text;
    }

    /// <summary>Nombre de archivo en el multipart según el MIME (Whisper lo usa para elegir el decoder).</summary>
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
        var envKey = Environment.GetEnvironmentVariable("OpenAI__ApiKey");
        if (string.IsNullOrWhiteSpace(envKey))
            envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            // Misma defensa que OpenAIProvider: no gastar una llamada 401 si la key es placeholder.
            if (envKey.Contains("xxx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "OpenAI API Key parece ser un placeholder (contiene 'xxx'). Configurar una key real en OpenAI__ApiKey");
            return envKey;
        }

        throw new InvalidOperationException("No se encontró OpenAI API Key para STT. Configurar OpenAI__ApiKey");
    }

    private class OpenAiTranscriptionResponse
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}