using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XanhNow.Messaging.Application;
using XanhNow.Messaging.Contracts;
using XanhNow.Messaging.Domain;

namespace XanhNow.Messaging.Infrastructure;

public sealed class KafkaConsumerWorker(
    IServiceScopeFactory scopeFactory,
    KafkaDeadLetterPublisher deadLetterPublisher,
    IOptions<KafkaOptions> options,
    ILogger<KafkaConsumerWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Kafka consumer is disabled.");
            return;
        }

        using var consumer = new ConsumerBuilder<string, string>(BuildConfig(options.Value))
            .SetErrorHandler((_, error) => logger.LogError("Kafka error {Code}: {Reason}", error.Code, error.Reason))
            .Build();
        consumer.Subscribe(options.Value.Topics);
        logger.LogInformation("Kafka consumer subscribed to {Topics}.", string.Join(',', options.Value.Topics));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result = null;
                try
                {
                    result = consumer.Consume(stoppingToken);
                    var envelope = JsonSerializer.Deserialize<MessagingEventEnvelope>(result.Message.Value, JsonOptions)
                        ?? throw new InvalidOperationException("Kafka event payload is empty.");
                    using var scope = scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<MessagingEventProcessor>();
                    await processor.ProcessAsync(envelope, stoppingToken);
                    consumer.Commit(result);
                }
                catch (ConsumeException exception)
                {
                    logger.LogError(exception, "Kafka consume failed.");
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                catch (JsonException exception)
                {
                    logger.LogError(exception, "Kafka event is malformed at {Position}; moving to DLQ.", result?.TopicPartitionOffset);
                    if (result is not null)
                    {
                        await deadLetterPublisher.PublishAsync(result, "malformed-json", stoppingToken);
                        consumer.Commit(result);
                    }
                }
                catch (InvalidOperationException exception)
                {
                    logger.LogError(exception, "Kafka event rejected at {Position}; moving to DLQ.", result?.TopicPartitionOffset);
                    if (result is not null)
                    {
                        await deadLetterPublisher.PublishAsync(result, "contract-rejected", stoppingToken);
                        consumer.Commit(result);
                    }
                }
                catch (DbUpdateException exception)
                {
                    logger.LogWarning(exception, "Database conflict while processing Kafka event; event will be redelivered.");
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            consumer.Close();
        }
    }

    private static ConsumerConfig BuildConfig(KafkaOptions value)
    {
        if (value.BootstrapServers.Length == 0 || value.Topics.Length == 0)
        {
            throw new InvalidOperationException("Kafka BootstrapServers and Topics are required.");
        }
        return new ConsumerConfig
        {
            BootstrapServers = string.Join(',', value.BootstrapServers),
            ClientId = value.ClientId,
            GroupId = value.ConsumerGroup,
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            SecurityProtocol = ParseSecurityProtocol(value.SecurityProtocol),
            SaslMechanism = ParseSaslMechanism(value.SaslMechanism),
            SaslUsername = value.Username,
            SaslPassword = SecretFile.ReadRequired(value.PasswordFile, "Kafka password"),
            SslCaLocation = value.CaFile,
            EnablePartitionEof = false
        };
    }

    private static SecurityProtocol ParseSecurityProtocol(string value) => value.ToUpperInvariant() switch
    {
        "PLAINTEXT" => SecurityProtocol.Plaintext,
        "SSL" => SecurityProtocol.Ssl,
        "SASL_PLAINTEXT" => SecurityProtocol.SaslPlaintext,
        "SASL_SSL" => SecurityProtocol.SaslSsl,
        _ => throw new InvalidOperationException($"Unsupported Kafka SecurityProtocol '{value}'.")
    };

    private static SaslMechanism ParseSaslMechanism(string value) => value.ToUpperInvariant() switch
    {
        "PLAIN" => SaslMechanism.Plain,
        "SCRAM-SHA-256" => SaslMechanism.ScramSha256,
        "SCRAM-SHA-512" => SaslMechanism.ScramSha512,
        _ => throw new InvalidOperationException($"Unsupported Kafka SaslMechanism '{value}'.")
    };
}

