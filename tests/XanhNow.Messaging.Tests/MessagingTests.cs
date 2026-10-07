using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;
using XanhNow.Messaging.Api;
using XanhNow.Messaging.Application;
using XanhNow.Messaging.Infrastructure;

namespace XanhNow.Messaging.Tests;

public sealed class MessagingTests
{
    [Fact]
    public void IdentityClaims_reads_subject_guid()
    {
        var expected = Guid.NewGuid();
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("sub", expected.ToString("D"))]));

        Assert.True(IdentityClaims.TryGetUserId(principal, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("0901234567", "+84901234567")]
    [InlineData("84901234567", "+84901234567")]
    public void Security_boundary_normalizes_trusted_phone(
        string input,
        string expected)
    {
        Assert.True(
            MessagingSecurityBoundaryAuthenticationHandler.TryNormalizePhone(
                input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("TRIP_PICKUP_REMINDER", "Sắp đến giờ đón khách")]
    [InlineData("TRIP_COMPLETION_REMINDER", "Nhắc hoàn thành lịch xe")]
    public void Scheduler_renders_supported_templates(string template, string expectedTitle)
    {
        var payload = JsonSerializer.Serialize(new { pickupAtUtc = "2026-10-06T10:00:00Z" });
        var rendered = ScheduledMessageWorker.Render(template, payload);
        Assert.Equal(expectedTitle, rendered.Title);
        Assert.False(string.IsNullOrWhiteSpace(rendered.Content));
    }

    [Fact]
    public void Scheduler_uses_two_hours_as_default_completion_reminder_delay()
    {
        var options = new SchedulerOptions();

        Assert.Equal(TimeSpan.FromHours(2), options.CompletionReminderDelay);
    }

    [Fact]
    public void Trip_reminders_use_pickup_and_pickup_plus_configured_delay()
    {
        var pickupAt = new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(pickupAt, MessagingEventProcessor.GetPickupReminderAt(pickupAt));
        Assert.Equal(
            pickupAt.AddHours(2),
            MessagingEventProcessor.GetCompletionReminderAt(pickupAt, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Initial_migration_is_discovered_in_messaging_context()
    {
        var options = new DbContextOptionsBuilder<XanhNowMessagingDbContext>()
            .UseNpgsql("Host=localhost;Database=authtest;Username=test;Password=test")
            .Options;
        using var context = new XanhNowMessagingDbContext(options);

        Assert.Contains("20261006010000_InitialMessaging", context.Database.GetMigrations());
    }

    [Fact]
    public void Kafka_topics_are_trimmed_deduplicated_and_empty_values_are_removed()
    {
        var topics = KafkaConsumerWorker.NormalizeTopics(
            [" s101.xanhnow.trip.events ", "", "s101.xanhnow.trip.events", "s101.xanhnow.membership.events"]);

        Assert.Equal(
            ["s101.xanhnow.trip.events", "s101.xanhnow.membership.events"],
            topics);
    }

    [Fact]
    public void Device_token_protector_encrypts_and_round_trips()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()));
            var options = Options.Create(new MessagingSecurityOptions
            {
                DeviceTokenEncryptionKeyFile = path
            });
            var protector = new AesDeviceTokenProtector(options);
            const string token = "device-token-that-must-not-be-stored-in-plaintext";

            var protectedToken = protector.Protect(token);

            Assert.DoesNotContain(token, protectedToken, StringComparison.Ordinal);
            Assert.Equal(token, protector.Unprotect(protectedToken));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
