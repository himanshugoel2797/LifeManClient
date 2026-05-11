namespace Lifeman.Client.Sensors;

/// Averages numeric samples (scalar or Vec3) across a fixed time window
/// and emits one summary per window. Good for ambient sensors (light,
/// pressure, temperature) and for "quiet state" heartbeats on motion
/// sensors when the peak detector has nothing interesting to say.
///
/// Window boundaries are time-based, not count-based: TryDrain returns
/// true the first time it's called after `Window` has elapsed since the
/// window start AND at least one sample has been observed. Empty windows
/// are skipped so we don't emit garbage when a sensor stops reporting.
public sealed class WindowedMeanDownsampler<TSample> : IDownsampler<TSample>
{
    private readonly TimeSpan _window;
    private readonly Func<TSample, double> _scalarSelector;
    private readonly Func<TSample, Vec3Sample?>? _vecSelector;
    private readonly string _trigger;

    private DateTimeOffset _windowStart;
    private DateTimeOffset _windowEnd;
    private int _count;
    private double _sumScalar;
    private double _sumX, _sumY, _sumZ;
    private double _minScalar = double.PositiveInfinity;
    private double _maxScalar = double.NegativeInfinity;

    /// Scalar (1-D) windowed mean — for light, pressure, temperature.
    public WindowedMeanDownsampler(TimeSpan window, Func<TSample, double> selector, string trigger = "windowed_mean")
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _window = window;
        _scalarSelector = selector;
        _vecSelector = null;
        _trigger = trigger;
    }

    /// Vec3 windowed mean — for accel/gyro/magnetometer quiet-state heartbeats.
    public WindowedMeanDownsampler(TimeSpan window, Func<TSample, Vec3Sample> vecSelector, string trigger = "windowed_mean")
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _window = window;
        _scalarSelector = s => vecSelector(s).Magnitude;
        _vecSelector = s => vecSelector(s);
        _trigger = trigger;
    }

    public void Add(TSample sample, DateTimeOffset at)
    {
        if (_count == 0) _windowStart = at;
        _windowEnd = at;
        _count++;

        var scalar = _scalarSelector(sample);
        _sumScalar += scalar;
        if (scalar < _minScalar) _minScalar = scalar;
        if (scalar > _maxScalar) _maxScalar = scalar;

        if (_vecSelector is not null)
        {
            var v = _vecSelector(sample)!.Value;
            _sumX += v.X; _sumY += v.Y; _sumZ += v.Z;
        }
    }

    public bool TryDrain(out DownsampleResult result)
    {
        if (_count == 0 || (_windowEnd - _windowStart) < _window)
        {
            result = default;
            return false;
        }

        var meanScalar = _sumScalar / _count;
        object payload;
        if (_vecSelector is not null)
        {
            payload = new
            {
                kind = "vec3_mean",
                count = _count,
                mean_x = _sumX / _count,
                mean_y = _sumY / _count,
                mean_z = _sumZ / _count,
                mean_magnitude = meanScalar,
                min_magnitude = _minScalar,
                max_magnitude = _maxScalar,
            };
        }
        else
        {
            payload = new
            {
                kind = "scalar_mean",
                count = _count,
                mean = meanScalar,
                min = _minScalar,
                max = _maxScalar,
            };
        }

        result = new DownsampleResult(_windowStart, _windowEnd, _count, _trigger, payload);
        Reset();
        return true;
    }

    private void Reset()
    {
        _count = 0;
        _sumScalar = 0;
        _sumX = _sumY = _sumZ = 0;
        _minScalar = double.PositiveInfinity;
        _maxScalar = double.NegativeInfinity;
    }
}
