using System.Text.Json;
using System.Threading.Channels;
using Android.Content;
using Android.Hardware;
using Android.Runtime;
using Lifeman.Client.Collectors;
using Lifeman.Client.Sensors;

namespace Lifeman.Client.Android.Collectors;

/// `phone.sensor.<name>` — one logical surface per Android sensor type.
/// Single class parameterised by sensor kind (rather than 7 near-duplicate
/// subclasses) because everything that varies — sensor-type id, surface
/// suffix, downsampler choice — is data, not behaviour. The service wires
/// up one instance per sensor it cares about.
public sealed class PhoneSensorCollector : ICollector
{
    public enum Kind
    {
        Accelerometer,
        Gyroscope,
        Magnetometer,
        Light,
        Pressure,
        AmbientTemperature,
        Proximity,
    }

    private readonly Context _ctx;
    private readonly Kind _kind;
    private readonly string _surface;
    private Channel<CollectedEvent>? _channel;
    private SensorManager? _manager;
    private Sensor? _sensor;
    private IDownsampler<Vec3Sample>? _peakDownsampler;
    private IDownsampler<Vec3Sample>? _meanVecDownsampler;
    private IDownsampler<float>? _meanScalarDownsampler;
    private DateTimeOffset _lastEmit = DateTimeOffset.MinValue;
    private TimeSpan _heartbeat = TimeSpan.FromSeconds(30);

    public string Surface => _surface;

    public PhoneSensorCollector(Context ctx, Kind kind)
    {
        _ctx = ctx;
        _kind = kind;
        _surface = "phone.sensor." + kind switch
        {
            Kind.Accelerometer => "accelerometer",
            Kind.Gyroscope => "gyroscope",
            Kind.Magnetometer => "magnetometer",
            Kind.Light => "light",
            Kind.Pressure => "pressure",
            Kind.AmbientTemperature => "ambient_temperature",
            Kind.Proximity => "proximity",
            _ => kind.ToString().ToLowerInvariant(),
        };
    }

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _manager = (SensorManager?)_ctx.GetSystemService(Context.SensorService);
        if (_manager is null) yield break;

        var sensorType = _kind switch
        {
            Kind.Accelerometer => SensorType.Accelerometer,
            Kind.Gyroscope => SensorType.Gyroscope,
            Kind.Magnetometer => SensorType.MagneticField,
            Kind.Light => SensorType.Light,
            Kind.Pressure => SensorType.Pressure,
            Kind.AmbientTemperature => SensorType.AmbientTemperature,
            Kind.Proximity => SensorType.Proximity,
            _ => SensorType.All,
        };

        _sensor = _manager.GetDefaultSensor(sensorType);
        if (_sensor is null)
        {
            // Self-disable cleanly: no magnetometer / temp on this device.
            global::Android.Util.Log.Info("lifeman", $"sensor {_surface} not present; collector disabled");
            yield break;
        }

        ConfigureDownsamplers();

        _channel = Channel.CreateUnbounded<CollectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        // SensorDelay.Game ≈ 20ms / 50Hz — plenty for downsampled motion;
        // ambient sensors will be polled by the OS at their natural rate.
        var rate = IsMotion(_kind) ? SensorDelay.Game : SensorDelay.Normal;
        var listener = new SensorListener(this);
        _manager.RegisterListener(listener, _sensor, rate);
        _lastEmit = DateTimeOffset.UtcNow;

        // Heartbeat ticker drains windowed-mean downsamplers even when
        // the peak detector is silent — quiet-state context still matters.
        using var heartbeat = new Timer(_ => DrainAll(force: true), null, _heartbeat, _heartbeat);

        using var reg = ct.Register(() =>
        {
            try { _manager.UnregisterListener(listener); } catch { }
            _channel?.Writer.TryComplete();
        });

        await foreach (var item in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;
    }

    private static bool IsMotion(Kind k) =>
        k is Kind.Accelerometer or Kind.Gyroscope or Kind.Magnetometer;

