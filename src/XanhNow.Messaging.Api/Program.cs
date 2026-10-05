using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using XanhNow.Messaging.Api;
using XanhNow.Messaging.Application;
using XanhNow.Messaging.Contracts;
using XanhNow.Messaging.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));
builder.Services.Configure<SchedulerOptions>(builder.Configuration.GetSection(SchedulerOptions.SectionName));
builder.Services.Configure<DeliveryOptions>(builder.Configuration.GetSection(DeliveryOptions.SectionName));
builder.Services.Configure<RealtimeOptions>(builder.Configuration.GetSection(RealtimeOptions.SectionName));
builder.Services.AddXanhNowSecurity(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);

var database = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
    ?? new DatabaseOptions();
var connectionString = !string.IsNullOrWhiteSpace(database.ConnectionStringFile)
    ? SecretFile.ReadRequired(database.ConnectionStringFile, "XanhNowMessaging database")
    : database.ConnectionString;
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("XanhNowMessaging database connection is required.");
}
builder.Services.AddDbContext<XanhNowMessagingDbContext>(options => options.UseNpgsql(
    connectionString,
    npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", XanhNowMessagingDbContext.Schema)));
builder.Services.AddScoped<MailboxRepository>();
builder.Services.AddScoped<IMailboxRepository>(provider => provider.GetRequiredService<MailboxRepository>());
builder.Services.AddScoped<IDeviceRegistry>(provider => provider.GetRequiredService<MailboxRepository>());
builder.Services.AddScoped<MailboxService>();
builder.Services.AddScoped<MessagingEventProcessor>();
builder.Services.AddSingleton<KafkaDeadLetterPublisher>();
builder.Services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();
builder.Services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();
builder.Services.AddSingleton<IPushGateway, DisabledPushGateway>();

var signalR = builder.Services.AddSignalR().AddJsonProtocol();
var realtime = builder.Configuration.GetSection(RealtimeOptions.SectionName).Get<RealtimeOptions>()
    ?? new RealtimeOptions();
if (realtime.RedisBackplaneEnabled)
{
    var redisConfiguration = ConfigurationOptions.Parse(
        SecretFile.ReadRequired(realtime.RedisConfigurationFile, "Redis configuration"));
    redisConfiguration.Password = SecretFile.ReadRequired(realtime.RedisPasswordFile, "Redis password");
    redisConfiguration.AbortOnConnectFail = false;
    redisConfiguration.ChannelPrefix = RedisChannel.Literal(realtime.ChannelPrefix);
    signalR.AddStackExchangeRedis(options => options.Configuration = redisConfiguration);
}

builder.Services.AddHostedService<KafkaConsumerWorker>();
builder.Services.AddHostedService<ScheduledMessageWorker>();
builder.Services.AddHostedService<OutboxDeliveryWorker>();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

var messages = app.MapGroup("/api/v1/messaging/messages").RequireAuthorization();
messages.MapGet("/", async (
    HttpContext context,
    MailboxService service,
    string? cursor,
    int pageSize,
    string? category,
    bool? unreadOnly,
    CancellationToken cancellationToken) =>
{
    if (!IdentityClaims.TryGetUserId(context.User, out var userId))
    {
        return Results.Unauthorized();
    }
    return Results.Ok(await service.GetPageAsync(
        userId,
        cursor,
        pageSize <= 0 ? 30 : pageSize,
        category,
        unreadOnly,
        cancellationToken));
});
messages.MapGet("/{messageId:guid}", async (
    Guid messageId,
    HttpContext context,
    MailboxService service,
    CancellationToken cancellationToken) =>
{
    if (!IdentityClaims.TryGetUserId(context.User, out var userId))
    {
        return Results.Unauthorized();
    }
    var message = await service.GetAsync(userId, messageId, cancellationToken);
    return message is null ? Results.NotFound() : Results.Ok(message);
});
messages.MapGet("/unread-count", async (
    HttpContext context,
    MailboxService service,
    CancellationToken cancellationToken) =>
{
    if (!IdentityClaims.TryGetUserId(context.User, out var userId))
    {
        return Results.Unauthorized();
    }
    return Results.Ok(new UnreadCountResponse(await service.GetUnreadCountAsync(userId, cancellationToken)));
});
messages.MapPut("/{messageId:guid}/read", async (
    Guid messageId,
    HttpContext context,
    MailboxService service,
    CancellationToken cancellationToken) =>
{
    if (!IdentityClaims.TryGetUserId(context.User, out var userId))
    {
        return Results.Unauthorized();
    }
    return await service.MarkReadAsync(userId, messageId, cancellationToken)
        ? Results.NoContent()
        : Results.NotFound();
});
messages.MapPut("/read-all", async (
    HttpContext context,
    MailboxService service,
    CancellationToken cancellationToken) =>
{
    if (!IdentityClaims.TryGetUserId(context.User, out var userId))
    {
        return Results.Unauthorized();
    }
    return Results.Ok(new { updated = await service.MarkAllReadAsync(userId, cancellationToken) });
});

var devices = app.MapGroup("/api/v1/messaging/devices").RequireAuthorization();
devices.MapPost("/", async (
    RegisterDeviceRequest request,
    HttpContext context,
    IDeviceRegistry registry,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    if (!IdentityClaims.TryGetUserId(context.User, out var userId))
    {
        return Results.Unauthorized();
    }
    try
    {
        return Results.Ok(await registry.RegisterAsync(
            userId,
            request,
            timeProvider.GetUtcNow(),
            cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});
devices.MapDelete("/{deviceId:guid}", async (
    Guid deviceId,
    HttpContext context,
    IDeviceRegistry registry,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    if (!IdentityClaims.TryGetUserId(context.User, out var userId))
    {
        return Results.Unauthorized();
    }
    return await registry.RevokeAsync(userId, deviceId, timeProvider.GetUtcNow(), cancellationToken)
        ? Results.NoContent()
        : Results.NotFound();
});

app.MapHub<MessagingHub>("/messagingHub");
app.MapGet("/health/live", () => Results.Ok(new { service = "XanhNowMessaging", status = "Healthy" }));
app.MapGet("/health/ready", async (XanhNowMessagingDbContext db, CancellationToken cancellationToken) =>
{
    var databaseReady = await db.Database.CanConnectAsync(cancellationToken);
    return databaseReady
        ? Results.Ok(new { service = "XanhNowMessaging", status = "Healthy", database = "connected" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.Run();

public partial class Program;
