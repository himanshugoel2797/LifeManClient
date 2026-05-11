using System.Runtime.CompilerServices;
using System.Text.Json;
using Android.Content;
using Lifeman.Client.Collectors;
using Lifeman.Client.Config;

namespace Lifeman.Client.Android.Collectors;

// Design choice: ONE collector, MANY surfaces. The collector iterates the
// record-type list once per poll, holds a single shared cursor namespace
// (phone.health.cursor.<type>) in IConfigStore, and emits each record as
// a CollectedEvent whose Surface is `phone.health.<metric>`. The surface
// per-record matches CLIENT_DESIGN §"WearOS strategy" wording and keeps
// the kernel router free to dispatch per-metric handlers, while the
// collector itself only has to negotiate Health Connect availability /
// permissions / suspend-fn bridging once.
public sealed class PhoneHealthConnectCollector : ICollector
{
    // Logical surface used for ICollector.Surface (registry key).
    // Per-event surfaces are `phone.health.<metric>` (set on emit).
    public string Surface => "phone.health";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    // Health Connect AndroidX permission names (string constants —
    // these are Manifest.permission.health.READ_* on the Java side).
    // Listed here so AndroidManifest.xml entries can be cross-checked.
    public const string PermReadHeartRate     = "android.permission.health.READ_HEART_RATE";
    public const string PermReadSteps         = "android.permission.health.READ_STEPS";
    public const string PermReadSleep         = "android.permission.health.READ_SLEEP";
    public const string PermReadExercise      = "android.permission.health.READ_EXERCISE";
    public const string PermReadCalories      = "android.permission.health.READ_TOTAL_CALORIES_BURNED";
    public const string PermReadOxygen        = "android.permission.health.READ_OXYGEN_SATURATION";

    public static readonly IReadOnlyList<string> RequiredPermissions = new[]
    {
        PermReadHeartRate, PermReadSteps, PermReadSleep,
        PermReadExercise, PermReadCalories, PermReadOxygen,
    };

    // The (record-type, surface, cursor-key) tuples this collector handles.
    private static readonly (string Metric, string Surface, string CursorKey, string Permission)[] s_metrics = new[]
    {
        ("heart_rate",       "phone.health.heart_rate",       "phone.health.cursor.heart_rate",       PermReadHeartRate),
        ("steps",            "phone.health.steps",            "phone.health.cursor.steps",            PermReadSteps),
        ("sleep_session",    "phone.health.sleep_session",    "phone.health.cursor.sleep_session",    PermReadSleep),
        ("exercise_session", "phone.health.exercise_session", "phone.health.cursor.exercise_session", PermReadExercise),
        ("total_calories",   "phone.health.total_calories",   "phone.health.cursor.total_calories",   PermReadCalories),
        ("oxygen_saturation","phone.health.oxygen_saturation","phone.health.cursor.oxygen_saturation",PermReadOxygen),
    };

    private readonly Context _ctx;
    private readonly IConfigStore _config;

