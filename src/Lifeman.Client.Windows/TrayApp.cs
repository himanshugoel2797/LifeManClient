using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Lifeman.Client.Collectors;
using Lifeman.Client.Config;
using Lifeman.Client.Contracts;
using Lifeman.Client.Health;
using Lifeman.Client.Hosting;
using Lifeman.Client.Net;
using Lifeman.Client.Windows.Collectors;
using Lifeman.Client.Windows.Config;
using Lifeman.Client.Windows.Logging;
using Lifeman.Client.Windows.Renderers;
using Microsoft.Extensions.Logging;

namespace Lifeman.Client.Windows;

/// The Windows agent runs as a tray app: no console window, a single
/// `NotifyIcon` in the notification area, the lifeman host loop on a
/// background thread, and a per-user named pipe so a second invocation
/// (URL handler / "Pair…" relaunch) can forward a `lifeman://` link
/// without spawning a second host.
[SupportedOSPlatform("windows")]
public static class TrayApp
{
    public static int Run(string stateDir, string logDir)
    {
        using var single = SingleInstance.TryAcquire();
        if (!single.Acquired)
        {
            MessageBox.Show("Another lifeman-client is already running.",
                "lifeman", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 2;
        }

        var config = new DpapiConfigStore(Path.Combine(stateDir, "config.json"));
        using var fileLog = new RollingFileLoggerProvider(logDir);
        using var loggerFactory = LoggerFactory.Create(b => b
            .AddProvider(fileLog)
            .SetMinimumLevel(LogLevel.Information));
        var log = loggerFactory.CreateLogger("lifeman-tray");

        using var authHandler = new DeviceTokenHandler(config);
        using var http = new HttpClient(authHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };

        // If the user hasn't paired yet, prompt instead of starting the
        // host (it would just refuse anyway, and silently doing nothing
        // is the worst UX).
        var token = config.GetAsync(ConfigKeys.DeviceToken).AsTask().GetAwaiter().GetResult();
        if (token is null)
        {
            log.LogInformation("not paired yet; prompting");
            if (!PromptAndPair(http, config))
            {
                return 1; // user cancelled
            }
        }

        ApplicationConfiguration.Initialize();
        using var ctSource = new CancellationTokenSource();

        var bundle = ClientHostFactory.Build(
            http, config, loggerFactory,
            rendererFactory: responses => new WindowsToastRenderer(responses),
            collectorsFactory: uploader => new ICollector[]
            {
                new PermissionAuditor(new[]
                {
                    new PermissionProbe("desktop.notification", "UserNotificationListener access",
                        DesktopNotificationCollector.GetAccessStatus),
                }),
                new DesktopPowerCollector(),
                new DesktopActiveWindowCollector(),
                new DesktopIdleCollector(),
                new DesktopNetworkCollector(uploader),
                new DesktopSessionCollector(),
                new DesktopProcessListCollector(),
                new DesktopNotificationCollector(),
                new DesktopScreenCaptureCollector(),
                new DesktopBatteryCollector(),
                new DesktopMediaCollector(),
                new DesktopLocaleCollector(),
                new DesktopAudioEndpointCollector(),
            },
            options: new ClientHostOptions
            {
                OutboxPath = Path.Combine(stateDir, "outbox.db"),
                Platform = "windows",
                CurrentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
                UploaderIdlePoll = TimeSpan.FromSeconds(2),
                MeteredByDefault = false,
            });
        RuntimeState.CurrentOutbox = bundle.Outbox;

        var hostTask = Task.Run(async () =>
        {
            try { await bundle.Host.RunAsync(ctSource.Token); }
            catch (OperationCanceledException) when (ctSource.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError(ex, "host loop crashed"); }
        });

        // Forward `lifeman://` clicks that race the tray (Windows may
        // launch a second process before our SingleInstance acquisition
        // serializes) — handled via the pipe.
        var uiContext = SynchronizationContext.Current
            ?? new WindowsFormsSynchronizationContext();
        _ = TrayIpc.StartListenerAsync(line =>
        {
            // Marshal to the UI thread so MessageBox / dialogs sit on
            // the right context.
            var tcs = new TaskCompletionSource();
            uiContext.Post(_ =>
            {
                try { HandleIpc(line, http, config, log); }
                finally { tcs.SetResult(); }
            }, null);
            return tcs.Task;
        }, ctSource.Token);

        using var icon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "lifeman client",
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Status…", null, (_, _) => StatusDialog.Show(bundle, config, logDir));
        menu.Items.Add("Pair new device…", null, (_, _) => PromptAndPair(http, config));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open logs", null, (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo { FileName = logDir, UseShellExecute = true }); }
            catch { /* explorer may be unavailable */ }
        });
        menu.Items.Add("Open server in browser", null, async (_, _) =>
        {
            var url = await config.GetAsync(ConfigKeys.ServerBaseUrl);
            if (!string.IsNullOrEmpty(url))
                try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
                catch { /* swallow */ }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) =>
        {
            ctSource.Cancel();
            Application.Exit();
        });
        icon.ContextMenuStrip = menu;
        // Double-click opens status — discoverable shortcut.
        icon.DoubleClick += (_, _) => StatusDialog.Show(bundle, config, logDir);

