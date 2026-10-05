using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using XanhNow.Messaging.Application;
using XanhNow.Messaging.Infrastructure;

namespace XanhNow.Messaging.Api;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";
    public string Issuer { get; set; } = "xanhnow-security";
    public string Audience { get; set; } = "xanhnow";
    public string SigningKeyFile { get; set; } = string.Empty;
    public string RedisConfigurationFile { get; set; } = string.Empty;
    public string RedisPasswordFile { get; set; } = string.Empty;
    public string SessionKeyPrefix { get; set; } = "xanhnow:security:cache:sessions";
    public string DeviceTokenEncryptionKeyFile { get; set; } = string.Empty;
}

public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";
    public bool RedisBackplaneEnabled { get; set; } = true;
    public string RedisConfigurationFile { get; set; } = string.Empty;
    public string RedisPasswordFile { get; set; } = string.Empty;
    public string ChannelPrefix { get; set; } = "s101:xanhnow:messaging";
}

public static class AuthenticationExtensions
{
    public static IServiceCollection AddXanhNowSecurity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.SectionName));
        services.AddSingleton<SecuritySessionValidator>();
        services.AddSingleton<IDeviceTokenProtector, AesDeviceTokenProtector>();
        var settings = configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>()
            ?? new SecurityOptions();
        var signingKey = SecretFile.ReadRequired(settings.SigningKeyFile, "Security JWT signing key");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = settings.Issuer,
                ValidateAudience = true,
                ValidAudience = settings.Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.Zero,
                NameClaimType = JwtRegisteredClaimNames.Sub
            };
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    if (!string.IsNullOrWhiteSpace(accessToken) &&
                        context.HttpContext.Request.Path.StartsWithSegments("/messagingHub"))
                    {
                        context.Token = accessToken;
                    }
                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    var sessionId = context.Principal?.FindFirst("sid")?.Value;
                    var tokenType = context.Principal?.FindFirst("typ")?.Value;
                    if (string.IsNullOrWhiteSpace(sessionId) ||
                        !string.Equals(tokenType, "access", StringComparison.Ordinal) ||
                        !await context.HttpContext.RequestServices
                            .GetRequiredService<SecuritySessionValidator>()
                            .IsActiveAsync(sessionId))
                    {
                        context.Fail("The Security session is not active.");
                    }
                }
            };
        });
        services.AddAuthorization();
        return services;
    }
}

public sealed class SecuritySessionValidator(
    IOptions<SecurityOptions> options,
    ILogger<SecuritySessionValidator> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConnectionMultiplexer? _connection;

    public async Task<bool> IsActiveAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }
        try
        {
            var database = await GetDatabaseAsync();
            var key = $"{options.Value.SessionKeyPrefix.TrimEnd(':')}:{sessionId.Trim()}";
            var value = await database.StringGetAsync(key);
            return value.HasValue && IsActivePayload(value.ToString());
        }
        catch (Exception exception) when (exception is RedisException or IOException or JsonException)
        {
            logger.LogWarning(exception, "Security session validation failed closed.");
            return false;
        }
    }

    internal static bool IsActivePayload(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty("status", out var status) &&
               status.ValueKind == JsonValueKind.String &&
               string.Equals(status.GetString(), "Active", StringComparison.Ordinal);
    }

    private async Task<IDatabase> GetDatabaseAsync()
    {
        if (_connection is { IsConnected: true })
        {
            return _connection.GetDatabase();
        }
        await _gate.WaitAsync();
        try
        {
            if (_connection is not { IsConnected: true })
            {
                if (_connection is not null)
                {
                    await _connection.DisposeAsync();
                }
                var configuration = ConfigurationOptions.Parse(
                    SecretFile.ReadRequired(options.Value.RedisConfigurationFile, "Redis configuration"));
                configuration.Password = SecretFile.ReadRequired(options.Value.RedisPasswordFile, "Redis password");
                configuration.AbortOnConnectFail = false;
                _connection = await ConnectionMultiplexer.ConnectAsync(configuration);
            }
            return _connection.GetDatabase();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
        _gate.Dispose();
    }
}

public sealed class AesDeviceTokenProtector : IDeviceTokenProtector
{
    private readonly byte[] _key;

    public AesDeviceTokenProtector(IOptions<SecurityOptions> options)
    {
        var encoded = SecretFile.ReadRequired(
            options.Value.DeviceTokenEncryptionKeyFile,
            "Device token encryption key");
        _key = Convert.FromBase64String(encoded);
        if (_key.Length != 32)
        {
            throw new InvalidOperationException("Device token encryption key must be 32 bytes encoded as Base64.");
        }
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
        Buffer.BlockCopy(ciphertext, 0, output, nonce.Length + tag.Length, ciphertext.Length);
        return Convert.ToBase64String(output);
    }

    public string Unprotect(string protectedToken)
    {
        var input = Convert.FromBase64String(protectedToken);
        if (input.Length < 29)
        {
            throw new CryptographicException("Protected token is invalid.");
        }
        var nonce = input.AsSpan(0, 12);
        var tag = input.AsSpan(12, 16);
        var ciphertext = input.AsSpan(28);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}

