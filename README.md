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

## VietQR payment notification

The payment-specific `SystemNotificationRequested` contract is in
`XanhNow.Messaging.Contracts/S101VietQrPaymentNotification.cs`. Membership references
Contracts only, never the Messaging host, Infrastructure or DbContext.

- Payment topic: `s101.xanhnow.membership_payment.events`. Append it to the deployed
  Kafka topic list without removing existing Trip/Membership subscriptions.
  This is not evidence of live topic existence or ACL grants. Existing unrelated
  Membership event topic names are not renamed by this integration.
- Source: `XanhNow.Membership`; template: `S101_VIETQR_PAYMENT_CREDITED`;
  category: `payment`; action: `OpenPayment`; entity: `s101VietQrPurchase`.
- Membership persists this notification intent in its existing outbox transaction
  with completed points/history/job. The existing Membership outbox publisher sends
  it after commit. Kafka key is the purchase ID; EventId is the stable fulfillment
  job ID; BusinessKey is `s101:membership:payment:<order-id-N>:credited`.
- Recipients contain only the authoritative purchaser. Messaging verifies the
  recipient, source, purchase ID, BusinessKey and version. EventId/inbox and the
  existing unique message BusinessKey prevent duplicate materialization. A second
  event with the same business key cannot overwrite different recipients/content.
- The existing mailbox/recipient and delivery outbox commit before Kafka offset
  acknowledgement. Database failures keep retrying the same record with a fresh
  scope; later offsets must not silently acknowledge an unprocessed record.
  A failed DLQ publish or offset acknowledgement retains/replays the source record.
- Payment delivery retries the SAME MessageId beyond the normal attempt limit,
  with capped backoff. It never calls Membership or runs points credit again.
  A successful SignalR dispatch stays Pending until the matching recipient has
  DeliveredAt or ReadAt. Missing receipts retry after 30 seconds, backing off to
  five minutes; a receipt completes the work without another dispatch. Other
  notification types retain their previous dispatch-completes behavior.
  Lease ownership uses optimistic concurrency on the existing lease_token column;
  no column/table or migration is added. Delivered is not downgraded to Dispatched.
- Redis remains the existing SignalR backplane with the existing s101 namespace and
  credential files. It is not a financial queue, points ledger or durable message
  store. Redis/SignalR outage leaves mailbox history and delivery work in PostgreSQL.
  No new Redis credential, cluster, key store or connection is provisioned.

The in-app message includes amount, bank code, credited points, remaining points
and purchase code. No full bank account, phone, OTP, proof or credential is included.
Payment events use Production only. Messaging's configured Environment must match;
no alternate Dev/Test deployment or environment fallback is added. Existing shared
Kafka/Redis credentials and Vault/Agent are unchanged.

Runtime acceptance is still pending: verify topic publish/consume ACLs, matching
environment and Redis backplane configuration, commit loss/redelivery/restart,
mailbox ownership and phone delivery. The existing Flutter mailbox reads history
through Security; Flutter's separate payment notification module receives realtime
and sends delivery ACK through Security. FCM/APNs remains disabled.

The SignalR Redis dependency previously resolved MessagePack 2.5.302 and reported
NU1902 (GHSA-qhrr-8q5h-9q3h). The separately approved remediation pins MessagePack
2.5.303 in Messaging.Api, the patched release on the same 2.5 branch. SignalR and
Redis package versions, runtime contracts and configuration remain unchanged.
NuGet audit and the repository warnings-as-errors policy remain enabled; normal
build/test must run without the earlier command-only WarningsNotAsErrors override.
This dependency fix is not deployment or live Redis/Kafka/phone acceptance.

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