        Application.Run();

        // Clean shutdown.
        try { ctSource.Cancel(); } catch { }
        try { hostTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
        try { bundle.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); } catch { }
        icon.Visible = false;
        return 0;
    }

    private static void HandleIpc(string line, HttpClient http, IConfigStore config, ILogger log)
    {
        // Wire format: `pair lifeman://…`.
        var parts = line.Split(' ', 2);
        if (parts.Length < 2) return;
        if (!string.Equals(parts[0], "pair", StringComparison.Ordinal)) return;
        var url = parts[1].Trim();
        if (!url.StartsWith("lifeman://", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var (serverUrl, code) = PairingClient.ParsePairUrl(url);
            PerformPair(http, config, serverUrl, code);
            MessageBox.Show("Paired successfully.", "lifeman",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ipc pair failed");
            MessageBox.Show($"Pairing failed: {ex.Message}", "lifeman",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// Modal prompt: paste a `lifeman://pair?...` URL. Used for first-
    /// run and for the "Pair new device…" menu item.
    private static bool PromptAndPair(HttpClient http, IConfigStore config)
    {
        var prompt = new InputForm
        {
            Title = "Pair with lifeman server",
            Description = "Paste the lifeman://pair?host=…&code=… URL from the server's pairing page,\n"
                          + "or enter the host URL and code on separate lines.",
        };
        if (prompt.ShowDialog() != DialogResult.OK) return false;
        var raw = prompt.Value?.Trim() ?? "";
        if (raw.Length == 0) return false;

        try
        {
            string serverUrl, code;
            if (raw.StartsWith("lifeman://", StringComparison.OrdinalIgnoreCase))
            {
                (serverUrl, code) = PairingClient.ParsePairUrl(raw);
            }
            else
            {
                var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length < 2) throw new FormatException("Expected pair URL or two lines (host, code).");
                serverUrl = lines[0]; code = lines[1];
            }
            PerformPair(http, config, serverUrl, code);
            MessageBox.Show("Paired successfully.", "lifeman",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Pairing failed: {ex.Message}", "lifeman",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static void PerformPair(HttpClient http, IConfigStore config, string serverUrl, string code)
    {
        var pairing = new PairingClient(http, config);
        var capabilities = new DeviceCapabilities(
            RichContent: true, Images: false, Actions: true, Persistence: true,
            InterruptionLevel: "foreground", TypicalLatencyMs: 1000);
        var name = $"win-{Environment.MachineName}";
        pairing.PairAsync(serverUrl, code, name, "windows", capabilities, CancellationToken.None)
            .GetAwaiter().GetResult();
    }
}

/// Minimal multi-line input prompt — keeps WinForms surface small (no
/// .resx, no designer files). Returned via `Value`.
[SupportedOSPlatform("windows")]
internal sealed class InputForm : Form
{
    private readonly Label _label = new();
    private readonly TextBox _box = new();
    private readonly Button _ok = new();
    private readonly Button _cancel = new();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get => Text; set => Text = value; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Description { get => _label.Text; set => _label.Text = value; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Value { get => _box.Text; set => _box.Text = value; }

    public InputForm()
    {
        ClientSize = new Size(540, 220);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;

        _label.AutoSize = false;
        _label.Location = new Point(12, 12);
        _label.Size = new Size(516, 48);
        Controls.Add(_label);

        _box.Multiline = true;
        _box.Location = new Point(12, 68);
        _box.Size = new Size(516, 100);
        _box.AcceptsReturn = true;
        _box.ScrollBars = ScrollBars.Vertical;
        Controls.Add(_box);

        _ok.Text = "OK";
        _ok.DialogResult = DialogResult.OK;
        _ok.Location = new Point(372, 180);
        _ok.Size = new Size(75, 28);
        Controls.Add(_ok);

        _cancel.Text = "Cancel";
        _cancel.DialogResult = DialogResult.Cancel;
        _cancel.Location = new Point(453, 180);
        _cancel.Size = new Size(75, 28);
        Controls.Add(_cancel);

        AcceptButton = _ok;
        CancelButton = _cancel;
    }
}

/// Read-only status window. Builds a fresh snapshot each time it opens.
[SupportedOSPlatform("windows")]
internal static class StatusDialog
{
    public static void Show(ClientHostBundle bundle, IConfigStore config, string logDir)
    {
        var text = BuildSnapshot(bundle, config, logDir);
        using var form = new Form
        {
            Text = "lifeman status",
            ClientSize = new Size(620, 480),
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.SizableToolWindow,
        };
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9.5f),
            WordWrap = false,
            Text = text,
        };
        form.Controls.Add(box);
        form.ShowDialog();
    }

    private static string BuildSnapshot(ClientHostBundle bundle, IConfigStore config, string logDir)
    {
        var sb = new StringBuilder();
        try
        {
            var url = config.GetAsync(ConfigKeys.ServerBaseUrl).AsTask().GetAwaiter().GetResult();
            var devId = config.GetAsync(ConfigKeys.DeviceId).AsTask().GetAwaiter().GetResult();
            var name = config.GetAsync(ConfigKeys.DeviceName).AsTask().GetAwaiter().GetResult();
            var repair = config.GetAsync(ConfigKeys.RepairRequired).AsTask().GetAwaiter().GetResult();

            sb.AppendLine("== pairing ==");
            sb.AppendLine($"server      {url ?? "(unpaired)"}");
            sb.AppendLine($"device_id   {devId ?? "-"}");
            sb.AppendLine($"name        {name ?? "-"}");
            if (!string.IsNullOrEmpty(repair))
                sb.AppendLine("*** re-pair required (server returned 401) ***");

            sb.AppendLine();
            sb.AppendLine("== runtime ==");
            var outboxDepth = bundle.Outbox.CountAsync().AsTask().GetAwaiter().GetResult();
            sb.AppendLine($"outbox      {outboxDepth} pending event(s)");
            sb.AppendLine($"uploaded    {bundle.Metrics.EventsUploaded} (last: {FormatTime(bundle.Metrics.LastSuccessfulUploadAt)})");
            sb.AppendLine($"rendered    {bundle.Metrics.EventsRendered}");
            sb.AppendLine($"sse connect {FormatTime(bundle.Metrics.LastSseConnectAt)}");
            sb.AppendLine($"sse event   {FormatTime(bundle.Metrics.LastSseEventAt)}");

            sb.AppendLine();
            sb.AppendLine("== collectors ==");
            var snapshot = bundle.Health.SnapshotAsync().AsTask().GetAwaiter().GetResult();
            if (snapshot.Count == 0) sb.AppendLine("(no observations yet)");
            foreach (var h in snapshot)
            {
                sb.AppendLine($"{h.Surface,-32} ok={h.SuccessCount,-6} err={h.ErrorCount,-4} "
                    + $"last_ok={FormatTime(h.LastSuccessAt)} last_err={FormatTime(h.LastErrorAt)}");
                if (!string.IsNullOrEmpty(h.LastError))
                    sb.AppendLine($"  └─ {h.LastError}");
            }

            sb.AppendLine();
            sb.AppendLine($"logs: {logDir}");
            sb.AppendLine($"autostart: {Autostart.CurrentCommand() ?? "(disabled)"}");
            sb.AppendLine($"url handler: {UrlProtocol.CurrentCommand() ?? "(not registered)"}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"status snapshot failed: {ex.Message}");
        }
        return sb.ToString();
    }

    private static string FormatTime(DateTimeOffset? when)
    {
        if (when is null) return "(never)";
        var delta = DateTimeOffset.UtcNow - when.Value;
        if (delta.TotalSeconds < 60) return $"{(int)delta.TotalSeconds}s ago";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }
}
