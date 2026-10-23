using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using XanhNow.Messaging.Contracts;
using XanhNow.Messaging.Domain;
using XanhNow.Messaging.Infrastructure;
using Xunit;

namespace XanhNow.Messaging.Tests;

public sealed class PaymentNotificationRetryTests
{
    [Fact]
    public void OnlyTheMatchingOwnersDeliveredOrReadReceiptAcknowledgesTheMessage()
    {
        var id = Guid.NewGuid(); var user = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var receipts = new List<MessageRecipient>
        {
            new() { MessageId = id, UserId = Guid.NewGuid(), DeliveredAt = now },
            new() { MessageId = Guid.NewGuid(), UserId = user, ReadAt = now }
        };
        Assert.Empty(S101MessagingRetry.AcknowledgedRecipients(receipts.AsQueryable(), id, user));
        var owner = new MessageRecipient { MessageId = id, UserId = user, ReadAt = now };
        receipts.Add(owner);
        Assert.Same(owner, Assert.Single(S101MessagingRetry.AcknowledgedRecipients(receipts.AsQueryable(), id, user)));
        owner.ReadAt = null;
        owner.DeliveredAt = now;
        Assert.Same(owner, Assert.Single(S101MessagingRetry.AcknowledgedRecipients(receipts.AsQueryable(), id, user)));
    }

