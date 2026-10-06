using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using XanhNow.Messaging.Application;

namespace XanhNow.Messaging.Api;

public sealed class MessagingSecurityOptions
{
    public const string SectionName = "SecurityBoundary";
    public string RequiredCallerService { get; set; } = "XanhNow_Security_App";
    public string ServiceApiKeyFile { get; set; } = string.Empty;
    public string DeviceTokenEncryptionKeyFile { get; set; } = string.Empty;
}

public sealed class MessagingSecurityBoundaryAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "XanhNowMessagingSecurityBoundary";
    private readonly MessagingSecurityOptions _settings;

    public MessagingSecurityBoundaryAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<MessagingSecurityOptions> settings)
        : base(options, logger, encoder) => _settings = settings.Value;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var caller = Request.Headers["X-Service-Name"].FirstOrDefault()?.Trim();
        var suppliedKey = Request.Headers["X-Service-Api-Key"].FirstOrDefault()?.Trim();
        if (!string.Equals(caller, _settings.RequiredCallerService,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(suppliedKey) ||
            !File.Exists(_settings.ServiceApiKeyFile))
            return AuthenticateResult.Fail(
                "Security boundary service authentication failed.");

        var expectedKey = (await File.ReadAllTextAsync(
            _settings.ServiceApiKeyFile, Context.RequestAborted)).Trim();
        if (!FixedTimeEquals(suppliedKey, expectedKey))
            return AuthenticateResult.Fail(
                "Security boundary service authentication failed.");

        var userIdText = Request.Headers["X-XanhNow-UserId"].FirstOrDefault()?.Trim();
        var phone = Request.Headers["X-XanhNow-PhoneNumber"].FirstOrDefault()?.Trim();
        var sessionId = Request.Headers["X-XanhNow-SessionId"].FirstOrDefault()?.Trim();
        if (!Guid.TryParse(userIdText, out var userId) || userId == Guid.Empty ||
            !TryNormalizePhone(phone, out var phoneNumber) ||
            string.IsNullOrWhiteSpace(sessionId))
            return AuthenticateResult.Fail(
                "Security boundary identity snapshot is invalid.");

        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim("sub", userId.ToString("D")));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString("D")));
        identity.AddClaim(new Claim("phone", phoneNumber));
        identity.AddClaim(new Claim("phone_number", phoneNumber));
        identity.AddClaim(new Claim("sid", sessionId));
        identity.AddClaim(new Claim("typ", "security-boundary"));
        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(identity), SchemeName));
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    internal static bool TryNormalizePhone(string? value, out string phoneNumber)
    {
        phoneNumber = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('*')) return false;
        var normalized = value.Trim().Replace(" ", "").Replace("-", "");
        if (normalized.StartsWith("84", StringComparison.Ordinal))
            normalized = "+" + normalized;
        if (normalized.StartsWith("0", StringComparison.Ordinal))
            normalized = "+84" + normalized[1..];
        if (!normalized.StartsWith('+') || normalized.Length is < 9 or > 16 ||
            normalized[1..].Any(character => !char.IsDigit(character)))
            return false;
        phoneNumber = normalized;
        return true;
    }
}

public static class MessagingSecurityBoundaryAuthentication
{
    public static IServiceCollection AddMessagingSecurityBoundaryAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<MessagingSecurityOptions>()
            .Bind(configuration.GetSection(MessagingSecurityOptions.SectionName))
            .Validate(x => !string.IsNullOrWhiteSpace(x.RequiredCallerService),
                "SecurityBoundary:RequiredCallerService is required.")
            .Validate(x => !string.IsNullOrWhiteSpace(x.ServiceApiKeyFile),
                "SecurityBoundary:ServiceApiKeyFile is required.")
            .Validate(x => !string.IsNullOrWhiteSpace(
                    x.DeviceTokenEncryptionKeyFile),
                "SecurityBoundary:DeviceTokenEncryptionKeyFile is required.")
            .ValidateOnStart();
        services.AddSingleton<IDeviceTokenProtector, AesDeviceTokenProtector>();
        services.AddAuthentication(
                MessagingSecurityBoundaryAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions,
                MessagingSecurityBoundaryAuthenticationHandler>(
                MessagingSecurityBoundaryAuthenticationHandler.SchemeName,
                _ => { });
        services.AddAuthorization();
        return services;
    }
}
