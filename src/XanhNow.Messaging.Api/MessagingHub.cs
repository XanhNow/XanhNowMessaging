using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XanhNow.Messaging.Application;
using XanhNow.Messaging.Contracts;

namespace XanhNow.Messaging.Api;

[Authorize]
public sealed class MessagingHub(MailboxService mailboxService) : Hub
{
    public async Task AcknowledgeDelivered(Guid messageId)
    {
        if (!IdentityClaims.TryGetUserId(Context.User!, out var userId) ||
            !await mailboxService.MarkDeliveredAsync(userId, messageId, Context.ConnectionAborted))
        {
            throw new HubException("Message does not belong to the current user.");
        }
    }
}

public sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.FindFirst("sub")?.Value;
}

public sealed class SignalRRealtimeNotifier(IHubContext<MessagingHub> hubContext) : IRealtimeNotifier
{
    public Task SendAsync(
        Guid userId,
        RealtimeMessageNotification notification,
        CancellationToken cancellationToken) =>
        hubContext.Clients.User(userId.ToString("D"))
            .SendAsync("MessageCreated", notification, cancellationToken);
}

public sealed class DisabledPushGateway : IPushGateway
{
    public bool IsEnabled => false;

    public Task<PushDeliveryResult> SendAsync(
        Guid userId,
        RealtimeMessageNotification notification,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PushDeliveryResult(false, false, "disabled", "Push provider is not configured."));
}
