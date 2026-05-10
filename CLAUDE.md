# Claude onboarding for LifeManClient

You are likely picking this up from a fresh Claude Code session on a
Windows machine, charged with bootstrapping the .NET MAUI client. Read
this file first, then [README.md](README.md), then
[CLIENT_DESIGN.md](CLIENT_DESIGN.md) and [SERVER_API.md](SERVER_API.md).

## What this repo is

The companion client for the lifeman kernel. Phones, desktops, eventually
watches. Sensors and surfaces only — no business logic. See
[README.md](README.md) for the elevator pitch.

The kernel lives in a separate repo (the parent of this submodule). Its
authoritative API surface is captured in [SERVER_API.md](SERVER_API.md);
trust that doc over guesses about what endpoints exist.

## Where you are starting

Server-side prerequisites are **done** (see commit
`feat(client-prereqs): per-device output channels, batch inputs,
pending fetch` in the kernel repo). Specifically:

- `POST /api/auth/pair` — mint a device token from a pairing code.
- `POST /api/inputs/batch` — bulk outbox upload, 200 events/req cap.
- `GET /events?token=…` — SSE stream, audience-filtered to this device.
- `GET /api/outputs/pending?since=…` — catch-up after disconnect.
- `POST /api/outputs/{id}/respond` — surface user responses.
- `device:<id>` output channel registered automatically on pair.

Nothing in `src/` exists yet. Phase 2 starts with bootstrapping the
.NET MAUI solution.

## Recommended first session

1. Confirm the toolchain:
   ```powershell
   dotnet --version              # need .NET 9
   dotnet workload list          # need maui-android, maui-windows
   dotnet workload install maui-android maui-windows  # if missing
   ```
2. Scaffold the solution per [README.md#repo-layout-target](README.md#repo-layout-target):
   ```powershell
   mkdir src; cd src
   dotnet new maui -n Lifeman.Client.Android  # rename / split heads as needed
   ```
   (You may prefer `dotnet new maui` once at `Lifeman.Client.sln` level
   and then split shared code into a class library — pick whichever
   structure keeps the shared core honest.)
3. Build the **shared core** first, before either platform head:
   - `IConfig` + a config store (encrypted at rest via the platform
     keystore — Android Keystore on Android, DPAPI on Windows).
   - `Outbox` (SQLite, schema in [CLIENT_DESIGN.md §"Local storage & offline"](CLIENT_DESIGN.md#local-storage--offline)).
   - `IUploader` with exponential backoff and adaptive batch size
     (1 on Wi-Fi, up to 50 on metered).
   - `ISseReceiver` with reconnect-and-`/pending`-catchup on disconnect.
   - `ICollector` and `IRenderer` interfaces (platform-implemented).
   - Pairing flow: code/URL → POST `/api/auth/pair` → store token.
4. Stand up the **first end-to-end test**: pair against a running
   kernel, ingest one `phone.battery` / `desktop.power` event, wait
   for any output via SSE, render a console log. Native notifications
   come **after** that loop is proven — too many things can go wrong
   in the surface layer to debug them at the same time.

## Testing against the real kernel

The kernel binds loopback by default. To accept your device:

```bash
LIFEMAN_ALLOW_NETWORK=true LIFEMAN_HOST=0.0.0.0 lifeman
```

Then on the kernel host, open `http://<host>:8390/system`, click
*Generate pairing code*, copy the code (or scan the
`lifeman://pair?host=…&code=…` URL). Your client uses that to pair.

For Android Emulator: the host's loopback is `10.0.2.2` from inside
the emulator, not `127.0.0.1`. The pairing URL the kernel renders uses
`window.location.host`, which won't be reachable from the emulator —
override the host on the emulator to the Wi-Fi IP of the host machine.

## What lives in the parent repo

Don't change anything outside this submodule directly. If you discover
a missing kernel endpoint, a server bug, or need a new surface
registered, that's a parent-repo change — note it in
`docs/PARENT_REPO_REQUESTS.md` (create the file) and let the user
shepherd it through the kernel repo so the changes land with proper
review and tests.

## Conventions to inherit

- `LifeMan*` repo names mirror the user's existing naming
  (`LifeMan` parent, `LifeManClient` here). Use `Lifeman.Client.*` for
  C# namespaces / project names — Microsoft convention is
  PascalCase.dot.Separated, not LifeManClient.
- Tests live under `tests/`, mirroring kernel-side conventions.
- Don't write speculative classes for "future" device types. Phase 2
  is one shared core + Android head + Windows head + battery
  collector. Add only what a current task requires.
- Don't add backwards-compat shims when changing internal APIs. This
  is greenfield code — refactor freely, no deprecation paths.

## Known gotchas

- **WSL2 + .NET MAUI is unhappy.** Develop and build on real Windows
  (or macOS for the Android head only). The kernel was developed in
  WSL but the client toolchain expects native Windows.
- **Android sideload signing key.** Generate once, back up alongside
  the kernel master key. Losing it forfeits update continuity for
  installed clients (CLIENT_DESIGN §"Risks" → signing-key compromise).
- **`+` in cursor URLs.** When passing the `since=` cursor to
  `/api/outputs/pending`, URL-encode it. Otherwise the server's query
  parser turns the timezone offset's `+` into a space and pagination
  silently misses an event.
- **Foreground service notification (Android 13+) is mandatory.**
  Don't try to hide it. Surface it as the "lifeman is observing"
  status indicator — that's the design intent, not a bug to work
  around.
