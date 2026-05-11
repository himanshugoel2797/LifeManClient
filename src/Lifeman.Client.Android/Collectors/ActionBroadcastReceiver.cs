using Android.Content;

namespace Lifeman.Client.Android.Collectors;

/// Single, reusable `BroadcastReceiver` that forwards `OnReceive` to a
/// caller-supplied delegate. Replaces the per-collector inner-class
/// receivers (one each for Battery, Screen, Idle, Locale, Headphones,
/// Alarms, Bluetooth) that all did the same trivial dispatch.
public sealed class ActionBroadcastReceiver : BroadcastReceiver
{
    private readonly Action<Intent> _onIntent;
    public ActionBroadcastReceiver(Action<Intent> onIntent) => _onIntent = onIntent;

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent is null) return;
        _onIntent(intent);
    }
}
