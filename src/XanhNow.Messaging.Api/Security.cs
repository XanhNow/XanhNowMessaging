using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XanhNow.Messaging.Application;
using XanhNow.Messaging.Infrastructure;

namespace XanhNow.Messaging.Api;

public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";
    public bool RedisBackplaneEnabled { get; set; } = true;
    public string RedisConfigurationFile { get; set; } = string.Empty;
    public string RedisPasswordFile { get; set; } = string.Empty;
    public string ChannelPrefix { get; set; } = "s101:xanhnow:messaging";
}

public sealed class AesDeviceTokenProtector : IDeviceTokenProtector
{
    private readonly byte[] _key;

    public AesDeviceTokenProtector(IOptions<MessagingSecurityOptions> options)
    {
        var encoded = SecretFile.ReadRequired(
            options.Value.DeviceTokenEncryptionKeyFile,
            "Device token encryption key");
        _key = Convert.FromBase64String(encoded);
        if (_key.Length != 32)
            throw new InvalidOperationException(
                "Device token encryption key must be 32 bytes encoded as Base64.");
    }

    public string Protect(string token)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(token);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var output = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, output, nonce.Length + tag.Length,
            ciphertext.Length);
        return Convert.ToBase64String(output);
    }

    public string Unprotect(string protectedToken)
    {
        var input = Convert.FromBase64String(protectedToken);
        if (input.Length < 29)
            throw new CryptographicException("Protected token is invalid.");
        var nonce = input.AsSpan(0, 12);
        var tag = input.AsSpan(12, 16);
        var ciphertext = input.AsSpan(28);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