    [Fact]
    public void OfflinePaymentDispatchStaysPendingWithTheSameMessageBeyondTheRetryLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var row = new OutboxEvent
        {
            Id = Guid.NewGuid(), MessageId = Guid.NewGuid(), UserId = Guid.NewGuid(), Channel = "realtime",
            Status = OutboxStatus.Processing, AttemptCount = 100, LeaseToken = Guid.NewGuid(),
            LastError = "previous-transport-error"
        };
        var messageId = row.MessageId;
        for (var i = 0; i < 3; i++)
        {
            S101MessagingRetry.DeliveryDispatched(row, payment: true, acknowledged: false, now);
            Assert.Equal(OutboxStatus.Pending, row.Status);
            Assert.Equal(messageId, row.MessageId);
            Assert.Equal(now.AddMinutes(5), row.NextAttemptAt);
            Assert.Null(row.LeaseToken);
            Assert.Null(row.LastError);
            now = now.AddMinutes(5);
        }
        Assert.Equal(103, row.AttemptCount);
    }

    [Fact]
    public void AcknowledgedPaymentCompletesAndClearsRetryState()
    {
        var now = DateTimeOffset.UtcNow;
        var row = new OutboxEvent { Channel = "realtime", LeaseToken = Guid.NewGuid(), AttemptCount = 9 };
        S101MessagingRetry.DeliveryDispatched(row, payment: true, acknowledged: false, now);
        S101MessagingRetry.DeliveryDispatched(row, payment: true, acknowledged: true, now.AddSeconds(1));
        Assert.Equal(OutboxStatus.Completed, row.Status);
        Assert.Null(row.NextAttemptAt);
        Assert.Null(row.LeaseToken);
        Assert.Equal(10, row.AttemptCount);
    }

    [Fact]
    public void ReceiptRetryStartsAtThirtySecondsAndBacksOffWithoutFailingThePayment()
    {
        var now = DateTimeOffset.UtcNow;
        var row = new OutboxEvent { Channel = "realtime" };
        foreach (var seconds in new[] { 30, 60, 120, 240, 300 })
        {
            S101MessagingRetry.DeliveryDispatched(row, payment: true, acknowledged: false, now);
            Assert.Equal(now.AddSeconds(seconds), row.NextAttemptAt);
            Assert.Equal(OutboxStatus.Pending, row.Status);
        }
    }

    [Fact]
    public void NonPaymentDispatchRetainsItsExistingCompletionBehavior()
    {
        var row = new OutboxEvent { Channel = "realtime", AttemptCount = 4 };
        S101MessagingRetry.DeliveryDispatched(row, payment: false, acknowledged: false, DateTimeOffset.UtcNow);
        Assert.Equal(OutboxStatus.Completed, row.Status);
        Assert.Null(row.NextAttemptAt);
        Assert.Equal(4, row.AttemptCount);
    }

    [Fact]
    public async Task DatabaseFailuresRetryTheSameRecordBeforeAcknowledgement()
    {
        var attempts = 0; var delays = new List<TimeSpan>(); var committed = false;
        await S101MessagingRetry.ProcessUntilCommittedAsync(_ =>
        {
            Assert.False(committed);
            if (++attempts < 3) throw new NpgsqlException("Test database offline");
            return Task.CompletedTask;
        }, (delay, _) => { delays.Add(delay); return Task.CompletedTask; }, () => { }, default);
        committed = true;
        Assert.True(committed);
        Assert.Equal(3, attempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, delays);
    }

    [Fact]
    public async Task InvalidContractDoesNotEnterTransientRetryLoop()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => S101MessagingRetry.ProcessUntilCommittedAsync(
            _ => throw new InvalidOperationException("Invalid contract"),
            (_, _) => throw new Exception("Must not retry a contract rejection"), () => { }, default));
        Assert.True(S101MessagingRetry.IsTransient(new InvalidOperationException("Wrapped", new NpgsqlException("Offline"))));
    }

    [Fact]
    public async Task ShutdownLeavesTheCurrentRecordUnacknowledged()
    {
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => S101MessagingRetry.ProcessUntilCommittedAsync(
            _ => throw new NpgsqlException("Test offline"), (_, ct) => { stop.Cancel(); return Task.FromCanceled(ct); },
            () => { }, stop.Token));
    }

    [Fact]
    public void PaymentDeliveryKeepsTheSameMessageAndRetriesBeyondTheNormalLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var row = new OutboxEvent
        {
            Id = Guid.NewGuid(), MessageId = Guid.NewGuid(), UserId = Guid.NewGuid(), Channel = "realtime",
            Status = OutboxStatus.Processing, AttemptCount = 100, LeaseToken = Guid.NewGuid()
        };
        var message = row.MessageId;
        S101MessagingRetry.DeliveryFailed(row, true, false, now, 10);
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(message, row.MessageId);
        Assert.Equal(now.AddMinutes(5), row.NextAttemptAt);
        Assert.Null(row.LeaseToken);
        Assert.Equal(101, row.AttemptCount);
    }

    [Fact]
    public void NonPaymentPermanentDeliveryFailuresRetainTheExistingLimit()
    {
        var row = new OutboxEvent { Channel = "realtime", AttemptCount = 9 };
        S101MessagingRetry.DeliveryFailed(row, false, false, DateTimeOffset.UtcNow, 10);
        Assert.Equal(OutboxStatus.Failed, row.Status);
        S101MessagingRetry.DeliveryFailed(row, false, true, DateTimeOffset.UtcNow, 10);
        Assert.Equal(OutboxStatus.Pending, row.Status);
    }

    [Fact]
    public void PaymentContractRejectsChangingItsRecipientOrBusinessKey()
    {
        var good = Envelope();
        S101VietQrPaymentNotification.Validate(good);
        Assert.Throws<InvalidOperationException>(() => S101VietQrPaymentNotification.Validate(good with { Recipients = [Guid.NewGuid()] }));
        Assert.Throws<InvalidOperationException>(() => S101VietQrPaymentNotification.Validate(good with { BusinessKey = "another-purchase" }));
        Assert.Throws<InvalidOperationException>(() => S101VietQrPaymentNotification.Validate(good with { EntityVersion = null }));
        Assert.Throws<InvalidOperationException>(() => S101VietQrPaymentNotification.Validate(good with { Environment = "Dev" }));
        Assert.Throws<InvalidOperationException>(() => S101VietQrPaymentNotification.Validate(good with { Environment = "Test" }));
    }

    [Fact]
    public void BusinessKeyDuplicateCannotCreateOrOverwriteASecondPaymentMessage()
    {
        var envelope = Envelope();
        var message = new Message
        {
            Id = Guid.NewGuid(), BusinessKey = envelope.BusinessKey, SourceApp = envelope.SourceApp,
            SourceEvent = envelope.EventType, Category = "payment", EntityType = envelope.EntityType,
            EntityId = envelope.EntityId, TemplateCode = S101VietQrPaymentNotification.TemplateCode,
            Title = "Payment", Content = "Credited", Recipients = [new() { UserId = envelope.Recipients[0] }]
        };
        Assert.True(MessagingEventProcessor.IsSamePaymentNotification(message, envelope));
        Assert.False(MessagingEventProcessor.IsSamePaymentNotification(message, envelope with { Recipients = [Guid.NewGuid()] }));
        message.Content = "Conflicting amount";
        Assert.False(MessagingEventProcessor.IsSamePaymentNotification(message, envelope));
    }

    [Fact]
    public void ExistingLeaseColumnIsUsedForOptimisticDeliveryOwnership()
    {
        using var db = new XanhNowMessagingDbContext(new DbContextOptionsBuilder<XanhNowMessagingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
        Assert.True(db.Model.FindEntityType(typeof(OutboxEvent))!.FindProperty(nameof(OutboxEvent.LeaseToken))!.IsConcurrencyToken);
        Assert.Contains(S101VietQrPaymentNotification.Topic, new KafkaOptions().Topics);
        Assert.Equal("s101.xanhnow.membership_payment.events", S101VietQrPaymentNotification.Topic);
        Assert.Contains("s101.xanhnow.membership.events", new KafkaOptions().Topics);
    }

    private static MessagingEventEnvelope Envelope()
    {
        var order = Guid.NewGuid(); var user = Guid.NewGuid();
        return new(Guid.NewGuid(), S101VietQrPaymentNotification.EventType, S101VietQrPaymentNotification.SourceApp,
            DateTimeOffset.UtcNow, "s101-test", S101VietQrPaymentNotification.BusinessKey(order), [user],
            S101VietQrPaymentNotification.EntityType, order.ToString("D"), "1", "Production", 1, null,
            JsonSerializer.SerializeToElement(new { templateCode = S101VietQrPaymentNotification.TemplateCode,
                title = "Payment", content = "Credited", category = "payment", orderId = order, userId = user,
                actionType = S101VietQrPaymentNotification.ActionType }));
    }
}
