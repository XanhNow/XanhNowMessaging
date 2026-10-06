# XanhNowMessaging

`XanhNowMessaging` is the independent system-message and scheduled-reminder app for XanhNow. It is not a person-to-person chat service and does not reference Trip, DirectChat, TripChat, or ComplaintChat projects.

## Boundaries

- Consumes authoritative domain events from Kafka.
- Stores messages, recipients, read state, schedules, Inbox, Outbox, delivery attempts, devices, and entity event positions in PostgreSQL database `authtest`, schema `s101_xanhnow_messaging`.
- Uses the shared XanhNow Kafka and Redis credentials. Logical names remain isolated through the `s101` namespace.
- Accepts user HTTP and SignalR traffic only from `XanhNow_Security_App` through the dedicated Messaging boundary. Messaging does not hold the Security JWT signing key or read Security sessions.
- Publishes committed messages to Flutter through SignalR at `/messagingHub`.
- Does not query or mutate another app's database.

## Projects

- `XanhNow.Messaging.Contracts`: Kafka and API contracts.
- `XanhNow.Messaging.Domain`: persistent domain models and states.
- `XanhNow.Messaging.Application`: mailbox and device use cases.
- `XanhNow.Messaging.Infrastructure`: EF Core, Kafka consumer/DLQ, scheduler, Inbox/Outbox, delivery worker.
- `XanhNow.Messaging.Api`: HTTP API, Security authentication, SignalR and hosted workers.
- `XanhNow.Messaging.Migrator`: isolated EF migration runner.

## Implemented event contracts

- `TripAccepted`: immediate acceptance notification, a reminder at pickup time, and a completion reminder two hours after pickup while the trip remains incomplete.
- `TripUpdated`: notification and reminder reschedule when `pickupAtUtc` is present.
- `TripCompleted`: completion notification and cancellation of outstanding reminders.
- `TripCancelled`: cancellation notification and cancellation of outstanding reminders.
- `SystemNotificationRequested`: trusted backend-only generic system notification.

All events require `SchemaVersion=1`, `Environment`, authoritative `Recipients`, a stable `BusinessKey`, and `EntityVersion` or `SequenceNumber` when the source stream has ordering.

## HTTP API

- `GET /api/v1/messaging/messages`
- `GET /api/v1/messaging/messages/{messageId}`
- `GET /api/v1/messaging/messages/unread-count`
- `PUT /api/v1/messaging/messages/{messageId}/read`
- `PUT /api/v1/messaging/messages/read-all`
- `POST /api/v1/messaging/devices`
- `DELETE /api/v1/messaging/devices/{deviceId}`

The SignalR client must call `AcknowledgeDelivered(messageId)` after receiving `MessageCreated`. This separates dispatched, delivered, and read states.

## Build and test

```powershell
dotnet restore .\XanhNowMessaging.slnx
dotnet build .\XanhNowMessaging.slnx --configuration Release --no-restore
dotnet test .\XanhNowMessaging.slnx --configuration Release --no-build --no-restore
```

## External prerequisite not faked

FCM/APNs credentials and provider infrastructure do not currently exist in the supplied XanhNow environment. Device registration, encrypted token storage, and the push gateway interface are implemented, but the production push adapter remains disabled until real provider credentials and policies are supplied. SignalR, durable history, scheduling, and retry do not depend on that adapter.
