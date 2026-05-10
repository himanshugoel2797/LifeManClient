# LifeManClient

The companion client app for [lifeman](https://github.com/himanshugoel2797/LifeMan).

`lifeman` is a personal-companion kernel that runs on a single trusted host
on the user's LAN (loopback UI by default). This repo holds the client
applications that turn devices — phones, desktops, eventually watches —
into **sensors and surfaces** for that kernel: they observe what's
happening, forward it to `/api/inputs`, and render delivered events from
the kernel's output system back as platform-native notifications.

The client carries **no business logic**. Routing, classification,
intervention, recall, and scheduling all live in the kernel.

See [CLIENT_DESIGN.md](CLIENT_DESIGN.md) for the full design document.

## Stack

- **.NET 9 + .NET MAUI** — single shared C# codebase with Android and
  Windows heads (~70% shared, the rest behind `#if ANDROID` /
  `#if WINDOWS` or partial-class platform splits).
- iOS / macOS / WearOS deferred. Health Connect is the v1 watch story.

Why MAUI: native UI on each platform; full Android API surface via
.NET for Android (UsageStatsManager, AccessibilityService,
NotificationListenerService, MediaProjection, SensorManager); WinUI 3 +
CsWin32 on Windows; sideload-friendly so neither store dictates target
SDK / signing / policy review.

## Repo layout (target)

```
LifeManClient/
├── README.md             this file
├── CLAUDE.md             onboarding for the next Claude Code agent
├── CLIENT_DESIGN.md      the full design (frozen at server-prereq commit)
├── SERVER_API.md         the kernel API surface this client talks to
├── .gitignore            .NET / Visual Studio noise
└── src/                  (to be created)
    ├── Lifeman.Client/                shared core (Outbox, Uploader, SSE, …)
    ├── Lifeman.Client.Android/        MAUI Android head
    └── Lifeman.Client.Windows/        MAUI Windows head
```

Nothing under `src/` exists yet — that's Phase 2.

## Current state

Server-side prerequisites for Phase 2 are done in the lifeman parent
(commit `feat(client-prereqs): per-device output channels, batch
inputs, pending fetch`). The client needs:

1. **Pairing flow** — first-run UI, exchange code for a device token,
   store token via platform keystore (Android Keystore / Windows DPAPI).
2. **Outbox** — local SQLite, observation events queued for upload.
3. **Uploader** — drains outbox, POSTs to `/api/inputs/batch`,
   exponential backoff, adaptive batch size.
4. **SSE receiver** — long-lived `/events?token=…` connection;
   `/api/outputs/pending?since=…` for catch-up after disconnect.
5. **Output renderer** — maps received events to native notifications
   (Android `NotificationCompat.Builder`, Windows `AppNotification`),
   POSTs back to `/api/outputs/{id}/respond` on user action.
6. **One collector end-to-end** — `phone.battery` /
   `desktop.power` (no permissions; easiest first collector).

Then collectors get added incrementally per CLIENT_DESIGN's
[priority order](CLIENT_DESIGN.md#next-steps).

## Building (once Phase 2 lands)

```bash
# requires .NET 9 SDK and the maui-android / maui-windows workloads
dotnet workload install maui-android maui-windows
dotnet build src/Lifeman.Client.sln
```

## Provisioning a device against a kernel

1. On the kernel host, open `http://<host>:8390/system`, click
   *Generate pairing code*. Note the 8-char code (or the
   `lifeman://pair?host=…&code=…` URL).
2. Install the client. On first run:
   - Scan / paste the pair URL or enter the code + host.
   - The client POSTs `/api/auth/pair` and stores the returned token.
3. Walk through OS-level permissions (Android only — see
   [CLIENT_DESIGN.md §"Permission model"](CLIENT_DESIGN.md#permission-model)).

## License

TBD — match the parent repo.
