using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using XanhNow.Messaging.Application;
using XanhNow.Messaging.Contracts;
using XanhNow.Messaging.Infrastructure;
using XanhNow.Messaging.Infrastructure.Migrations;

namespace XanhNow.Messaging.Tests;

public sealed class DisposableMessagingPostgresFactAttribute : FactAttribute
{
    public DisposableMessagingPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("S101_MESSAGING_DISPOSABLE_TEST_DB")))
            Skip = "Requires an explicitly provisioned disposable localhost PostgreSQL database.";
    }
}

public sealed class PaymentNotificationPersistenceTests
{
    [DisposableMessagingPostgresFact]
    public async Task PaymentCommitCreatesOwnerMailboxAndOutboxAndReplaysDoNotDuplicateThem()
    {
        var value = Environment.GetEnvironmentVariable("S101_MESSAGING_DISPOSABLE_TEST_DB")!;
        var connection = new NpgsqlConnectionStringBuilder(value);
        Assert.Equal("127.0.0.1", connection.Host);
        Assert.Equal("s101_messaging_fk_test", connection.Database);
        Assert.False(connection.Pooling);
        var options = new DbContextOptionsBuilder<XanhNowMessagingDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new XanhNowMessagingDbContext(options);
        Assert.False(await db.Database.SqlQueryRaw<bool>(
            "SELECT to_regnamespace('s101_xanhnow_messaging') IS NOT NULL AS \"Value\"").SingleAsync());
        var migration = new InitialMessaging { ActiveProvider = db.Database.ProviderName! };
        // Use the actual application's existing SQL, including its immediate FK.
        await db.Database.ExecuteSqlRawAsync(Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql);
        var owner = Guid.NewGuid();
        var purchase = Guid.NewGuid();
        var envelope = new MessagingEventEnvelope(Guid.NewGuid(), S101VietQrPaymentNotification.EventType,
            S101VietQrPaymentNotification.SourceApp, DateTimeOffset.UtcNow, "s101-fk-regression",
            S101VietQrPaymentNotification.BusinessKey(purchase), [owner], S101VietQrPaymentNotification.EntityType,
            purchase.ToString("D"), "1", "Production", 1, null,
            JsonSerializer.SerializeToElement(new { templateCode = S101VietQrPaymentNotification.TemplateCode,
                title = "Payment test", content = "Credited test", category = "payment",
                orderId = purchase, userId = owner, actionType = S101VietQrPaymentNotification.ActionType }));
        MessagingEventProcessor Processor() => new(db, Options.Create(new KafkaOptions { Environment = "Production" }),
            Options.Create(new SchedulerOptions()), TimeProvider.System, NullLogger<MessagingEventProcessor>.Instance);
        await Processor().ProcessAsync(envelope, default);
        db.ChangeTracker.Clear();
        var message = Assert.Single(await db.Messages.Include(x => x.Recipients).ToListAsync());
        Assert.Equal(owner, Assert.Single(message.Recipients).UserId);
        Assert.Equal(purchase.ToString("D"), message.EntityId);
        var outbox = Assert.Single(await db.OutboxEvents.ToListAsync());
        Assert.Equal(message.Id, outbox.MessageId);
        Assert.Equal(owner, outbox.UserId);
        Assert.Equal(1, await db.InboxEvents.CountAsync());
        Assert.Equal(1, await db.EventPositions.CountAsync());
        var mailbox = new MailboxRepository(db, new UnusedDeviceTokenProtector());
        Assert.Equal(message.Id, Assert.Single((await mailbox.GetPageAsync(owner, null, 30, null, null, default)).Items).MessageId);
        Assert.Empty((await mailbox.GetPageAsync(Guid.NewGuid(), null, 30, null, null, default)).Items);
        // Both event-ID replay and a new event-ID for the same business key stay one message.
        await Processor().ProcessAsync(envelope, default);
        db.ChangeTracker.Clear();
        await Processor().ProcessAsync(envelope with { EventId = Guid.NewGuid() }, default);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Messages.CountAsync());
        Assert.Equal(1, await db.MessageRecipients.CountAsync());
        Assert.Equal(1, await db.OutboxEvents.CountAsync());
        Assert.Equal(2, await db.InboxEvents.CountAsync());
        Assert.Equal(1, await db.InboxEvents.CountAsync(x => x.Status == "DuplicateBusinessKey"));
    }

    private sealed class UnusedDeviceTokenProtector : IDeviceTokenProtector
    {
        public string Protect(string token) => throw new NotSupportedException();
        public string Unprotect(string token) => throw new NotSupportedException();
    }
}