    public PhoneHealthConnectCollector(Context ctx, IConfigStore config)
    {
        _ctx = ctx;
        _config = config;
    }

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 1) Health Connect SDK availability gate.
        // GetSdkStatus is one of the very few non-suspend statics on the
        // HealthConnectClient companion; it returns 1=SDK_AVAILABLE,
        // 2=PROVIDER_UPDATE_REQUIRED, 3=PROVIDER_DISABLED. We use the
        // managed Java reflection bridge so the collector compiles even
        // before the suspend-fn shim lands (see TODO at bottom).
        var sdkStatus = TryGetSdkStatus(_ctx);
        if (sdkStatus is null)
        {
            global::Android.Util.Log.Warn("lifeman",
                "phone.health: HealthConnectClient unavailable on this device");
            yield return ClientObservations.CollectorDisabled(Surface,
                "Health Connect SDK not present");
            yield break;
        }
        if (sdkStatus.Value != 1)
        {
            global::Android.Util.Log.Warn("lifeman",
                $"phone.health: SDK status={sdkStatus.Value} (need 1=AVAILABLE)");
            yield return ClientObservations.CollectorDisabled(Surface,
                $"Health Connect SDK status={sdkStatus.Value}");
            yield break;
        }

        // 2) Permission self-check. Permissions are package-level: the
        // user grants them via the Health Connect app, NOT via the
        // standard runtime-permission dialog. ContextCompat.CheckSelfPermission
        // returns the right answer on Android 14+ where Health Connect
        // permissions live alongside other runtime perms; on pre-14 the
        // call goes through the Health Connect APK shim.
        // TODO(MainActivity): wire up a "Open Health Connect permissions"
        // deep-link button — Intent("androidx.health.ACTION_SHOW_PERMISSIONS_RATIONALE")
        // or HealthConnectClient.ActionHealthConnectSettings — so the user
        // has a one-tap path. The collector itself only inspects state.
        var grantedAny = false;
        foreach (var (_, _, _, perm) in s_metrics)
        {
            if (AndroidX.Core.Content.ContextCompat.CheckSelfPermission(_ctx, perm)
                == global::Android.Content.PM.Permission.Granted)
            {
                grantedAny = true;
                break;
            }
        }
        if (!grantedAny)
        {
            global::Android.Util.Log.Warn("lifeman",
                "phone.health: no Health Connect read permissions granted, collector idle");
            yield return ClientObservations.CollectorDisabled(Surface,
                "no Health Connect read permissions granted");
            yield break;
        }

        // 3) Poll loop. Every PollInterval we'd ask Health Connect for
        // records of each enabled metric since the per-metric cursor.
        // Health Connect's read APIs (readRecords / getChanges) are
        // Kotlin suspend functions; the AndroidX C# binding exposes
        // them as Java methods that take a kotlin.coroutines.Continuation.
        // Calling those from C# requires either (a) a tiny Kotlin shim
        // .aar that wraps each suspend call in a Java callback, or
        // (b) a hand-rolled Continuation implementation in C# — both
        // outside the scope of this scaffold.
        //
        // What this loop does today:
        //   - emits one heartbeat-style "phone.health.poll" event per
        //     cycle so the kernel sees the collector is alive,
        //   - persists a poll-tick cursor under phone.health.cursor.last_poll
        //     so we can verify the cadence is right,
        //   - leaves a clearly marked TODO where the per-metric reads
        //     belong.
        while (!ct.IsCancellationRequested)
        {
            var nowIso = DateTimeOffset.UtcNow.ToString("O");
            await _config.SetAsync("phone.health.cursor.last_poll", nowIso, ct).ConfigureAwait(false);

            foreach (var (metric, surface, cursorKey, perm) in s_metrics)
            {
                if (AndroidX.Core.Content.ContextCompat.CheckSelfPermission(_ctx, perm)
                    != global::Android.Content.PM.Permission.Granted)
                    continue;

                var since = await _config.GetAsync(cursorKey, ct).ConfigureAwait(false);

                // TODO(health-connect-suspend-bridge): replace this stub
                // with a real readRecords(<RecordType>, TimeRangeFilter.after(since))
                // call. Requires either:
                //   - bundling a Kotlin .aar (HealthBridge.kt) that
                //     wraps each suspend fn in a Java callback interface
                //     the C# binding can implement, OR
                //   - implementing kotlin.coroutines.Continuation in C#
                //     and dispatching to a TaskCompletionSource.
                // The shape this should yield once wired:
                //   {type, value, unit, recorded_at, source_device}
                //
                // For now we emit a single placeholder per metric so the
                // pipeline is exercised end-to-end.
                var payload = JsonSerializer.Serialize(new
                {
                    type = metric,
                    value = (object?)null,
                    unit = (string?)null,
                    recorded_at = (string?)null,
                    source_device = (string?)null,
                    note = "scaffold: suspend-fn bridge not yet implemented",
                    since,
                    polled_at = nowIso,
                });
                yield return new CollectedEvent(surface, payload, DateTimeOffset.UtcNow);

                // Cursor advances to the poll timestamp so the next
                // iteration's `since` window starts where this one ended.
                // When the real read lands, advance to the max
                // record.endTime instead.
                await _config.SetAsync(cursorKey, nowIso, ct).ConfigureAwait(false);
            }

            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    /// Reflective shim around HealthConnectClient.Companion.GetSdkStatus(ctx).
    /// Returns null when the binding is missing the type entirely (older
    /// platforms) or the call throws. Kept reflective on purpose: the
    /// strongly-typed C# binding for the Companion's static helper isn't
    /// uniform across the binding's versions, and the surrounding API
    /// surface is large enough that one misnamed reference would block
    /// compile of the whole collector. Once the suspend-fn bridge is
    /// added we'd replace this with the typed call.
    private static int? TryGetSdkStatus(Context ctx)
    {
        try
        {
            var clientType = global::Java.Lang.Class.ForName(
                "androidx.health.connect.client.HealthConnectClient");
            var companionField = clientType.GetField("Companion");
            var companion = companionField.Get(null);
            if (companion is null) return null;
            var method = companion.Class.GetMethod("getSdkStatus",
                global::Java.Lang.Class.ForName("android.content.Context"));
            var result = method.Invoke(companion, ctx);
            return result is global::Java.Lang.Integer i ? i.IntValue() : null;
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn("lifeman",
                $"phone.health: GetSdkStatus reflection failed: {ex.Message}");
            return null;
        }
    }
}
