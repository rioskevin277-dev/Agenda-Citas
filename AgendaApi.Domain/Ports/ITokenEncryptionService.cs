namespace AgendaApi.Domain.Ports;

/// <summary>
/// Puerto para el servicio de cifrado de tokens OAuth en reposo.
/// Patrón: AES-256-GCM con clave derivada de una clave maestra configurable.
/// </summary>
public interface ITokenEncryptionService
{
    /// <summary>
    /// Cifra un token (access_token o refresh_token) para almacenamiento seguro.
    /// </summary>
    string Encrypt(string plainText);

    /// <summary>
    /// Descifra un token previamente cifrado.
    /// </summary>
    string Decrypt(string cipherText);
}
