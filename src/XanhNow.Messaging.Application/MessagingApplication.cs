using XanhNow.Messaging.Contracts;

namespace XanhNow.Messaging.Application;

public interface IMailboxRepository
{
    Task<MessagePage> GetPageAsync(
        Guid userId,
        string? cursor,
        int pageSize,
        string? category,
        bool? unreadOnly,
        CancellationToken cancellationToken);

    Task<MessageDetail?> GetAsync(Guid userId, Guid messageId, CancellationToken cancellationToken);
    Task<long> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> MarkReadAsync(Guid userId, Guid messageId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> MarkDeliveredAsync(Guid userId, Guid messageId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<int> MarkAllReadAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IDeviceRegistry
{
    Task<DeviceResponse> RegisterAsync(
        Guid userId,
        RegisterDeviceRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<bool> RevokeAsync(Guid userId, Guid deviceId, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IDeviceTokenProtector
{
    string Protect(string token);
    string Unprotect(string protectedToken);
}

public interface IRealtimeNotifier
{
    Task SendAsync(Guid userId, RealtimeMessageNotification notification, CancellationToken cancellationToken);
}

public sealed record PushDeliveryResult(bool Accepted, bool TokenInvalid, string? ProviderCode, string? Error);

public interface IPushGateway
{
    bool IsEnabled { get; }
    Task<PushDeliveryResult> SendAsync(
        Guid userId,
        RealtimeMessageNotification notification,
        CancellationToken cancellationToken);
}

public sealed class MailboxService(IMailboxRepository repository, TimeProvider timeProvider)
{
    private const int MaxPageSize = 100;

    public Task<MessagePage> GetPageAsync(
        Guid userId,
        string? cursor,
        int pageSize,
        string? category,
        bool? unreadOnly,
        CancellationToken cancellationToken)
    {
        var safePageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        return repository.GetPageAsync(userId, cursor, safePageSize, category, unreadOnly, cancellationToken);
    }

    public Task<MessageDetail?> GetAsync(Guid userId, Guid messageId, CancellationToken cancellationToken) =>
        repository.GetAsync(userId, messageId, cancellationToken);

    public Task<long> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken) =>
        repository.GetUnreadCountAsync(userId, cancellationToken);

    public Task<bool> MarkReadAsync(Guid userId, Guid messageId, CancellationToken cancellationToken) =>
        repository.MarkReadAsync(userId, messageId, timeProvider.GetUtcNow(), cancellationToken);

    public Task<bool> MarkDeliveredAsync(Guid userId, Guid messageId, CancellationToken cancellationToken) =>
        repository.MarkDeliveredAsync(userId, messageId, timeProvider.GetUtcNow(), cancellationToken);

    public Task<int> MarkAllReadAsync(Guid userId, CancellationToken cancellationToken) =>
        repository.MarkAllReadAsync(userId, timeProvider.GetUtcNow(), cancellationToken);
}

public static class IdentityClaims
{
    public static bool TryGetUserId(System.Security.Claims.ClaimsPrincipal principal, out Guid userId)
    {
        var value = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(value, out userId);
    }
}
