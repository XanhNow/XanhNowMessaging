namespace XanhNow.Messaging.Infrastructure;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public string ConnectionString { get; set; } = string.Empty;
    public string ConnectionStringFile { get; set; } = string.Empty;
}

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";
    public bool Enabled { get; set; } = true;
    public string[] BootstrapServers { get; set; } = [];
    public string ClientId { get; set; } = "s101-xanhnow-messaging";
    public string ConsumerGroup { get; set; } = "s101.xanhnow.messaging";
    public string[] Topics { get; set; } = ["s101.xanhnow.trip.events", "s101.xanhnow.membership.events"];
    public string DeadLetterTopic { get; set; } = "s101.xanhnow.messaging.dlq";
    public string SecurityProtocol { get; set; } = "SASL_SSL";
    public string SaslMechanism { get; set; } = "SCRAM-SHA-512";
    public string Username { get; set; } = string.Empty;
    public string PasswordFile { get; set; } = string.Empty;
    public string CaFile { get; set; } = string.Empty;
    public string Environment { get; set; } = "production";
}

public sealed class SchedulerOptions
{
    public const string SectionName = "Scheduler";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan CompletionReminderDelay { get; set; } = TimeSpan.FromHours(2);
    public int BatchSize { get; set; } = 50;
    public int MaxAttempts { get; set; } = 10;
}

public sealed class DeliveryOptions
{
    public const string SectionName = "Delivery";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public int BatchSize { get; set; } = 100;
    public int MaxAttempts { get; set; } = 10;
}

public static class SecretFile
{
    public static string ReadRequired(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException($"{name} secret file does not exist.");
        }

        var value = File.ReadAllText(path).Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} secret file is empty.")
            : value;
    }
}
