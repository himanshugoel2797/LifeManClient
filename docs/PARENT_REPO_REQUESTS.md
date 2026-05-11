# Parent-repo requests

Things this submodule needs from the kernel side. Per CLAUDE.md, do not
change the parent repo from this submodule — file the request here and
let the user shepherd it through with proper review and tests.

## Add `delivered_at` to `output.deliver` payloads

**Where:** the `data` blob of `output.deliver` SSE events (and the
identical shape returned by `GET /api/outputs/pending`).

**Why:** the client uses `pending.cursor` (a `delivered_at` timestamp)
to ask `/api/outputs/pending?since=<cursor>` for events missed during a
disconnect. The cursor needs to advance as live SSE events arrive,
otherwise reconnecting after a long live session re-fetches everything.

Today the client falls back to `DateTimeOffset.UtcNow` at receive time,
which is best-effort but suffers two problems:

- Clock skew between device and server can either skip events
  (device clock ahead → cursor in the future) or duplicate them
  (device clock behind → cursor in the past).
- A late-arriving event whose true `delivered_at` predates a cursor
  set by an earlier-received-but-later-delivered event would be missed
  on next reconnect.

Adding the server-authoritative `delivered_at` to the payload (same
ISO-8601 with `+00:00` offset that `/pending` already returns as
`cursor`) lets the client advance the cursor correctly without
guessing. No new endpoints required.

**Affected client code:** `SseReceiver.DispatchAsync` in
`src/Lifeman.Client/Net/SseReceiver.cs`.

## Add `/api/system/client-updates/<platform>` endpoint

**Where:** new HTTP endpoints on the kernel:

- `GET /api/system/client-updates/{platform}` →
  `{ "version": "1.4.0", "sha256": "…", "download_url": "…", "notes": "…" }`
  (`notes` optional). 404 means "no published build for this platform"
  and the client treats that as "no update available".
- `GET /api/system/client-updates/{platform}/download` (optional —
  `download_url` may point at any static host). Returns the binary.

**Why:** CLIENT_DESIGN.md §"Distribution & updates" specifies a weekly
client poll for new builds. The client-side poller is already wired up
in `src/Lifeman.Client/Updates/UpdateChecker.cs` and runs on Windows +
Android; it currently logs at debug and skips when the endpoint returns
404 / DNS failure, so shipping the endpoint flips updates on without
any further client change.

`platform` values the client sends today: `windows`, `android`.

**Affected client code:** `UpdateChecker.CheckOnceAsync` in
`src/Lifeman.Client/Updates/UpdateChecker.cs`. The renderer-side
notification is already shaped (`category=alert urgency=soft`).

## Add FCM push transport (server side)

**Where:** new endpoints + outbound integration on the kernel:

- `POST /api/devices/push-token` →
  body: `{ "transport": "fcm", "token": "<fcm-registration-token>" }`.
  Stores the token on the device row keyed by the bearer-token's
  device id. 200 / 204 on success. The client treats 404 / 501 as
  "endpoint not yet shipped" and skips silently.
- `DELETE /api/devices/push-token` (optional) — used when the user
  wipes the FCM token (uninstall, app data clear).
- Server-side FCM publisher: when a new output event is queued for a
  device with an FCM token AND the device has no live SSE connection,
  publish a wake-only data message via FCM. Payload should be empty
  (or contain the output id only) — the client wakes, calls
  `GET /api/outputs/pending?since=…` to drain.

**Why:** CLIENT_DESIGN.md §"Inbound: output delivery" specifies FCM as
the cellular-friendly fallback for SSE; "battery-expensive on
cellular" is the design's words. Without FCM the Android client
either keeps a long-lived SSE connection over the radio (battery
shred) or polls (latency + battery). FCM lets the device sleep until
something actually happens.

**Operational note:** running FCM requires the server-side maintainer
to register a Firebase project and embed the service-account JSON in
the kernel's secrets store. The kernel-side integration can use the
official `Firebase.Admin` SDK (Python: `firebase-admin` PyPI). No
client-side Google account is needed beyond standard Play Services on
the device.

**Affected client code:** `src/Lifeman.Client/Net/FcmRegistration.cs`
ships the registration POST today. The Android-side token acquisition
(Firebase.Messaging binding) and the wake-handler service are NOT
implemented yet — they're a single-file follow-on (one
`FirebaseMessagingService` subclass) once the server endpoint exists.
