using System.Security.Cryptography;
using AgendaApi.Domain.Ports;

namespace AgendaApi.Infrastructure.Services;

/// <summary>
/// Servicio de cifrado AES-256-GCM para tokens OAuth en reposo.
/// Usa una clave maestra de 256 bits desde configuración (o entorno).
///
/// Formato del ciphertext: base64(iv + tag + ciphertext)
/// - IV: 12 bytes (nonce GCM)
/// - Tag: 16 bytes (authentication tag)
/// - Ciphertext: variable
/// </summary>
public class TokenEncryptionService : ITokenEncryptionService
{
    private readonly byte[] _masterKey;

    public TokenEncryptionService()
    {
        // Leer clave maestra desde variable de entorno
        var keyBase64 = Environment.GetEnvironmentVariable("TokenEncryption__MasterKey")
                        ?? throw new InvalidOperationException(
                            "TokenEncryption__MasterKey no configurada. " +
                            "Generar con: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))");

        _masterKey = Convert.FromBase64String(keyBase64);

        if (_masterKey.Length != 32)
            throw new InvalidOperationException(
                $"La clave maestra debe tener 32 bytes (256 bits). Longitud actual: {_masterKey.Length}");
    }

    public string Encrypt(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);

        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
        var iv = RandomNumberGenerator.GetBytes(12); // GCM nonce de 12 bytes
        var tag = new byte[16];
        var ciphertext = new byte[plainBytes.Length];

        using var aes = new AesGcm(_masterKey, 16);
        aes.Encrypt(iv, plainBytes, ciphertext, tag);

        // Combinar iv + tag + ciphertext
        var result = new byte[iv.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
        Buffer.BlockCopy(tag, 0, result, iv.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, iv.Length + tag.Length, ciphertext.Length);

        return Convert.ToBase64String(result);
    }

    public string Decrypt(string cipherText)
    {
        ArgumentNullException.ThrowIfNull(cipherText);

        var data = Convert.FromBase64String(cipherText);

        if (data.Length < 12 + 16)
            throw new InvalidOperationException("Formato de ciphertext invalido");

        var iv = data.AsSpan(0, 12);
        var tag = data.AsSpan(12, 16);
        var ciphertext = data.AsSpan(12 + 16);

        var plainBytes = new byte[ciphertext.Length];

        using var aes = new AesGcm(_masterKey, 16);
        aes.Decrypt(iv, ciphertext, tag, plainBytes);

        return System.Text.Encoding.UTF8.GetString(plainBytes);
    }
}
