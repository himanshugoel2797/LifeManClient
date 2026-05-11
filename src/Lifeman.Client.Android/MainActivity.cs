using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Lifeman.Client.Android.Config;
using Lifeman.Client.Android.Services;
using Lifeman.Client.Config;
using Lifeman.Client.Contracts;
using Lifeman.Client.Net;

namespace Lifeman.Client.Android;

/// Minimal pair + status screen. Either:
///   - Open via a `lifeman://pair?host=…&code=…` deep link → pair-and-go.
///   - Launch from the launcher → text field for the pair URL or
///     manual host/code entry, plus a Start/Stop service toggle.
[Activity(Label = "@string/app_name", MainLauncher = true, Exported = true,
    Theme = "@android:style/Theme.DeviceDefault.Light.NoActionBar")]
[IntentFilter(new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "lifeman",
    DataHost = "pair")]
public sealed class MainActivity : Activity
{
    private TextView? _status;
    private EditText? _input;
    private Button? _pairBtn;
    private Button? _startBtn;
    private Button? _stopBtn;
    private KeystoreConfigStore? _config;

    private const int NotificationPermRequest = 0x10;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _config = new KeystoreConfigStore(ApplicationContext!);
        BuildUi();
        MaybeRequestNotificationPermission();

        global::Android.Util.Log.Info("lifeman",
            $"OnCreate: action={Intent?.Action} data={Intent?.Data?.ToString() ?? "(none)"}");

        // Deep-link handoff: lifeman://pair?host=…&code=…
        if (Intent?.Action == Intent.ActionView && Intent.Data is global::Android.Net.Uri u)
        {
            global::Android.Util.Log.Info("lifeman", $"deep-link pair url: {u}");
            AttemptPair(u.ToString()!);
        }
        else
        {
            _ = RefreshStatusAsync();
        }
    }

    private void BuildUi()
    {
        var outer = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };
        outer.SetBackgroundColor(global::Android.Graphics.Color.White);
        outer.SetFitsSystemWindows(true);

        // Custom header bar in lieu of the system action bar — gives us
        // predictable layout math instead of fighting the theme's
        // overlay / inset behavior.
        var header = new TextView(this) { Text = "Lifeman", TextSize = 22f };
        header.SetTextColor(global::Android.Graphics.Color.White);
        header.SetBackgroundColor(global::Android.Graphics.Color.Argb(0xff, 0x15, 0x18, 0x1c));
        header.SetPadding(48, 64, 48, 32);
        outer.AddView(header);

        var scroll = new ScrollView(this);
        var root = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };
        root.SetPadding(48, 48, 48, 48);
        root.SetBackgroundColor(global::Android.Graphics.Color.White);
        scroll.AddView(root);
        outer.AddView(scroll);

        var statusHeader = new TextView(this) { Text = "Status", TextSize = 12f };
        statusHeader.SetTextColor(global::Android.Graphics.Color.Argb(0xff, 0x66, 0x66, 0x66));
        statusHeader.SetPadding(0, 0, 0, 4);
        root.AddView(statusHeader);

        _status = new TextView(this)
        {
            TextSize = 14f,
            Text = "…",
        };
        _status.SetTextColor(global::Android.Graphics.Color.Black);
        _status.SetTypeface(global::Android.Graphics.Typeface.Monospace, global::Android.Graphics.TypefaceStyle.Normal);
        _status.SetPadding(16, 12, 16, 12);
        _status.SetBackgroundColor(global::Android.Graphics.Color.Argb(0xff, 0xf2, 0xf2, 0xf2));
        root.AddView(_status);

        var label = new TextView(this) { Text = "Pair URL or code", TextSize = 14f };
        label.SetTextColor(global::Android.Graphics.Color.Black);
        label.SetPadding(0, 48, 0, 8);
        root.AddView(label);

        _input = new EditText(this) { Hint = "lifeman://pair?host=…&code=…" };
        _input.SetTextColor(global::Android.Graphics.Color.Black);
        _input.SetHintTextColor(global::Android.Graphics.Color.Argb(0xff, 0x99, 0x99, 0x99));
        root.AddView(_input);

        _pairBtn = new Button(this) { Text = "Pair" };
        _pairBtn.Click += (_, _) =>
        {
            var text = _input.Text?.Trim();
            if (!string.IsNullOrEmpty(text)) AttemptPair(text);
        };
        root.AddView(_pairBtn);

        _startBtn = new Button(this) { Text = "Start agent" };
        _startBtn.Click += async (_, _) => { LifemanService.Start(this); await Task.Delay(300); await RefreshStatusAsync(); };
        root.AddView(_startBtn);

        _stopBtn = new Button(this) { Text = "Stop agent" };
        _stopBtn.Click += async (_, _) => { LifemanService.Stop(this); await Task.Delay(300); await RefreshStatusAsync(); };
        root.AddView(_stopBtn);

        SetContentView(outer);
    }

    private void MaybeRequestNotificationPermission()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu) return;
        if (ContextCompat.CheckSelfPermission(this, global::Android.Manifest.Permission.PostNotifications)
            == Permission.Granted) return;
        ActivityCompat.RequestPermissions(this,
            new[] { global::Android.Manifest.Permission.PostNotifications }, NotificationPermRequest);
    }

    private async void AttemptPair(string text)
    {
        try
        {
            global::Android.Util.Log.Info("lifeman", $"AttemptPair: text={text}");
            string url, code;
            if (text.StartsWith("lifeman://", StringComparison.OrdinalIgnoreCase))
            {
                (url, code) = PairingClient.ParsePairUrl(text);
            }
            else
            {
                SetStatus("expected a lifeman:// URL");
                return;
            }
            global::Android.Util.Log.Info("lifeman", $"AttemptPair: url={url} code={code}");

            SetStatus($"pairing → {url}");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var pairing = new PairingClient(http, _config!);
            var caps = new DeviceCapabilities(
                RichContent: true, Images: true, Actions: true, Persistence: true,
                InterruptionLevel: "ambient", TypicalLatencyMs: 2000);
            var resp = await pairing.PairAsync(url, code,
                $"android-{Build.Model}", "android", caps, CancellationToken.None);
            global::Android.Util.Log.Info("lifeman", $"AttemptPair: paired device_id={resp.DeviceId}");
            SetStatus($"paired: {resp.DeviceId}\nstarting agent…");
            LifemanService.Start(this);
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("lifeman", $"AttemptPair failed: {ex}");
            SetStatus($"pair failed: {ex.Message}");
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_config is null) return;
        var server = await _config.GetAsync(ConfigKeys.ServerBaseUrl) ?? "(unpaired)";
        var deviceId = await _config.GetAsync(ConfigKeys.DeviceId) ?? "-";
        var name = await _config.GetAsync(ConfigKeys.DeviceName) ?? "-";
        var running = Services.LifemanService.IsRunning(this) ? "running" : "stopped";
        SetStatus($"server:    {server}\ndevice_id: {deviceId}\nname:      {name}\nagent:     {running}");
    }

    private void SetStatus(string s) => RunOnUiThread(() => { if (_status is not null) _status.Text = s; });
}