    private void ConfigureDownsamplers()
    {
        switch (_kind)
        {
            case Kind.Accelerometer:
                // Accel idles around 9.81 m/s² (gravity); 2.0 m/s² range
                // catches any meaningful motion (steps, pickup, shake).
                _peakDownsampler = new PeakDetectorDownsampler<Vec3Sample>(
                    TimeSpan.FromSeconds(2), 2.0, v => v,
                    cooldown: TimeSpan.FromSeconds(5));
                _meanVecDownsampler = new WindowedMeanDownsampler<Vec3Sample>(
                    TimeSpan.FromSeconds(30), v => v, trigger: "heartbeat");
                _heartbeat = TimeSpan.FromSeconds(30);
                break;
            case Kind.Gyroscope:
                // Gyro idles near 0; 0.5 rad/s range = noticeable rotation.
                _peakDownsampler = new PeakDetectorDownsampler<Vec3Sample>(
                    TimeSpan.FromSeconds(2), 0.5, v => v,
                    cooldown: TimeSpan.FromSeconds(5));
                _meanVecDownsampler = new WindowedMeanDownsampler<Vec3Sample>(
                    TimeSpan.FromSeconds(30), v => v, trigger: "heartbeat");
                _heartbeat = TimeSpan.FromSeconds(30);
                break;
            case Kind.Magnetometer:
                // Magnetic field range is large; 20 µT swing = orientation
                // change, near-magnet event, or motion through field.
                _peakDownsampler = new PeakDetectorDownsampler<Vec3Sample>(
                    TimeSpan.FromSeconds(2), 20.0, v => v,
                    cooldown: TimeSpan.FromSeconds(10));
                _meanVecDownsampler = new WindowedMeanDownsampler<Vec3Sample>(
                    TimeSpan.FromSeconds(30), v => v, trigger: "heartbeat");
                _heartbeat = TimeSpan.FromSeconds(30);
                break;
            case Kind.Light:
            case Kind.Pressure:
            case Kind.AmbientTemperature:
            case Kind.Proximity:
                _meanScalarDownsampler = new WindowedMeanDownsampler<float>(
                    TimeSpan.FromSeconds(60), s => s, trigger: "heartbeat");
                _heartbeat = TimeSpan.FromSeconds(60);
                break;
        }
    }

    private sealed class SensorListener : Java.Lang.Object, ISensorEventListener
    {
        private readonly PhoneSensorCollector _owner;
        public SensorListener(PhoneSensorCollector owner) => _owner = owner;
        public void OnAccuracyChanged(Sensor? sensor, [GeneratedEnum] SensorStatus accuracy) { }
        public void OnSensorChanged(SensorEvent? e) => _owner.HandleSensorEvent(e);
    }

    private void HandleSensorEvent(SensorEvent? e)
    {
        if (e?.Values is null || _channel is null) return;
        var now = DateTimeOffset.UtcNow;

        if (IsMotion(_kind) && e.Values.Count >= 3)
        {
            var sample = new Vec3Sample(e.Values[0], e.Values[1], e.Values[2]);
            _peakDownsampler?.Add(sample, now);
            _meanVecDownsampler?.Add(sample, now);
            DrainAll(force: false);
        }
        else if (e.Values.Count >= 1)
        {
            _meanScalarDownsampler?.Add(e.Values[0], now);
            DrainAll(force: false);
        }
    }

    /// `force=true` is the heartbeat tick — always try to drain the
    /// windowed-mean sampler so the kernel sees fresh ambient context
    /// even when the peak detector is quiet. `force=false` is a
    /// per-sample best-effort drain.
    private void DrainAll(bool force)
    {
        if (_channel is null) return;
        try
        {
            if (_peakDownsampler is not null && _peakDownsampler.TryDrain(out var peak))
                Emit(peak);
            if (force && _meanVecDownsampler is not null && _meanVecDownsampler.TryDrain(out var meanVec))
                Emit(meanVec);
            if (force && _meanScalarDownsampler is not null && _meanScalarDownsampler.TryDrain(out var meanS))
                Emit(meanS);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("lifeman", $"sensor drain {_surface}: {ex.Message}");
        }
    }

    private void Emit(DownsampleResult r)
    {
        if (_channel is null) return;
        var payload = JsonSerializer.Serialize(new
        {
            trigger = r.Trigger,
            window_start = r.WindowStart.ToString("O"),
            window_end = r.WindowEnd.ToString("O"),
            sample_count = r.SampleCount,
            summary = r.Payload,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        _channel.Writer.TryWrite(new CollectedEvent(_surface, payload, DateTimeOffset.UtcNow));
        _lastEmit = DateTimeOffset.UtcNow;
    }
}
