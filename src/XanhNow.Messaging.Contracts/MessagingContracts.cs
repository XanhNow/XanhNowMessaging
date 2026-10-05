using System.Text.Json;

namespace XanhNow.Messaging.Contracts;

public sealed record MessagingEventEnvelope(
    Guid EventId,
    string EventType,
    string SourceApp,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    string BusinessKey,
    IReadOnlyList<Guid> Recipients,
    string EntityType,
    string EntityId,
    string SchemaVersion,
    string Environment,
    long? EntityVersion,
    long? SequenceNumber,
    JsonElement Payload);

public sealed record MessageSummary(
    Guid MessageId,
    string Category,
    string Title,
    string Content,
    string? ActionType,
    JsonElement? ActionData,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

public sealed record MessageDetail(
    Guid MessageId,
    string Category,
    string TemplateCode,
    string Title,
    string Content,
    string SourceApp,
    string SourceEvent,
    string EntityType,
    string EntityId,
    string? ActionType,
    JsonElement? ActionData,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? ReadAt);

public sealed record MessagePage(
    IReadOnlyList<MessageSummary> Items,
    string? NextCursor);

public sealed record UnreadCountResponse(long Count);

public sealed record RegisterDeviceRequest(
    string Platform,
    string PushToken,
    string? DeviceName,
    string? AppVersion);

public sealed record DeviceResponse(
    Guid DeviceId,
    string Platform,
    string? DeviceName,
    string? AppVersion,
    DateTimeOffset UpdatedAt);

public sealed record RealtimeMessageNotification(
    Guid MessageId,
    string Category,
    string Title,
    string Content,
    string? ActionType,
    JsonElement? ActionData,
    DateTimeOffset CreatedAt);

