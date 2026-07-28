using AgendaApi.Infrastructure.Services;
using FluentAssertions;

namespace AgendaApi.Tests.Services;

public class TokenEncryptionServiceTests
{
    private const string ValidBase64Key = "Y7uM6gFkR2xX8pLz9qW3vN5mB0cV4sH1jK6tY8eA2wU="; // 32 bytes base64

    [Fact]
    public void EncryptDecrypt_RoundTrip_ReturnsOriginal()
    {
        // Arrange
        Environment.SetEnvironmentVariable("TokenEncryption__MasterKey", ValidBase64Key);
        var service = new TokenEncryptionService();
        var original = "Hello, this is a secret token!";

        // Act
        var encrypted = service.Encrypt(original);
        var decrypted = service.Decrypt(encrypted);

        // Assert
        decrypted.Should().Be(original);
        encrypted.Should().NotBe(original);
    }

    [Fact]
    public void EncryptDecrypt_WithLongString_ReturnsOriginal()
    {
        // Arrange
        Environment.SetEnvironmentVariable("TokenEncryption__MasterKey", ValidBase64Key);
        var service = new TokenEncryptionService();
        var original = string.Join("", Enumerable.Repeat("AccessTokenValueNeedsEncryption. ", 20));

        // Act
        var encrypted = service.Encrypt(original);
        var decrypted = service.Decrypt(encrypted);

        // Assert
        decrypted.Should().Be(original);
    }

    [Fact]
    public void Encrypt_ProducesBase64Output()
    {
        // Arrange
        Environment.SetEnvironmentVariable("TokenEncryption__MasterKey", ValidBase64Key);
        var service = new TokenEncryptionService();

        // Act
        var encrypted = service.Encrypt("test");

        // Assert
        encrypted.Should().MatchRegex("^[A-Za-z0-9+/=]+$");
    }

    [Fact]
    public void Encrypt_SameInput_ProducesDifferentOutput()
    {
        // Arrange
        Environment.SetEnvironmentVariable("TokenEncryption__MasterKey", ValidBase64Key);
        var service = new TokenEncryptionService();

        // Act
        var encrypted1 = service.Encrypt("same-value");
        var encrypted2 = service.Encrypt("same-value");

        // Assert
        encrypted1.Should().NotBe(encrypted2); // Different nonce → different ciphertext
    }

    [Fact]
    public void Decrypt_WithInvalidBase64_Throws()
    {
        // Arrange
        Environment.SetEnvironmentVariable("TokenEncryption__MasterKey", ValidBase64Key);
        var service = new TokenEncryptionService();

        // Act
        var act = () => service.Decrypt("not-base64-!!!");

        // Assert
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Decrypt_WithTamperedPayload_Throws()
    {
        // Arrange
        Environment.SetEnvironmentVariable("TokenEncryption__MasterKey", ValidBase64Key);
        var service = new TokenEncryptionService();
        var encrypted = service.Encrypt("important-data");

        // Tamper with the payload (change a character in the base64)
        var tampered = encrypted[..^5] + "XXXXX";

        // Act
        var act = () => service.Decrypt(tampered);

        // Assert
        act.Should().Throw<Exception>(); // GCM authentication should fail
    }

    [Fact]
    public void Encrypt_WhenMasterKeyNotSet_Throws()
    {
        // Arrange
        Environment.SetEnvironmentVariable("TokenEncryption__MasterKey", null);

        // Act
        var act = () => new TokenEncryptionService();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TokenEncryption__MasterKey*");
    }
}
