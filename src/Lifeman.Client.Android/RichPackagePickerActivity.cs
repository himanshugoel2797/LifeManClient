using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using Lifeman.Client.Android.Config;
using Lifeman.Client.Config;

namespace Lifeman.Client.Android;

/// Multi-select picker for ConfigKeys.NotificationRichPackages.
/// Lists every launchable user-installed app, plus any packages the
/// user has already added (so a backend-pushed list isn't silently
/// trimmed away on edit). Toggling a checkbox persists immediately;
/// PhoneNotificationCollector reads the value once at start, so a
/// fresh selection takes effect after the next service restart.
[Activity(Label = "Notification rich-payload allowlist", Exported = false,
    Theme = "@android:style/Theme.DeviceDefault.Light.NoActionBar")]
public sealed class RichPackagePickerActivity : Activity
{
    private KeystoreConfigStore? _config;
    private readonly HashSet<string> _selected = new();
    private LinearLayout? _listLayout;
    private EditText? _search;
    private List<(string Package, string Label)> _apps = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _config = new KeystoreConfigStore(ApplicationContext!);
        BuildUi();
        _ = LoadAsync();
    }

    private void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        root.SetBackgroundColor(global::Android.Graphics.Color.White);
        root.SetFitsSystemWindows(true);

        var header = new TextView(this) { Text = "Rich notification packages", TextSize = 22f };
        header.SetTextColor(global::Android.Graphics.Color.White);
        header.SetBackgroundColor(global::Android.Graphics.Color.Argb(0xff, 0x15, 0x18, 0x1c));
        header.SetPadding(48, 64, 48, 32);
        root.AddView(header);

        var hint = new TextView(this)
        {
            Text = "Checked packages upload title / text / subText / ticker on every notification. Unchecked packages send metadata only (package, channel, flags).",
            TextSize = 12f,
        };
        hint.SetTextColor(global::Android.Graphics.Color.Argb(0xff, 0x55, 0x55, 0x55));
        hint.SetPadding(48, 32, 48, 16);
        root.AddView(hint);

        _search = new EditText(this) { Hint = "Filter by name or package" };
        _search.SetTextColor(global::Android.Graphics.Color.Black);
        _search.SetHintTextColor(global::Android.Graphics.Color.Argb(0xff, 0x99, 0x99, 0x99));
        _search.AfterTextChanged += (_, _) => RenderList();
        var searchWrap = new FrameLayout(this);
        searchWrap.SetPadding(48, 8, 48, 8);
        searchWrap.AddView(_search);
        root.AddView(searchWrap);

        // Bulk actions operate on the currently-filtered view, not the
        // whole package list — that way "Select all" with a filter
        // becomes the obvious "select every WhatsApp-related package"
        // gesture without forcing the user to tap each row.
        var bulkBar = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        bulkBar.SetPadding(48, 0, 48, 8);
        var selectAll = new Button(this) { Text = "Select all" };
        selectAll.Click += (_, _) => BulkSet(true);
        var deselectAll = new Button(this) { Text = "Deselect all" };
        deselectAll.Click += (_, _) => BulkSet(false);
        var halfWeight = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        selectAll.LayoutParameters = halfWeight;
        deselectAll.LayoutParameters = halfWeight;
        bulkBar.AddView(selectAll);
        bulkBar.AddView(deselectAll);
        root.AddView(bulkBar);

        var scroll = new ScrollView(this);
        _listLayout = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        _listLayout.SetPadding(48, 0, 48, 48);
        scroll.AddView(_listLayout);
        scroll.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent,
            1f);
        root.AddView(scroll);

        SetContentView(root);
    }

    private async Task LoadAsync()
    {
        var raw = await _config!.GetAsync(ConfigKeys.NotificationRichPackages) ?? string.Empty;
        foreach (var p in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            _selected.Add(p);

        var pm = PackageManager!;
        var byPackage = new Dictionary<string, (string Label, bool System)>(StringComparer.Ordinal);

        // Every installed application, not just launchable ones — many
        // of the notifying apps (Messages, Clock, dialer, framework
        // services) have no launcher icon. QUERY_ALL_PACKAGES in the
        // manifest is what makes this return everything on Android 11+.
        var apps = pm.GetInstalledApplications(PackageInfoFlags.MatchAll);
        foreach (var ai in apps)
        {
            if (ai.PackageName is null) continue;
            var label = ai.LoadLabel(pm)?.ToString();
            if (string.IsNullOrWhiteSpace(label) || label == ai.PackageName) label = ai.PackageName;
            var isSystem = (ai.Flags & ApplicationInfoFlags.System) != 0
                        && (ai.Flags & ApplicationInfoFlags.UpdatedSystemApp) == 0;
            byPackage[ai.PackageName] = (label, isSystem);
        }

        // Ensure already-selected packages are visible even if they
        // somehow didn't make the list (rare; e.g. uninstalled while
        // selected, or kernel-pushed package-prefix entries).
        foreach (var s in _selected)
            if (!byPackage.ContainsKey(s) && !s.EndsWith('.'))
                byPackage[s] = (s, false);

        // User apps first (so the common case — your messaging /
        // calendar / music apps — sits at the top), then system apps
        // below. Within each group, sort alphabetically by label.
        _apps = byPackage
            .OrderBy(kv => kv.Value.System)
            .ThenBy(kv => kv.Value.Label, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, kv.Value.Label))
            .ToList();

        RunOnUiThread(RenderList);
    }

    private void RenderList()
    {
        if (_listLayout is null) return;
        _listLayout.RemoveAllViews();
        foreach (var (pkg, label) in VisibleRows())
        {
            var row = new CheckBox(this) { Checked = _selected.Contains(pkg) };
            row.SetTextColor(global::Android.Graphics.Color.Black);
            row.TextFormatted = HtmlCompat($"<b>{Escape(label)}</b><br/><small>{Escape(pkg)}</small>");
            row.CheckedChange += (_, e) =>
            {
                if (e.IsChecked) _selected.Add(pkg);
                else _selected.Remove(pkg);
                _ = PersistAsync();
            };
            _listLayout.AddView(row);
        }
    }

    private void BulkSet(bool check)
    {
        foreach (var (pkg, _) in VisibleRows())
        {
            if (check) _selected.Add(pkg);
            else _selected.Remove(pkg);
        }
        _ = PersistAsync();
        RenderList();
    }

    private IEnumerable<(string Package, string Label)> VisibleRows()
    {
        var filter = _search?.Text?.Trim() ?? string.Empty;
        return _apps.Where(a =>
            string.IsNullOrEmpty(filter)
            || a.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || a.Package.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private async Task PersistAsync()
    {
        if (_config is null) return;
        var joined = string.Join(",", _selected.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        await _config.SetAsync(ConfigKeys.NotificationRichPackages, joined);
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static ISpanned HtmlCompat(string html) =>
        Build.VERSION.SdkInt >= BuildVersionCodes.N
            ? Html.FromHtml(html, FromHtmlOptions.ModeLegacy)!
            : Html.FromHtml(html)!;
}
