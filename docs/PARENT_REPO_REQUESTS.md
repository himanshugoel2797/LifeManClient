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
