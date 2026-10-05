namespace XanhNow.Messaging.Domain;

public enum DeliveryStatus
{
    Created = 0,
    Dispatched = 1,
    Delivered = 2
}

public enum ScheduleStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Cancelled = 3,
    Failed = 4
}

public enum OutboxStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3
}

public sealed class Message
{
    public Guid Id { get; set; }
    public required string BusinessKey { get; set; }
    public required string Category { get; set; }
    public required string TemplateCode { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public required string SourceApp { get; set; }
    public required string SourceEvent { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public string? ActionType { get; set; }
    public string? ActionDataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<MessageRecipient> Recipients { get; set; } = [];
}

public sealed class MessageRecipient
{
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public string? PhoneNumberSnapshot { get; set; }
    public DeliveryStatus DeliveryStatus { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Message Message { get; set; } = null!;
}

public sealed class ScheduledMessage
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string TemplateCode { get; set; }
    public required string BusinessKey { get; set; }
    public required string Category { get; set; }
    public required string SourceApp { get; set; }
    public required string SourceEvent { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset ScheduledAt { get; set; }
    public ScheduleStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class InboxEvent
{
    public Guid EventId { get; set; }
    public required string ConsumerName { get; set; }
    public required string SourceApp { get; set; }
    public required string EventType { get; set; }
    public required string Status { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

public sealed class OutboxEvent
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public required string Channel { get; set; }
    public OutboxStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public Guid? LeaseToken { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DeliveryAttempt
{
    public long Id { get; set; }
    public Guid OutboxEventId { get; set; }
    public required string Channel { get; set; }
    public bool Succeeded { get; set; }
    public string? ProviderCode { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
}

public sealed class UserDevice
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Platform { get; set; }
    public required string PushTokenHash { get; set; }
    public required string ProtectedPushToken { get; set; }
    public string? DeviceName { get; set; }
    public string? AppVersion { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class EventPosition
{
    public required string SourceApp { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public long Position { get; set; }
    public Guid LastEventId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
