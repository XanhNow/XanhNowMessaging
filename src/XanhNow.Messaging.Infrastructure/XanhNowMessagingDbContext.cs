using Microsoft.EntityFrameworkCore;
using XanhNow.Messaging.Domain;

namespace XanhNow.Messaging.Infrastructure;

public sealed class XanhNowMessagingDbContext(DbContextOptions<XanhNowMessagingDbContext> options)
    : DbContext(options)
{
    public const string Schema = "s101_xanhnow_messaging";

    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageRecipient> MessageRecipients => Set<MessageRecipient>();
    public DbSet<ScheduledMessage> ScheduledMessages => Set<ScheduledMessage>();
    public DbSet<InboxEvent> InboxEvents => Set<InboxEvent>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<UserDevice> UserDevices => Set<UserDevice>();
    public DbSet<EventPosition> EventPositions => Set<EventPosition>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Message>(entity =>
        {
            entity.ToTable("messages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("message_id");
            entity.Property(x => x.BusinessKey).HasColumnName("business_key").HasMaxLength(300);
            entity.Property(x => x.Category).HasColumnName("category").HasMaxLength(80);
            entity.Property(x => x.TemplateCode).HasColumnName("template_code").HasMaxLength(120);
            entity.Property(x => x.Title).HasColumnName("title");
            entity.Property(x => x.Content).HasColumnName("content");
            entity.Property(x => x.SourceApp).HasColumnName("source_app").HasMaxLength(120);
            entity.Property(x => x.SourceEvent).HasColumnName("source_event").HasMaxLength(160);
            entity.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(100);
            entity.Property(x => x.EntityId).HasColumnName("entity_id").HasMaxLength(160);
            entity.Property(x => x.ActionType).HasColumnName("action_type").HasMaxLength(80);
            entity.Property(x => x.ActionDataJson).HasColumnName("action_data").HasColumnType("jsonb");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(x => x.BusinessKey).IsUnique();
            entity.HasIndex(x => new { x.EntityType, x.EntityId, x.CreatedAt });
        });

        modelBuilder.Entity<MessageRecipient>(entity =>
        {
            entity.ToTable("message_recipients");
            entity.HasKey(x => new { x.MessageId, x.UserId });
            entity.Property(x => x.MessageId).HasColumnName("message_id");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.PhoneNumberSnapshot).HasColumnName("phone_number_snapshot").HasMaxLength(32);
            entity.Property(x => x.DeliveryStatus).HasColumnName("delivery_status").HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.DispatchedAt).HasColumnName("dispatched_at");
            entity.Property(x => x.DeliveredAt).HasColumnName("delivered_at");
            entity.Property(x => x.ReadAt).HasColumnName("read_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasOne(x => x.Message).WithMany(x => x.Recipients).HasForeignKey(x => x.MessageId);
            entity.HasIndex(x => new { x.UserId, x.ReadAt, x.CreatedAt });
        });

        modelBuilder.Entity<ScheduledMessage>(entity =>
        {
            entity.ToTable("scheduled_messages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("schedule_id");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.TemplateCode).HasColumnName("template_code").HasMaxLength(120);
            entity.Property(x => x.BusinessKey).HasColumnName("business_key").HasMaxLength(300);
            entity.Property(x => x.Category).HasColumnName("category").HasMaxLength(80);
            entity.Property(x => x.SourceApp).HasColumnName("source_app").HasMaxLength(120);
            entity.Property(x => x.SourceEvent).HasColumnName("source_event").HasMaxLength(160);
            entity.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(100);
            entity.Property(x => x.EntityId).HasColumnName("entity_id").HasMaxLength(160);
            entity.Property(x => x.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(x => x.ScheduledAt).HasColumnName("scheduled_at");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            entity.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            entity.Property(x => x.LeaseExpiresAt).HasColumnName("lease_expires_at");
            entity.Property(x => x.LeaseToken).HasColumnName("lease_token");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.BusinessKey, x.UserId, x.TemplateCode }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.ScheduledAt, x.NextAttemptAt });
        });

        modelBuilder.Entity<InboxEvent>(entity =>
        {
            entity.ToTable("message_inbox_events");
            entity.HasKey(x => new { x.EventId, x.ConsumerName });
            entity.Property(x => x.EventId).HasColumnName("event_id");
            entity.Property(x => x.ConsumerName).HasColumnName("consumer_name").HasMaxLength(160);
            entity.Property(x => x.SourceApp).HasColumnName("source_app").HasMaxLength(120);
            entity.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(160);
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(24);
            entity.Property(x => x.Error).HasColumnName("error").HasMaxLength(1000);
            entity.Property(x => x.ProcessedAt).HasColumnName("processed_at");
        });

        modelBuilder.Entity<OutboxEvent>(entity =>
        {
            entity.ToTable("message_outbox_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("outbox_event_id");
            entity.Property(x => x.MessageId).HasColumnName("message_id");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(32);
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            entity.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            entity.Property(x => x.LeaseExpiresAt).HasColumnName("lease_expires_at");
            entity.Property(x => x.LeaseToken).HasColumnName("lease_token");
            entity.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(1000);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.MessageId, x.UserId, x.Channel }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.NextAttemptAt });
        });

        modelBuilder.Entity<DeliveryAttempt>(entity =>
        {
            entity.ToTable("message_delivery_attempts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("delivery_attempt_id").UseIdentityAlwaysColumn();
            entity.Property(x => x.OutboxEventId).HasColumnName("outbox_event_id");
            entity.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(32);
            entity.Property(x => x.Succeeded).HasColumnName("succeeded");
            entity.Property(x => x.ProviderCode).HasColumnName("provider_code").HasMaxLength(120);
            entity.Property(x => x.Error).HasColumnName("error").HasMaxLength(1000);
            entity.Property(x => x.AttemptedAt).HasColumnName("attempted_at");
            entity.HasIndex(x => x.OutboxEventId);
        });

        modelBuilder.Entity<UserDevice>(entity =>
        {
            entity.ToTable("user_devices");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("device_id");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.Platform).HasColumnName("platform").HasMaxLength(24);
            entity.Property(x => x.PushTokenHash).HasColumnName("push_token_hash").HasMaxLength(64);
            entity.Property(x => x.ProtectedPushToken).HasColumnName("protected_push_token");
            entity.Property(x => x.DeviceName).HasColumnName("device_name").HasMaxLength(160);
            entity.Property(x => x.AppVersion).HasColumnName("app_version").HasMaxLength(40);
            entity.Property(x => x.IsActive).HasColumnName("is_active");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            entity.HasIndex(x => new { x.UserId, x.PushTokenHash }).IsUnique();
        });

        modelBuilder.Entity<EventPosition>(entity =>
        {
            entity.ToTable("event_positions");
            entity.HasKey(x => new { x.SourceApp, x.EntityType, x.EntityId });
            entity.Property(x => x.SourceApp).HasColumnName("source_app").HasMaxLength(120);
            entity.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(100);
            entity.Property(x => x.EntityId).HasColumnName("entity_id").HasMaxLength(160);
            entity.Property(x => x.Position).HasColumnName("position");
            entity.Property(x => x.LastEventId).HasColumnName("last_event_id");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });
    }
}

