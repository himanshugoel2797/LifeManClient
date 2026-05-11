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
[Activity(Label = "@string/app_name", MainLauncher = true, Exported = true)]
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
        var scroll = new ScrollView(this);
        var root = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };
        // Slightly generous top padding so the status field clears the
        // action bar even before any window-insets handling.
        root.SetPadding(48, 32, 48, 48);
        scroll.AddView(root);

        _status = new TextView(this)
        {
            TextSize = 14f,
            Text = "…",
        };
        root.AddView(_status);

        var label = new TextView(this) { Text = "Pair URL or code", TextSize = 14f };
        label.SetPadding(0, 48, 0, 8);
        root.AddView(label);

        _input = new EditText(this) { Hint = "lifeman://pair?host=…&code=…" };
        root.AddView(_input);

        _pairBtn = new Button(this) { Text = "Pair" };
        _pairBtn.Click += (_, _) =>
        {
            var text = _input.Text?.Trim();
            if (!string.IsNullOrEmpty(text)) AttemptPair(text);
        };
        root.AddView(_pairBtn);

        _startBtn = new Button(this) { Text = "Start agent" };
        _startBtn.Click += (_, _) => { LifemanService.Start(this); _ = RefreshStatusAsync(); };
        root.AddView(_startBtn);

        _stopBtn = new Button(this) { Text = "Stop agent" };
        _stopBtn.Click += (_, _) => { LifemanService.Stop(this); _ = RefreshStatusAsync(); };
        root.AddView(_stopBtn);

        SetContentView(scroll);
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
        SetStatus($"server: {server}\ndevice_id: {deviceId}\nname: {name}");
    }

    private void SetStatus(string s) => RunOnUiThread(() => { if (_status is not null) _status.Text = s; });
}
