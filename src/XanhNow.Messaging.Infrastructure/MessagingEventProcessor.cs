using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XanhNow.Messaging.Contracts;
using XanhNow.Messaging.Domain;

namespace XanhNow.Messaging.Infrastructure;

public sealed class MessagingEventProcessor(
    XanhNowMessagingDbContext dbContext,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<SchedulerOptions> schedulerOptions,
    TimeProvider timeProvider,
    ILogger<MessagingEventProcessor> logger)
{
    public async Task ProcessAsync(MessagingEventEnvelope envelope, CancellationToken cancellationToken)
    {
        Validate(envelope);
        if (!string.Equals(envelope.Environment, kafkaOptions.Value.Environment, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Event environment does not match this deployment.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var consumerName = kafkaOptions.Value.ConsumerGroup;
        if (await dbContext.InboxEvents.AnyAsync(
                x => x.EventId == envelope.EventId && x.ConsumerName == consumerName,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var now = timeProvider.GetUtcNow();
        var position = envelope.SequenceNumber ?? envelope.EntityVersion;
        if (position.HasValue && !await TryAdvancePositionAsync(envelope, position.Value, now, cancellationToken))
        {
            dbContext.InboxEvents.Add(CreateInbox(envelope, consumerName, "IgnoredStale", now));
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var status = await ApplyEventAsync(envelope, now, cancellationToken);
        dbContext.InboxEvents.Add(CreateInbox(envelope, consumerName, status, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<string> ApplyEventAsync(
        MessagingEventEnvelope envelope,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        switch (envelope.EventType)
        {
            case "TripAccepted":
                await HandleTripAcceptedAsync(envelope, now, cancellationToken);
                return "Processed";
            case "TripUpdated":
                await HandleTripUpdatedAsync(envelope, now, cancellationToken);
                return "Processed";
            case "TripCompleted":
                await CancelSchedulesAsync(envelope.EntityType, envelope.EntityId, now, cancellationToken);
                AddMessage(
                    envelope,
                    "trip",
                    "TRIP_COMPLETED",
                    "Lịch xe đã hoàn thành",
                    "Lịch xe đã được xác nhận hoàn thành.",
                    "OpenTrip",
                    envelope.Recipients,
                    now);
                return "Processed";
            case "TripCancelled":
                await CancelSchedulesAsync(envelope.EntityType, envelope.EntityId, now, cancellationToken);
                AddMessage(
                    envelope,
                    "trip",
                    "TRIP_CANCELLED",
                    "Lịch xe đã hủy",
                    "Lịch xe liên quan đã được hủy.",
                    "OpenTrip",
                    envelope.Recipients,
                    now);
                return "Processed";
            case "SystemNotificationRequested":
                HandleSystemNotification(envelope, now);
                return "Processed";
            default:
                logger.LogInformation(
                    "Ignoring unsupported messaging event type {EventType} with event id {EventId}.",
                    envelope.EventType,
                    envelope.EventId);
                return "IgnoredUnsupported";
        }
    }

    private async Task HandleTripAcceptedAsync(
        MessagingEventEnvelope envelope,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        AddMessage(
            envelope,
            "trip",
            "TRIP_ACCEPTED",
            "Lịch xe đã có người nhận",
            "Lịch xe đã được nhận. Vui lòng kiểm tra thời gian và địa điểm đón.",
            "OpenTrip",
            envelope.Recipients,
            now);

        var pickupAt = ReadRequiredDateTime(envelope.Payload, "pickupAtUtc");
        var driverUserId = ReadGuid(envelope.Payload, "driverUserId") ?? envelope.Recipients.FirstOrDefault();
        if (driverUserId == Guid.Empty)
        {
            throw new InvalidOperationException("TripAccepted requires driverUserId or at least one recipient.");
        }

        await CancelSchedulesAsync(envelope.EntityType, envelope.EntityId, now, cancellationToken);
        AddSchedule(
            envelope,
            driverUserId,
            "TRIP_PICKUP_REMINDER",
            pickupAt - schedulerOptions.Value.PickupReminderLeadTime,
            now);
        AddSchedule(
            envelope,
            driverUserId,
            "TRIP_COMPLETION_REMINDER",
            pickupAt + schedulerOptions.Value.CompletionReminderDelay,
            now);
    }

    private async Task HandleTripUpdatedAsync(
        MessagingEventEnvelope envelope,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        AddMessage(
            envelope,
            "trip",
            "TRIP_UPDATED",
            "Lịch xe đã thay đổi",
            "Thông tin lịch xe đã được cập nhật. Vui lòng kiểm tra lại chi tiết.",
            "OpenTrip",
            envelope.Recipients,
            now);

        if (!envelope.Payload.TryGetProperty("pickupAtUtc", out var pickupElement) ||
            pickupElement.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(pickupElement.GetString(), out var pickupAt))
        {
            return;
        }

        var driverUserId = ReadGuid(envelope.Payload, "driverUserId") ?? envelope.Recipients.FirstOrDefault();
        if (driverUserId == Guid.Empty)
        {
            return;
        }

        await CancelSchedulesAsync(envelope.EntityType, envelope.EntityId, now, cancellationToken);
        AddSchedule(
            envelope,
            driverUserId,
            "TRIP_PICKUP_REMINDER",
            pickupAt - schedulerOptions.Value.PickupReminderLeadTime,
            now);
        AddSchedule(
            envelope,
            driverUserId,
            "TRIP_COMPLETION_REMINDER",
            pickupAt + schedulerOptions.Value.CompletionReminderDelay,
            now);
    }

    private void HandleSystemNotification(MessagingEventEnvelope envelope, DateTimeOffset now)
    {
        var templateCode = ReadRequiredString(envelope.Payload, "templateCode", 120);
        var title = ReadRequiredString(envelope.Payload, "title", 300);
        var content = ReadRequiredString(envelope.Payload, "content", 4000);
        var category = ReadOptionalString(envelope.Payload, "category", 80) ?? "system";
        var actionType = ReadOptionalString(envelope.Payload, "actionType", 80);
        AddMessage(envelope, category, templateCode, title, content, actionType, envelope.Recipients, now);
    }

    private void AddMessage(
        MessagingEventEnvelope envelope,
        string category,
        string templateCode,
        string title,
        string content,
        string? actionType,
        IEnumerable<Guid> recipientIds,
        DateTimeOffset now)
    {
        var recipients = recipientIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (recipients.Length == 0)
        {
            throw new InvalidOperationException("At least one authoritative recipient is required.");
        }

        var message = new Message
        {
            Id = Guid.NewGuid(),
            BusinessKey = envelope.BusinessKey,
            Category = category,
            TemplateCode = templateCode,
            Title = title,
            Content = content,
            SourceApp = envelope.SourceApp,
            SourceEvent = envelope.EventType,
            EntityType = envelope.EntityType,
            EntityId = envelope.EntityId,
            ActionType = actionType,
            ActionDataJson = JsonSerializer.Serialize(new { entityType = envelope.EntityType, entityId = envelope.EntityId }),
            CreatedAt = now
        };
        foreach (var userId in recipients)
        {
            message.Recipients.Add(new MessageRecipient
            {
                MessageId = message.Id,
                UserId = userId,
                DeliveryStatus = DeliveryStatus.Created,
                CreatedAt = now
            });
            dbContext.OutboxEvents.Add(new OutboxEvent
            {
                Id = Guid.NewGuid(),
                MessageId = message.Id,
                UserId = userId,
                Channel = "realtime",
                Status = OutboxStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        dbContext.Messages.Add(message);
    }

    private void AddSchedule(
        MessagingEventEnvelope envelope,
        Guid userId,
        string templateCode,
        DateTimeOffset scheduledAt,
        DateTimeOffset now)
    {
        dbContext.ScheduledMessages.Add(new ScheduledMessage
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TemplateCode = templateCode,
            BusinessKey = $"{envelope.BusinessKey}:{templateCode.ToLowerInvariant()}",
            Category = "trip",
            SourceApp = envelope.SourceApp,
            SourceEvent = envelope.EventType,
            EntityType = envelope.EntityType,
            EntityId = envelope.EntityId,
            PayloadJson = envelope.Payload.GetRawText(),
            ScheduledAt = scheduledAt < now ? now : scheduledAt,
            Status = ScheduleStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private Task<int> CancelSchedulesAsync(
        string entityType,
        string entityId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        dbContext.ScheduledMessages
            .Where(x =>
                x.EntityType == entityType &&
                x.EntityId == entityId &&
                (x.Status == ScheduleStatus.Pending || x.Status == ScheduleStatus.Processing))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, ScheduleStatus.Cancelled)
                .SetProperty(x => x.LeaseToken, (Guid?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);

    private async Task<bool> TryAdvancePositionAsync(
        MessagingEventEnvelope envelope,
        long value,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var position = await dbContext.EventPositions.SingleOrDefaultAsync(x =>
            x.SourceApp == envelope.SourceApp &&
            x.EntityType == envelope.EntityType &&
            x.EntityId == envelope.EntityId,
            cancellationToken);
        if (position is not null && value <= position.Position)
        {
            return false;
        }
        if (position is null)
        {
            dbContext.EventPositions.Add(new EventPosition
            {
                SourceApp = envelope.SourceApp,
                EntityType = envelope.EntityType,
                EntityId = envelope.EntityId,
                Position = value,
                LastEventId = envelope.EventId,
                UpdatedAt = now
            });
        }
        else
        {
            position.Position = value;
            position.LastEventId = envelope.EventId;
            position.UpdatedAt = now;
        }
        return true;
    }

    private static InboxEvent CreateInbox(
        MessagingEventEnvelope envelope,
        string consumerName,
        string status,
        DateTimeOffset now) => new()
        {
            EventId = envelope.EventId,
            ConsumerName = consumerName,
            SourceApp = envelope.SourceApp,
            EventType = envelope.EventType,
            Status = status,
            ProcessedAt = now
        };

    private static void Validate(MessagingEventEnvelope envelope)
    {
        if (envelope.EventId == Guid.Empty ||
            string.IsNullOrWhiteSpace(envelope.EventType) ||
            string.IsNullOrWhiteSpace(envelope.SourceApp) ||
            string.IsNullOrWhiteSpace(envelope.BusinessKey) ||
            string.IsNullOrWhiteSpace(envelope.EntityType) ||
            string.IsNullOrWhiteSpace(envelope.EntityId))
        {
            throw new InvalidOperationException("Messaging event envelope is incomplete.");
        }
        if (!string.Equals(envelope.SchemaVersion, "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported SchemaVersion '{envelope.SchemaVersion}'.");
        }
    }

    private static DateTimeOffset ReadRequiredDateTime(JsonElement payload, string propertyName)
    {
        var value = ReadRequiredString(payload, propertyName, 80);
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Payload property '{propertyName}' is not a valid timestamp.");
    }

    private static Guid? ReadGuid(JsonElement payload, string propertyName)
    {
        return payload.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String &&
               Guid.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static string ReadRequiredString(JsonElement payload, string propertyName, int maxLength) =>
        ReadOptionalString(payload, propertyName, maxLength)
        ?? throw new InvalidOperationException($"Payload property '{propertyName}' is required.");

    private static string? ReadOptionalString(JsonElement payload, string propertyName, int maxLength)
    {
        if (!payload.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var text = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}

