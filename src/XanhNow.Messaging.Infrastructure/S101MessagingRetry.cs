using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using XanhNow.Messaging.Domain;

namespace XanhNow.Messaging.Infrastructure;

internal static class S101MessagingRetry
{
    internal static IQueryable<MessageRecipient> AcknowledgedRecipients(IQueryable<MessageRecipient> recipients,
        Guid messageId, Guid userId) => recipients.Where(x => x.MessageId == messageId && x.UserId == userId &&
            (x.DeliveredAt != null || x.ReadAt != null));

    internal static void DeliveryDispatched(OutboxEvent row, bool payment, bool acknowledged, DateTimeOffset now)
    {
        // SignalR success is not a device receipt. Retry the same payment message until its owner ACKs.
        var awaitingReceipt = payment && !acknowledged;
        row.Status = awaitingReceipt ? OutboxStatus.Pending : OutboxStatus.Completed;
        row.NextAttemptAt = awaitingReceipt
            ? now.AddSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Min(row.AttemptCount, 4)))) : null;
        if (awaitingReceipt && row.AttemptCount < int.MaxValue) row.AttemptCount++;
        row.LeaseToken = null;
        row.LeaseExpiresAt = null;
        row.LastError = null;
        row.UpdatedAt = now;
    }

    internal static bool IsTransient(Exception exception) => exception is DbException or DbUpdateException or
        TimeoutException or IOException || exception.InnerException is { } inner && IsTransient(inner);

    internal static async Task ProcessUntilCommittedAsync(Func<CancellationToken, Task> process,
        Func<TimeSpan, CancellationToken, Task> delay, Action retrying, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { await process(ct); return; }
            catch (Exception error) when (!ct.IsCancellationRequested && IsTransient(error))
            {
                retrying();
                attempt = Math.Min(attempt + 1, 5);
                await delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))), ct);
            }
        }
    }

    internal static void DeliveryFailed(OutboxEvent row, bool payment, bool transient,
        DateTimeOffset now, int maxAttempts)
    {
        row.AttemptCount = row.AttemptCount < int.MaxValue ? row.AttemptCount + 1 : int.MaxValue;
        row.Status = payment || transient || row.AttemptCount < maxAttempts ? OutboxStatus.Pending : OutboxStatus.Failed;
        row.NextAttemptAt = now.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(row.AttemptCount, 9))));
        row.LeaseToken = null;
        row.LeaseExpiresAt = null;
        row.UpdatedAt = now;
    }
}
