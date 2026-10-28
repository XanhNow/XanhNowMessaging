# Messaging FK fix — owner-approved rollout

Source commit: `8e0e298d1346d6da7172c18d8f353821e9150954`.
Only application source change: map the existing FK from delivery outbox to Message
so EF orders INSERTs correctly. No schema change or migration is required.

Regression evidence: the original processor reproduced PostgreSQL 23503 at
`message_outbox_events_message_id_fkey` on disposable localhost PostgreSQL. The
fixed processor commits one message/recipient/outbox, reads it only for its owner,
and deduplicates both the same EventId and a new EventId with the same business key.
The existing 23 tests passed; the opt-in PostgreSQL test was run separately.

## Owner command

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "C:\Users\xanhn\OneDrive\Documents\ChatGPT\Chat\s101-vietqr-release\deploy-messaging-fk.ps1" `
  -Apply
```

Without `-Apply`, the script runs read-only preflight on all three nodes. It still
requires SSH/sudo passwords; it does not upload or activate anything in that mode.
This preparation session does not execute either remote mode.

## Fixed scope

- Targets: `192.168.2.25`, `.38`, `.65`.
- All-node preflight before any upload/restart; activation sequentially.
- New immutable Messaging API release under `/opt/s101-xanhnow-messaging/fk-order/`.
- Add only `zzzzzz-s101-messaging-fk-order.conf` to the existing Messaging unit's
  drop-in directory, resetting ExecStart to the new API. Other unit directives,
  WorkingDirectory/content root, appsettings and existing environment are preserved.
- Archive excludes appsettings, migrator, credential files and deploy installers.
  The legacy installer is not invoked: it can provision units/run a migrator and
  does not fit this approved Messaging-only change.
- Restart ONLY `xanhnow-messaging-api.service`. A daemon-reload loads that override.
- Compare existing credential/config fingerprints, other unit files, and all
  protected service PID/InvocationID states across activation. No values printed.
- Health checks are unauthenticated loopback GET `/health/live` and `/health/ready`;
  readiness checks the existing DB connection without applying migrations.
- No direct SQL, migration, provider/Test Callback call, Kafka offset reset or
  manual event replay. The normal Messaging consumer may resume queued work and
  deliver notifications after restart; this is expected application behavior.
- Registration/login, Security, Membership/worker, five children, credentials,
  and Vault/Agents are not deployed, edited or restarted by this script.

## Failure behavior

Local deployment receipt is created exclusively and preserved. Each node gets a
root-only before-state record and activation confirmation. No automatic retry or
rollback. If any step fails, keep the receipt and output to determine exactly which
nodes activated. Do not rerun `-Apply` or delete receipts to force another attempt.

The old release is retained. This script does not switch or remove the old
`/srv/xanhnow/apps/messaging/current` symlink; its content root/config remains in
use. Future deployments must account for the new ExecStart override rather than
assuming that changing the old symlink changes the running code.

The rollout confirms version files, process/config preservation and health only.
Verify the three existing payment notifications on the owner's phone afterwards;
do not resend payment callbacks to compensate for a notification failure.

Binary ZIP is retained in the sealed local package, not committed to GitHub. The manifest and helper source are committed for review; running from another location requires the same verified ZIP package.