public sealed class KafkaDeadLetterPublisher : IDisposable
{
    private readonly KafkaOptions _options;
    private readonly IProducer<string, string>? _producer;

    public KafkaDeadLetterPublisher(IOptions<KafkaOptions> options)
    {
        _options = options.Value;
        if (!_options.Enabled)
        {
            return;
        }
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = string.Join(',', _options.BootstrapServers),
            ClientId = $"{_options.ClientId}-dlq",
            Acks = Acks.All,
            EnableIdempotence = true,
            SecurityProtocol = ParseSecurityProtocol(_options.SecurityProtocol),
            SaslMechanism = ParseSaslMechanism(_options.SaslMechanism),
            SaslUsername = _options.Username,
            SaslPassword = SecretFile.ReadRequired(_options.PasswordFile, "Kafka password"),
            SslCaLocation = _options.CaFile
        }).Build();
    }

    public async Task PublishAsync(
        ConsumeResult<string, string> source,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_producer is null || string.IsNullOrWhiteSpace(_options.DeadLetterTopic))
        {
            throw new InvalidOperationException("Kafka DLQ publisher is not configured.");
        }
        var headers = new Headers
        {
            { "x-source-topic", System.Text.Encoding.UTF8.GetBytes(source.Topic) },
            { "x-source-partition", System.Text.Encoding.UTF8.GetBytes(source.Partition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
            { "x-source-offset", System.Text.Encoding.UTF8.GetBytes(source.Offset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
            { "x-reject-reason", System.Text.Encoding.UTF8.GetBytes(reason) }
        };
        await _producer.ProduceAsync(
            _options.DeadLetterTopic,
            new Message<string, string>
            {
                Key = source.Message.Key,
                Value = source.Message.Value,
                Headers = headers
            },
            cancellationToken);
    }

    public void Dispose()
    {
        _producer?.Flush(TimeSpan.FromSeconds(5));
        _producer?.Dispose();
    }

    private static SecurityProtocol ParseSecurityProtocol(string value) => value.ToUpperInvariant() switch
    {
        "PLAINTEXT" => SecurityProtocol.Plaintext,
        "SSL" => SecurityProtocol.Ssl,
        "SASL_PLAINTEXT" => SecurityProtocol.SaslPlaintext,
        "SASL_SSL" => SecurityProtocol.SaslSsl,
        _ => throw new InvalidOperationException($"Unsupported Kafka SecurityProtocol '{value}'.")
    };

    private static SaslMechanism ParseSaslMechanism(string value) => value.ToUpperInvariant() switch
    {
        "PLAIN" => SaslMechanism.Plain,
        "SCRAM-SHA-256" => SaslMechanism.ScramSha256,
        "SCRAM-SHA-512" => SaslMechanism.ScramSha512,
        _ => throw new InvalidOperationException($"Unsupported Kafka SaslMechanism '{value}'.")
    };
}

public sealed class ScheduledMessageWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<SchedulerOptions> options,
    TimeProvider timeProvider,
    ILogger<ScheduledMessageWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Scheduled message batch failed.");
            }
            await Task.Delay(options.Value.PollInterval, stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var leaseToken = Guid.NewGuid();
        using var claimScope = scopeFactory.CreateScope();
        var claimDb = claimScope.ServiceProvider.GetRequiredService<XanhNowMessagingDbContext>();
        var ids = await claimDb.ScheduledMessages
            .Where(x =>
                (x.Status == ScheduleStatus.Pending ||
                 (x.Status == ScheduleStatus.Processing && x.LeaseExpiresAt < now)) &&
                x.ScheduledAt <= now &&
                (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.ScheduledAt)
            .Take(options.Value.BatchSize)
            .Select(x => x.Id)
            .ToArrayAsync(cancellationToken);
        if (ids.Length == 0)
        {
            return;
        }
        await claimDb.ScheduledMessages
            .Where(x => ids.Contains(x.Id) &&
                (x.Status == ScheduleStatus.Pending ||
                 (x.Status == ScheduleStatus.Processing && x.LeaseExpiresAt < now)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, ScheduleStatus.Processing)
                .SetProperty(x => x.LeaseToken, leaseToken)
                .SetProperty(x => x.LeaseExpiresAt, now + options.Value.LeaseDuration)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);

        var claimed = await claimDb.ScheduledMessages
            .AsNoTracking()
            .Where(x => x.LeaseToken == leaseToken)
            .Select(x => x.Id)
            .ToArrayAsync(cancellationToken);
        foreach (var id in claimed)
        {
            await MaterializeAsync(id, leaseToken, cancellationToken);
        }
    }

    private async Task MaterializeAsync(Guid scheduleId, Guid leaseToken, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<XanhNowMessagingDbContext>();
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var schedule = await db.ScheduledMessages.SingleOrDefaultAsync(
            x => x.Id == scheduleId &&
                 x.Status == ScheduleStatus.Processing &&
                 x.LeaseToken == leaseToken,
            cancellationToken);
        if (schedule is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        try
        {
            var existing = await db.Messages.AnyAsync(x => x.BusinessKey == schedule.BusinessKey, cancellationToken);
            if (!existing)
            {
                var (title, content) = Render(schedule.TemplateCode, schedule.PayloadJson);
                var message = new Message
                {
                    Id = Guid.NewGuid(),
                    BusinessKey = schedule.BusinessKey,
                    Category = schedule.Category,
                    TemplateCode = schedule.TemplateCode,
                    Title = title,
                    Content = content,
                    SourceApp = schedule.SourceApp,
                    SourceEvent = schedule.SourceEvent,
                    EntityType = schedule.EntityType,
                    EntityId = schedule.EntityId,
                    ActionType = "OpenTrip",
                    ActionDataJson = JsonSerializer.Serialize(new { entityType = schedule.EntityType, entityId = schedule.EntityId }),
                    CreatedAt = now
                };
                message.Recipients.Add(new MessageRecipient
                {
                    MessageId = message.Id,
                    UserId = schedule.UserId,
                    DeliveryStatus = DeliveryStatus.Created,
                    CreatedAt = now
                });
                db.Messages.Add(message);
                db.OutboxEvents.Add(new OutboxEvent
                {
                    Id = Guid.NewGuid(),
                    MessageId = message.Id,
                    UserId = schedule.UserId,
                    Channel = "realtime",
                    Status = OutboxStatus.Pending,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            schedule.Status = ScheduleStatus.Completed;
            schedule.LeaseToken = null;
            schedule.LeaseExpiresAt = null;
            schedule.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await transaction.RollbackAsync(cancellationToken);
            await MarkFailedAsync(scheduleId, leaseToken, exception, cancellationToken);
        }
    }

    private async Task MarkFailedAsync(
        Guid scheduleId,
        Guid leaseToken,
        Exception exception,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<XanhNowMessagingDbContext>();
        var now = timeProvider.GetUtcNow();
        var schedule = await db.ScheduledMessages.SingleOrDefaultAsync(
            x => x.Id == scheduleId && x.LeaseToken == leaseToken,
            cancellationToken);
        if (schedule is null)
        {
            return;
        }
        schedule.AttemptCount++;
        schedule.Status = schedule.AttemptCount >= options.Value.MaxAttempts
            ? ScheduleStatus.Failed
            : ScheduleStatus.Pending;
        schedule.NextAttemptAt = now + Backoff(schedule.AttemptCount);
        schedule.LeaseToken = null;
        schedule.LeaseExpiresAt = null;
        schedule.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogError(exception, "Scheduled message {ScheduleId} failed on attempt {Attempt}.", scheduleId, schedule.AttemptCount);
    }

    internal static (string Title, string Content) Render(string templateCode, string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var pickupAt = document.RootElement.TryGetProperty("pickupAtUtc", out var element)
            ? element.GetString()
            : null;
        return templateCode switch
        {
            "TRIP_PICKUP_REMINDER" => (
                "Sắp đến giờ đón khách",
                string.IsNullOrWhiteSpace(pickupAt)
                    ? "Vui lòng kiểm tra lịch xe và chuẩn bị đón khách đúng giờ."
                    : $"Vui lòng chuẩn bị thực hiện lịch xe có giờ đón {pickupAt}."),
            "TRIP_COMPLETION_REMINDER" => (
                "Nhắc hoàn thành lịch xe",
                "Nếu lịch xe đã kết thúc an toàn, người tạo và người nhận vui lòng mở lịch xe để xác nhận Hoàn thành."),
            _ => throw new InvalidOperationException($"Unsupported scheduled template '{templateCode}'.")
        };
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, attempt)));
}

public sealed class OutboxDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IRealtimeNotifier realtimeNotifier,
    IOptions<DeliveryOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Outbox delivery batch failed.");
            }
            await Task.Delay(options.Value.PollInterval, stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var leaseToken = Guid.NewGuid();
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<XanhNowMessagingDbContext>();
        var ids = await db.OutboxEvents
            .Where(x =>
                (x.Status == OutboxStatus.Pending ||
                 (x.Status == OutboxStatus.Processing && x.LeaseExpiresAt < now)) &&
                (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.CreatedAt)
            .Take(options.Value.BatchSize)
            .Select(x => x.Id)
            .ToArrayAsync(cancellationToken);
        if (ids.Length == 0)
        {
            return;
        }
        await db.OutboxEvents
            .Where(x => ids.Contains(x.Id) &&
                (x.Status == OutboxStatus.Pending ||
                 (x.Status == OutboxStatus.Processing && x.LeaseExpiresAt < now)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, OutboxStatus.Processing)
                .SetProperty(x => x.LeaseToken, leaseToken)
                .SetProperty(x => x.LeaseExpiresAt, now + options.Value.LeaseDuration)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);

        var claimed = await db.OutboxEvents.AsNoTracking()
            .Where(x => x.LeaseToken == leaseToken)
            .Select(x => x.Id)
            .ToArrayAsync(cancellationToken);
        foreach (var id in claimed)
        {
            await DeliverAsync(id, leaseToken, cancellationToken);
        }
    }

    private async Task DeliverAsync(Guid outboxId, Guid leaseToken, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<XanhNowMessagingDbContext>();
        var row = await db.OutboxEvents.SingleOrDefaultAsync(
            x => x.Id == outboxId && x.LeaseToken == leaseToken,
            cancellationToken);
        if (row is null)
        {
            return;
        }
        var now = timeProvider.GetUtcNow();
        try
        {
            var message = await db.Messages.AsNoTracking()
                .Where(x => x.Id == row.MessageId)
                .Select(x => new
                {
                    x.Id,
                    x.Category,
                    x.Title,
                    x.Content,
                    x.ActionType,
                    x.ActionDataJson,
                    x.CreatedAt
                })
                .SingleAsync(cancellationToken);
            var data = new RealtimeMessageNotification(
                message.Id,
                message.Category,
                message.Title,
                message.Content,
                message.ActionType,
                ParseJson(message.ActionDataJson),
                message.CreatedAt);
            await realtimeNotifier.SendAsync(row.UserId, data, cancellationToken);
            row.Status = OutboxStatus.Completed;
            row.LeaseToken = null;
            row.LeaseExpiresAt = null;
            row.UpdatedAt = now;
            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                OutboxEventId = row.Id,
                Channel = row.Channel,
                Succeeded = true,
                ProviderCode = "signalr-dispatched",
                AttemptedAt = now
            });
            await db.MessageRecipients
                .Where(x => x.MessageId == row.MessageId && x.UserId == row.UserId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.DeliveryStatus, DeliveryStatus.Dispatched)
                    .SetProperty(x => x.DispatchedAt, now), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            row.AttemptCount++;
            row.LastError = Sanitize(exception.Message);
            row.Status = row.AttemptCount >= options.Value.MaxAttempts ? OutboxStatus.Failed : OutboxStatus.Pending;
            row.NextAttemptAt = now + TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, row.AttemptCount)));
            row.LeaseToken = null;
            row.LeaseExpiresAt = null;
            row.UpdatedAt = now;
            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                OutboxEventId = row.Id,
                Channel = row.Channel,
                Succeeded = false,
                Error = row.LastError,
                AttemptedAt = now
            });
            logger.LogWarning(exception, "Outbox event {OutboxId} failed on attempt {Attempt}.", row.Id, row.AttemptCount);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Sanitize(string value) => value.Length <= 1000 ? value : value[..1000];
}
