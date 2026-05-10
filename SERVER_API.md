# Lifeman server API surface (client view)

The kernel endpoints this client talks to. All routes are under `/api/`
(except `/events` — the SSE stream — which is at the root).

Authentication: every request carries `Authorization: Bearer <token>`,
where `<token>` is the per-device token issued at pair time. The kernel
also has a *master* token used by the loopback web UI; the client is
**never** issued the master token. SSE uses `?token=…` instead of the
header because EventSource can't set custom headers.

`LIFEMAN_ALLOW_NETWORK=true` must be set on the kernel for device
tokens to be accepted from non-loopback peers; without it, the kernel
binds loopback-only and devices cannot reach it.

## Pairing

### `POST /api/auth/pair` — exchange a code for a device token

The new device has no credential yet; the pairing code (5-min TTL,
single-use, minted by the loopback caller via the `/system` UI) is the
credential here.

Request:
```json
{
  "code": "ABCDEFGH",
  "name": "Pixel 9",
  "platform": "android",
  "capabilities": {
    "rich_content": true,
    "images": false,
    "actions": true,
    "persistence": true,
    "interruption_level": "foreground",
    "typical_latency_ms": 1000
  }
}
```

Response (the `token` is returned exactly once — store it before the
process exits):
```json
{
  "device_id": "ee60f38e021aad9c",
  "name": "Pixel 9",
  "platform": "android",
  "token": "JTRkbS9NPdkXZUtR…",
  "created_at": "2026-05-10T22:03:20.444015+00:00"
}
```

Pairing also registers an output channel named `device:<device_id>` in
the kernel, so the router can dispatch events to this device
immediately — no kernel restart needed.

### `DELETE /api/auth/devices/{device_id}` — revoke

A device may revoke itself (a "log out" flow). The master caller can
revoke any device. Subsequent requests with the revoked token return
`401`.

### `GET /api/auth/devices` — list paired devices (master-only useful)

Returns every paired device's metadata (name, platform, capabilities,
last-seen). Tokens are never included. The `/system` UI consumes this.

## Inputs (observations the client uploads)

### `POST /api/inputs` — single event

```json
{
  "surface": "phone.foreground_app",
  "raw_payload": "{\"package\":\"com.slack\",\"timestamp\":\"2026-05-10T13:00:00Z\"}",
  "intent_hint": null,
  "source": "device:hgoel-pixel-7",
  "reason": "foreground app changed",
  "context": {},
  "sensitivity": "personal",
  "expires_at": null
}
```

`surface` is the routing key the kernel's input router uses to pick a
handler. `raw_payload` is opaque JSON — the kernel doesn't validate
its shape; the matched handler does.

Response:
```json
{
  "event_id": "abc123…",
  "dispatched": ["llm"],
  "dropped": [],
  "expired": false
}
```

### `POST /api/inputs/batch` — outbox upload

Use this once the client outbox holds more than one event. Each event
is processed independently — a malformed entry doesn't poison the
batch. The cap is **200 events per request**; oversized batches return
`413`.

```json
{
  "events": [
    { "surface": "phone.battery", "raw_payload": "{\"level\":0.8}" },
    { "surface": "phone.foreground_app", "raw_payload": "{\"package\":\"com.slack\"}" }
  ]
}
```

Response (preserves request order):
```json
{
  "results": [
    { "ok": true, "response": { "event_id": "…", "dispatched": [], "dropped": [], "expired": false } },
    { "ok": true, "response": { "event_id": "…", "dispatched": [], "dropped": [], "expired": false } }
  ]
}
```

Failed entries return `{"ok": false, "error": "ExceptionType: message"}`
in their slot. The client should retry only the failures.

## Outputs (events the kernel pushes to this device)

### `GET /events` — Server-Sent Events stream

