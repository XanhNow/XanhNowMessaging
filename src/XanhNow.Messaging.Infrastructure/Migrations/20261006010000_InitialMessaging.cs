using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace XanhNow.Messaging.Infrastructure.Migrations;

[DbContext(typeof(XanhNowMessagingDbContext))]
[Migration("20261006010000_InitialMessaging")]
public sealed class InitialMessaging : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            create schema if not exists s101_xanhnow_messaging;

            create table if not exists s101_xanhnow_messaging.messages (
                message_id uuid primary key,
                business_key varchar(300) not null unique,
                category varchar(80) not null,
                template_code varchar(120) not null,
                title text not null,
                content text not null,
                source_app varchar(120) not null,
                source_event varchar(160) not null,
                entity_type varchar(100) not null,
                entity_id varchar(160) not null,
                action_type varchar(80),
                action_data jsonb,
                created_at timestamptz not null
            );

            create index if not exists ix_messages_entity_created
                on s101_xanhnow_messaging.messages(entity_type, entity_id, created_at desc);

            create table if not exists s101_xanhnow_messaging.message_recipients (
                message_id uuid not null references s101_xanhnow_messaging.messages(message_id) on delete cascade,
                user_id uuid not null,
                phone_number_snapshot varchar(32),
                delivery_status varchar(24) not null,
                dispatched_at timestamptz,
                delivered_at timestamptz,
                read_at timestamptz,
                created_at timestamptz not null,
                primary key(message_id, user_id)
            );

            create index if not exists ix_recipients_mailbox
                on s101_xanhnow_messaging.message_recipients(user_id, read_at, created_at desc);

            create table if not exists s101_xanhnow_messaging.scheduled_messages (
                schedule_id uuid primary key,
                user_id uuid not null,
                template_code varchar(120) not null,
                business_key varchar(300) not null,
                category varchar(80) not null,
                source_app varchar(120) not null,
                source_event varchar(160) not null,
                entity_type varchar(100) not null,
                entity_id varchar(160) not null,
                payload jsonb not null,
                scheduled_at timestamptz not null,
                status varchar(24) not null,
                attempt_count integer not null default 0,
                next_attempt_at timestamptz,
                lease_expires_at timestamptz,
                lease_token uuid,
                created_at timestamptz not null,
                updated_at timestamptz not null,
                unique(business_key, user_id, template_code)
            );

            create index if not exists ix_schedules_due
                on s101_xanhnow_messaging.scheduled_messages(status, scheduled_at, next_attempt_at);

            create table if not exists s101_xanhnow_messaging.message_inbox_events (
                event_id uuid not null,
                consumer_name varchar(160) not null,
                source_app varchar(120) not null,
                event_type varchar(160) not null,
                status varchar(24) not null,
                error varchar(1000),
                processed_at timestamptz not null,
                primary key(event_id, consumer_name)
            );

            create table if not exists s101_xanhnow_messaging.message_outbox_events (
                outbox_event_id uuid primary key,
                message_id uuid not null references s101_xanhnow_messaging.messages(message_id) on delete cascade,
                user_id uuid not null,
                channel varchar(32) not null,
                status varchar(24) not null,
                attempt_count integer not null default 0,
                next_attempt_at timestamptz,
                lease_expires_at timestamptz,
                lease_token uuid,
                last_error varchar(1000),
                created_at timestamptz not null,
                updated_at timestamptz not null,
                unique(message_id, user_id, channel)
            );

            create index if not exists ix_outbox_pending
                on s101_xanhnow_messaging.message_outbox_events(status, next_attempt_at);

            create table if not exists s101_xanhnow_messaging.message_delivery_attempts (
                delivery_attempt_id bigint generated always as identity primary key,
                outbox_event_id uuid not null,
                channel varchar(32) not null,
                succeeded boolean not null,
                provider_code varchar(120),
                error varchar(1000),
                attempted_at timestamptz not null
            );

            create index if not exists ix_delivery_attempt_outbox
                on s101_xanhnow_messaging.message_delivery_attempts(outbox_event_id);

            create table if not exists s101_xanhnow_messaging.user_devices (
                device_id uuid primary key,
                user_id uuid not null,
                platform varchar(24) not null,
                push_token_hash varchar(64) not null,
                protected_push_token text not null,
                device_name varchar(160),
                app_version varchar(40),
                is_active boolean not null,
                created_at timestamptz not null,
                updated_at timestamptz not null,
                revoked_at timestamptz,
                unique(user_id, push_token_hash)
            );

            create table if not exists s101_xanhnow_messaging.event_positions (
                source_app varchar(120) not null,
                entity_type varchar(100) not null,
                entity_id varchar(160) not null,
                position bigint not null,
                last_event_id uuid not null,
                updated_at timestamptz not null,
                primary key(source_app, entity_type, entity_id)
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop schema if exists s101_xanhnow_messaging cascade;");
    }
}

