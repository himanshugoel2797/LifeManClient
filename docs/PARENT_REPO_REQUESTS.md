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

## Add UnifiedPush transport (server side)

**Where:** new endpoints + outbound integration on the kernel:

- `POST /api/devices/push-token` →
  body: `{ "transport": "unifiedpush", "token": "<endpoint-url>" }`.
  The `token` field carries the device's UnifiedPush endpoint URL
  (an HTTPS URL the kernel will POST wake messages to). Stores the
  endpoint on the device row keyed by the bearer-token's device id.
  200 / 204 on success. The client treats 404 / 501 as "endpoint
  not yet shipped" and skips silently.
- `DELETE /api/devices/push-token` — clears the endpoint. The
  client calls this when the user removes the UnifiedPush
  distributor or disables push.
- Server-side UnifiedPush publisher: when a new output event is
  queued for a device with a registered endpoint AND the device has
  no live SSE connection, the kernel POSTs to the endpoint URL per
  the UnifiedPush spec (RFC 8030 Web Push). Body should be empty
  (or contain the output id only) — the client wakes, calls
  `GET /api/outputs/pending?since=…` to drain.

**Why UnifiedPush (not FCM):** UnifiedPush is a Google-free push
spec. The phone picks its own distributor (ntfy, NextPush, Gotify,
FCM-UP for users who do want Google) and hands the app an HTTPS
endpoint. The kernel only needs to POST to that URL — no Firebase
project, no service-account JSON in the kernel's secrets store, no
Play Services dependency, and the kernel never talks to a Google
API. Works on de-Googled phones (CalyxOS / GrapheneOS / LineageOS
without microG), which is most of the target audience for a
self-hosted lifeman.

**Why a push transport at all:** CLIENT_DESIGN.md §"Inbound: output
delivery" calls cellular SSE "battery-expensive". Without a wake
transport the Android client either keeps a long-lived SSE
connection over the radio (battery shred) or polls (latency +
battery). UnifiedPush lets the device sleep until something
actually happens.

**Operational note:** the kernel needs an HTTP client capable of
POSTing to arbitrary HTTPS URLs, plus optionally Web Push
encryption + VAPID if you don't want the distributor to see payload
contents. A minimal first cut can ship unencrypted wake-only pings
— the real payload still travels over the authenticated SSE /
pending pull, so the wake itself has no secret to leak. Encryption
can be added later without a client change if the client treats
unknown encrypted payloads as "just wake and pull anyway".

**Affected client code:**
`src/Lifeman.Client/Net/UnifiedPushRegistration.cs` ships the
registration `POST` + `DELETE` today. The Android-side endpoint
acquisition (UnifiedPush distributor binding — a `BroadcastReceiver`
subclass per the UnifiedPush Android library) and the wake-handler
service are NOT implemented yet; they're a single-file follow-on
once the server endpoint exists.
