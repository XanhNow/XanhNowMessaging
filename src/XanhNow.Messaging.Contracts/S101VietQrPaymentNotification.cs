using System.Text.Json;

namespace XanhNow.Messaging.Contracts;

public static class S101VietQrPaymentNotification
{
    public const string Topic = "s101.xanhnow.membership_payment.events";
    public const string EventType = "SystemNotificationRequested";
    public const string SourceApp = "XanhNow.Membership";
    public const string EntityType = "s101VietQrPurchase";
    public const string TemplateCode = "S101_VIETQR_PAYMENT_CREDITED";
    public const string ActionType = "OpenPayment";
    public static string BusinessKey(Guid orderId) => $"s101:membership:payment:{orderId:N}:credited";

    public static bool IsPayment(MessagingEventEnvelope envelope) =>
        envelope.EventType == EventType && envelope.Payload.ValueKind == JsonValueKind.Object &&
        envelope.Payload.TryGetProperty("templateCode", out var code) &&
        code.ValueKind == JsonValueKind.String && code.GetString() == TemplateCode;

    public static void Validate(MessagingEventEnvelope envelope)
    {
        if (!IsPayment(envelope) || envelope.SourceApp != SourceApp || envelope.EntityType != EntityType ||
            !Guid.TryParse(envelope.EntityId, out var orderId) || orderId == Guid.Empty ||
            envelope.EventId == Guid.Empty || envelope.BusinessKey != BusinessKey(orderId) ||
            envelope.Recipients is not { Count: 1 } || envelope.Recipients[0] == Guid.Empty ||
            envelope.SchemaVersion != "1" || envelope.EntityVersion is not > 0 ||
            envelope.Environment != "Production" ||
            !envelope.Payload.TryGetProperty("userId", out var user) ||
            user.ValueKind != JsonValueKind.String || !user.TryGetGuid(out var userId) || userId != envelope.Recipients[0] ||
            !envelope.Payload.TryGetProperty("orderId", out var order) ||
            order.ValueKind != JsonValueKind.String || !order.TryGetGuid(out var payloadOrder) || payloadOrder != orderId ||
            !envelope.Payload.TryGetProperty("actionType", out var action) || action.GetString() != ActionType)
            throw new InvalidOperationException("Invalid s101 VietQR payment notification contract.");
        foreach (var field in new[] { "title", "content", "category" })
            if (!envelope.Payload.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidOperationException("Incomplete s101 VietQR payment notification content.");
        if (envelope.Payload.GetProperty("category").GetString() != "payment" ||
            envelope.Payload.GetProperty("title").GetString()!.Length > 300 ||
            envelope.Payload.GetProperty("content").GetString()!.Length > 4000)
            throw new InvalidOperationException("Invalid s101 VietQR payment notification content.");
    }
}