Long-lived. Pass `?token=<device_token>` (no header — EventSource
can't set headers). Optional `?since_seq=N` replays events newer than
seq N from the kernel's in-memory ring buffer.

Each event has `event:` and `data:` lines. Event types the device
should handle:

- `output.deliver` — a structured output event targeted at this device.
  Render a notification, capture user action, POST back to
  `/api/outputs/{id}/respond`. Payload:
  ```json
  {
    "output_id": "abc123…",
    "delivery_id": "f3a8…",
    "device_id": "ee60f38e021aad9c",
    "category": "alert",
    "urgency": "urgent",
    "content": { "title": "…", "body": "…" },
    "actions": [
      { "label": "Snooze", "invoke_tool": "snooze", "invoke_args": {} }
    ],
    "source_tool": "scheduler",
    "expires_at": null,
    "_seq": 42
  }
  ```

- `output.cancel` — the kernel recalled an event you previously
  delivered. Dismiss the corresponding notification.
  ```json
  { "output_id": "abc123…", "delivery_id": "f3a8…", "device_id": "…", "channel": "device:…", "_seq": 43 }
  ```

- `sse.sync` — sentinel that separates historical replay from live
  events. The sequence number it carries (`{"seq": N}`) is the cutoff;
  anything before this came from the ring buffer, anything after is
  live. Use this to suppress reload-on-event handlers during catch-up.

- `sse.dropped` — `{ "count": N }` — how many events the kernel
  dropped for this subscriber because the queue filled. Surface to
  the user as a "you missed N events" hint and call `/pending` to
  reconcile.

The audience filter on the SSE bus ensures device subscribers only see
broadcasts plus events targeted to their own `device:<id>` channel —
no leakage between devices. The master/loopback UI sees every targeted
event for transparency.

### `GET /api/outputs/pending?since=<delivered_at>` — catch-up

The client lost its SSE connection (cellular handoff, battery saver,
app suspend). Anything older than the in-memory replay buffer
(~256 events) is gone from `/events`. This endpoint reads the durable
delivery table and returns events targeted to *this* device since the
cursor, oldest-first, with the same payload shape `output.deliver`
would have carried.

```json
{
  "events": [ /* same shape as output.deliver `data` */ ],
  "cursor": "2026-05-10T22:03:20.487295+00:00"
}
```

Pass the returned `cursor` as `?since=` next time. **URL-encode** the
cursor — the `+` in the timezone offset will otherwise be decoded as
a space and the comparison will misbehave.

### `POST /api/outputs/{output_id}/respond` — user response

The user clicked an action button on the notification (or freeform-typed
into a reply field, if the surface supports it). The body's
`action_label` should match one of the labels in the original
`output.deliver` event's `actions[].label`; `raw_input` is for free-form
text when no action matched.

```http
POST /api/outputs/abc123…/respond?action_label=Snooze&channel=device:ee60f38e
```

(`channel` lets the kernel record which surface delivered the response;
it's the device's `device:<id>` name.)

## Sensitivity & capabilities — what your device manifest implies

Device channels carry `sensitivity_tolerance="personal"` — the kernel's
router will *not* dispatch `sensitivity="private"` events to a device
channel. If you need private events on this device, that's a design
decision (do you trust the device with private content, e.g. lockscreen
visibility?) that the kernel won't make for you.

The `actions` capability gates whether action-bearing events reach the
device. If the device manifest says `actions=false`, an event with
actions is filtered out. Default is `actions=true` — most platforms
support buttoned notifications.

`interruption_level` (background / foreground / demanding) is reported
to the router but doesn't currently change behaviour; future routing
rules may use it.

## Things the client must NOT do

- **Never carry the master token.** It's loopback-only by design and
  is rejected from any non-loopback peer; do not attempt to obtain it.
- **Never POST to `/api/auth/pairing-codes`.** Devices can't mint
  codes for other devices — only the loopback master can. This is
  enforced server-side; attempting it returns `403`.
- **Never invent `surface` strings the server doesn't expect.** The
  input router routes on this; introducing a new surface needs a
  matching handler on the kernel side.
