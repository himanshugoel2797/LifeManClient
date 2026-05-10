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

        // Deep-link handoff: lifeman://pair?host=…&code=…
        if (Intent?.Action == Intent.ActionView && Intent.Data is global::Android.Net.Uri u)
            AttemptPair(u.ToString()!);

        _ = RefreshStatusAsync();
    }

    private void BuildUi()
    {
        var root = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };
        root.SetPadding(48, 48, 48, 48);

        _status = new TextView(this) { TextSize = 16f, Text = "…" };
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

        SetContentView(root);
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

            SetStatus("pairing…");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var pairing = new PairingClient(http, _config!);
            var caps = new DeviceCapabilities(
                RichContent: true, Images: true, Actions: true, Persistence: true,
                InterruptionLevel: "ambient", TypicalLatencyMs: 2000);
            var resp = await pairing.PairAsync(url, code,
                $"android-{Build.Model}", "android", caps, CancellationToken.None);
            SetStatus($"paired: {resp.DeviceId}\nstarting agent…");
            LifemanService.Start(this);
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
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
