namespace Lifeman.Client.Sensors;

/// Emits a summary as soon as the max-min range of the scalar signal
/// across the live window exceeds a threshold. Good for "device picked
/// up" / motion bursts: stays quiet during stillness, fires promptly on
/// movement. Pair with a WindowedMeanDownsampler for the quiet-state
/// heartbeat — together they bound emission rate to roughly 1–60/min.
///
/// The window is a sliding time window: samples older than `Window` are
/// evicted before each peak check. This means a sustained motion burst
/// produces one event (the first time the threshold is crossed), then
/// the sampler waits for the window to clear before it can fire again.
public sealed class PeakDetectorDownsampler<TSample> : IDownsampler<TSample>
{
    private readonly TimeSpan _window;
    private readonly TimeSpan _cooldown;
    private readonly double _rangeThreshold;
    private readonly Func<TSample, double> _scalarSelector;
    private readonly Queue<(DateTimeOffset At, double Value)> _samples = new();
    private DateTimeOffset _lastEmit = DateTimeOffset.MinValue;

    public PeakDetectorDownsampler(
        TimeSpan window,
        double rangeThreshold,
        Func<TSample, double> selector,
        TimeSpan? cooldown = null)
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        if (rangeThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(rangeThreshold));
        _window = window;
        _cooldown = cooldown ?? window;
        _rangeThreshold = rangeThreshold;
        _scalarSelector = selector;
    }

    public PeakDetectorDownsampler(
        TimeSpan window,
        double rangeThreshold,
        Func<TSample, Vec3Sample> vecSelector,
        TimeSpan? cooldown = null)
        : this(window, rangeThreshold, s => vecSelector(s).Magnitude, cooldown)
    {
    }

    public void Add(TSample sample, DateTimeOffset at)
    {
        _samples.Enqueue((at, _scalarSelector(sample)));
        EvictOlderThan(at - _window);
    }

    public bool TryDrain(out DownsampleResult result)
    {
        if (_samples.Count == 0)
        {
            result = default;
            return false;
        }

        double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
        DateTimeOffset start = DateTimeOffset.MaxValue, end = DateTimeOffset.MinValue;
        foreach (var (at, value) in _samples)
        {
            if (value < min) min = value;
            if (value > max) max = value;
            sum += value;
            if (at < start) start = at;
            if (at > end) end = at;
        }

        var range = max - min;
        if (range < _rangeThreshold || (end - _lastEmit) < _cooldown)
        {
            result = default;
            return false;
        }

        var payload = new
        {
            kind = "peak_event",
            count = _samples.Count,
            range,
            min,
            max,
            mean = sum / _samples.Count,
            threshold = _rangeThreshold,
        };
        result = new DownsampleResult(start, end, _samples.Count, "peak", payload);
        _lastEmit = end;
        _samples.Clear();
        return true;
    }

    private void EvictOlderThan(DateTimeOffset cutoff)
    {
        while (_samples.Count > 0 && _samples.Peek().At < cutoff)
            _samples.Dequeue();
    }
}
