using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XanhNow.Messaging.Application;
using XanhNow.Messaging.Contracts;
using XanhNow.Messaging.Domain;

namespace XanhNow.Messaging.Infrastructure;

public sealed class MailboxRepository(
    XanhNowMessagingDbContext dbContext,
    IDeviceTokenProtector tokenProtector)
    : IMailboxRepository, IDeviceRegistry
{
    public async Task<MessagePage> GetPageAsync(
        Guid userId,
        string? cursor,
        int pageSize,
        string? category,
        bool? unreadOnly,
        CancellationToken cancellationToken)
    {
        var query = dbContext.MessageRecipients
            .AsNoTracking()
            .Where(x => x.UserId == userId);

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(x => x.Message.Category == category);
        }
        if (unreadOnly is true)
        {
            query = query.Where(x => x.ReadAt == null);
        }
        if (TryDecodeCursor(cursor, out var cursorTime, out var cursorId))
        {
            query = query.Where(x =>
                x.Message.CreatedAt < cursorTime ||
                (x.Message.CreatedAt == cursorTime && x.MessageId.CompareTo(cursorId) < 0));
        }

        var rows = await query
            .OrderByDescending(x => x.Message.CreatedAt)
            .ThenByDescending(x => x.MessageId)
            .Take(pageSize + 1)
            .Select(x => new
            {
                x.MessageId,
                x.Message.Category,
                x.Message.Title,
                x.Message.Content,
                x.Message.ActionType,
                x.Message.ActionDataJson,
                x.Message.CreatedAt,
                x.ReadAt
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        var pageRows = rows.Take(pageSize).ToArray();
        var items = pageRows.Select(x => new MessageSummary(
            x.MessageId,
            x.Category,
            x.Title,
            x.Content,
            x.ActionType,
            ParseJson(x.ActionDataJson),
            x.CreatedAt,
            x.ReadAt)).ToArray();
        var nextCursor = hasMore && pageRows.Length > 0
            ? EncodeCursor(pageRows[^1].CreatedAt, pageRows[^1].MessageId)
            : null;
        return new MessagePage(items, nextCursor);
    }

    public async Task<MessageDetail?> GetAsync(Guid userId, Guid messageId, CancellationToken cancellationToken)
    {
        var row = await dbContext.MessageRecipients
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.MessageId == messageId)
            .Select(x => new
            {
                x.MessageId,
                x.Message.Category,
                x.Message.TemplateCode,
                x.Message.Title,
                x.Message.Content,
                x.Message.SourceApp,
                x.Message.SourceEvent,
                x.Message.EntityType,
                x.Message.EntityId,
                x.Message.ActionType,
                x.Message.ActionDataJson,
                x.Message.CreatedAt,
                x.DispatchedAt,
                x.DeliveredAt,
                x.ReadAt
            })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : new MessageDetail(
            row.MessageId,
            row.Category,
            row.TemplateCode,
            row.Title,
            row.Content,
            row.SourceApp,
            row.SourceEvent,
            row.EntityType,
            row.EntityId,
            row.ActionType,
            ParseJson(row.ActionDataJson),
            row.CreatedAt,
            row.DispatchedAt,
            row.DeliveredAt,
            row.ReadAt);
    }

    public Task<long> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.MessageRecipients.LongCountAsync(x => x.UserId == userId && x.ReadAt == null, cancellationToken);

    public async Task<bool> MarkReadAsync(
        Guid userId,
        Guid messageId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.MessageRecipients
            .SingleOrDefaultAsync(x => x.UserId == userId && x.MessageId == messageId, cancellationToken);
        if (row is null)
        {
            return false;
        }
        if (row.ReadAt is null)
        {
            row.ReadAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    public async Task<bool> MarkDeliveredAsync(
        Guid userId,
        Guid messageId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.MessageRecipients
            .SingleOrDefaultAsync(x => x.UserId == userId && x.MessageId == messageId, cancellationToken);
        if (row is null)
        {
            return false;
        }
        if (row.DeliveredAt is null)
        {
            row.DeliveryStatus = DeliveryStatus.Delivered;
            row.DispatchedAt ??= now;
            row.DeliveredAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    public async Task<int> MarkAllReadAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await dbContext.MessageRecipients
            .Where(x => x.UserId == userId && x.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ReadAt, now), cancellationToken);

    public async Task<DeviceResponse> RegisterAsync(
        Guid userId,
        RegisterDeviceRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var platform = request.Platform.Trim().ToLowerInvariant();
        if (platform is not ("android" or "ios"))
        {
            throw new ArgumentException("Platform must be android or ios.", nameof(request));
        }
        var token = request.PushToken.Trim();
        if (token.Length is < 16 or > 4096)
        {
            throw new ArgumentException("PushToken length is invalid.", nameof(request));
        }
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var device = await dbContext.UserDevices
            .SingleOrDefaultAsync(x => x.UserId == userId && x.PushTokenHash == tokenHash, cancellationToken);
        if (device is null)
        {
            device = new UserDevice
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Platform = platform,
                PushTokenHash = tokenHash,
                ProtectedPushToken = tokenProtector.Protect(token),
                CreatedAt = now
            };
            dbContext.UserDevices.Add(device);
        }
        device.Platform = platform;
        device.ProtectedPushToken = tokenProtector.Protect(token);
        device.DeviceName = Normalize(request.DeviceName, 160);
        device.AppVersion = Normalize(request.AppVersion, 40);
        device.IsActive = true;
        device.RevokedAt = null;
        device.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new DeviceResponse(device.Id, device.Platform, device.DeviceName, device.AppVersion, device.UpdatedAt);
    }

    public async Task<bool> RevokeAsync(
        Guid userId,
        Guid deviceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var device = await dbContext.UserDevices
            .SingleOrDefaultAsync(x => x.UserId == userId && x.Id == deviceId, cancellationToken);
        if (device is null)
        {
            return false;
        }
        device.IsActive = false;
        device.RevokedAt = now;
        device.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string? Normalize(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static JsonElement? ParseJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string EncodeCursor(DateTimeOffset createdAt, Guid messageId)
    {
        var raw = $"{createdAt:O}|{messageId:D}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    private static bool TryDecodeCursor(string? cursor, out DateTimeOffset createdAt, out Guid messageId)
    {
        createdAt = default;
        messageId = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var separator = raw.LastIndexOf('|');
            return separator > 0 &&
                   DateTimeOffset.TryParse(raw[..separator], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out createdAt) &&
                   Guid.TryParse(raw[(separator + 1)..], out messageId);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
