namespace AgendaApi.Domain.Ports;

/// <summary>
/// Puerto para transcribir audio a texto (speech-to-text).
/// Permite que un cliente envíe un audio por WhatsApp y el flujo lo trate como un mensaje escrito.
/// Devuelve el texto transcrito, o null si no se pudo transcribir (audio vacío / no inteligible).
/// </summary>
public interface ISpeechToTextProvider
{
    /// <summary>
    /// Transcribe un audio (bytes) a texto.
    /// </summary>
    /// <param name="audioBytes">Bytes del audio (p. ej. ogg/opus de WhatsApp).</param>
    /// <param name="mimeType">MIME del audio (p. ej. audio/ogg, audio/mpeg).</param>
    Task<string?> TranscribeAsync(byte[] audioBytes, string mimeType, CancellationToken ct = default);
}