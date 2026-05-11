using Lifeman.Client.Sensors;

namespace Lifeman.Client.Tests;

public sealed class SensorsDownsamplerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---------- Vec3Sample ----------

    [Fact]
    public void Vec3_Magnitude_Computes_Euclidean()
    {
        var v = new Vec3Sample(3, 4, 0);
        Assert.Equal(5f, v.Magnitude, 5);
    }

    [Fact]
    public void Vec3_Magnitude_Zero()
    {
        Assert.Equal(0f, new Vec3Sample(0, 0, 0).Magnitude);
    }

    // ---------- WindowedMeanDownsampler (scalar) ----------

    [Fact]
    public void Scalar_WindowedMean_RequiresFullWindow()
    {
        var d = new WindowedMeanDownsampler<double>(TimeSpan.FromSeconds(10), s => s);
        d.Add(1.0, T0);
        d.Add(3.0, T0 + TimeSpan.FromSeconds(5));
        Assert.False(d.TryDrain(out _));
    }

    [Fact]
    public void Scalar_WindowedMean_EmitsOnceFullWindowElapsed()
    {
        var d = new WindowedMeanDownsampler<double>(TimeSpan.FromSeconds(10), s => s);
        d.Add(1.0, T0);
        d.Add(3.0, T0 + TimeSpan.FromSeconds(5));
        d.Add(5.0, T0 + TimeSpan.FromSeconds(10));

        Assert.True(d.TryDrain(out var r));
        Assert.Equal(3, r.SampleCount);
        Assert.Equal(T0, r.WindowStart);
        Assert.Equal(T0 + TimeSpan.FromSeconds(10), r.WindowEnd);

        // Inspect via reflection on anonymous type — simpler: serialize.
        var json = System.Text.Json.JsonSerializer.Serialize(r.Payload);
        Assert.Contains("\"mean\":3", json);
        Assert.Contains("\"min\":1", json);
        Assert.Contains("\"max\":5", json);
        Assert.Contains("scalar_mean", json);
    }

    [Fact]
    public void Scalar_WindowedMean_ResetsAfterDrain()
    {
        var d = new WindowedMeanDownsampler<double>(TimeSpan.FromSeconds(10), s => s);
        d.Add(2.0, T0);
        d.Add(4.0, T0 + TimeSpan.FromSeconds(10));
        Assert.True(d.TryDrain(out _));
        // After drain, no immediate re-emit possible without new samples.
        Assert.False(d.TryDrain(out _));

        d.Add(10.0, T0 + TimeSpan.FromSeconds(20));
        d.Add(20.0, T0 + TimeSpan.FromSeconds(31));
        Assert.True(d.TryDrain(out var r2));
        var json = System.Text.Json.JsonSerializer.Serialize(r2.Payload);
        Assert.Contains("\"mean\":15", json);
    }

    [Fact]
    public void Scalar_WindowedMean_EmptyWindow_DoesNotDrain()
    {
        var d = new WindowedMeanDownsampler<double>(TimeSpan.FromSeconds(5), s => s);
        Assert.False(d.TryDrain(out _));
    }

    // ---------- WindowedMeanDownsampler (vec3) ----------

    [Fact]
    public void Vec3_WindowedMean_AveragesComponentsAndMagnitude()
    {
        var d = new WindowedMeanDownsampler<Vec3Sample>(TimeSpan.FromSeconds(10), v => v);
        d.Add(new Vec3Sample(0, 0, 10), T0);
        d.Add(new Vec3Sample(0, 0, 10), T0 + TimeSpan.FromSeconds(5));
        d.Add(new Vec3Sample(0, 0, 10), T0 + TimeSpan.FromSeconds(10));

        Assert.True(d.TryDrain(out var r));
        var json = System.Text.Json.JsonSerializer.Serialize(r.Payload);
        Assert.Contains("vec3_mean", json);
        Assert.Contains("\"mean_z\":10", json);
        Assert.Contains("\"mean_magnitude\":10", json);
    }

    // ---------- PeakDetectorDownsampler ----------

    [Fact]
    public void PeakDetector_QuietSignal_DoesNotEmit()
    {
        var d = new PeakDetectorDownsampler<double>(TimeSpan.FromSeconds(2), 5.0, s => s);
        for (int i = 0; i < 20; i++)
            d.Add(9.81, T0 + TimeSpan.FromMilliseconds(50 * i));
        Assert.False(d.TryDrain(out _));
    }

    [Fact]
    public void PeakDetector_BurstAboveThreshold_Emits()
    {
        var d = new PeakDetectorDownsampler<double>(TimeSpan.FromSeconds(2), 5.0, s => s);
        d.Add(9.81, T0);
        d.Add(9.81, T0 + TimeSpan.FromMilliseconds(100));
        d.Add(20.0, T0 + TimeSpan.FromMilliseconds(200)); // burst
        d.Add(9.81, T0 + TimeSpan.FromMilliseconds(300));

        Assert.True(d.TryDrain(out var r));
        Assert.Equal("peak", r.Trigger);
        var json = System.Text.Json.JsonSerializer.Serialize(r.Payload);
        Assert.Contains("peak_event", json);
        Assert.Contains("\"max\":20", json);
    }

    [Fact]
    public void PeakDetector_RespectsCooldown()
    {
        var d = new PeakDetectorDownsampler<double>(
            TimeSpan.FromSeconds(2), 5.0, s => s,
            cooldown: TimeSpan.FromSeconds(10));

        d.Add(0, T0);
        d.Add(20, T0 + TimeSpan.FromMilliseconds(100));
        Assert.True(d.TryDrain(out _));

        // Another burst within the cooldown should NOT emit.
        d.Add(0, T0 + TimeSpan.FromSeconds(3));
        d.Add(30, T0 + TimeSpan.FromSeconds(3.1));
        Assert.False(d.TryDrain(out _));

        // After cooldown, next burst emits again.
        d.Add(0, T0 + TimeSpan.FromSeconds(15));
        d.Add(30, T0 + TimeSpan.FromSeconds(15.1));
        Assert.True(d.TryDrain(out _));
    }

    [Fact]
    public void PeakDetector_EvictsOldSamples()
    {
        // Old high sample falls out of the window; subsequent quiet
        // signal should not retroactively trigger.
        var d = new PeakDetectorDownsampler<double>(
            TimeSpan.FromSeconds(1), 5.0, s => s,
            cooldown: TimeSpan.Zero);
        d.Add(50, T0);                                       // ancient peak
        d.Add(0, T0 + TimeSpan.FromSeconds(2));              // window cleared
        d.Add(0, T0 + TimeSpan.FromSeconds(2.5));
        Assert.False(d.TryDrain(out _));
    }

    [Fact]
    public void PeakDetector_Vec3_Overload_UsesMagnitude()
    {
        var d = new PeakDetectorDownsampler<Vec3Sample>(
            TimeSpan.FromSeconds(2), 5.0, v => v);
        d.Add(new Vec3Sample(0, 0, 9.81f), T0);
        d.Add(new Vec3Sample(0, 0, 20f), T0 + TimeSpan.FromMilliseconds(100));
        Assert.True(d.TryDrain(out var r));
        Assert.Equal("peak", r.Trigger);
    }

    [Fact]
    public void PeakDetector_EmptyState_NoEmit()
    {
        var d = new PeakDetectorDownsampler<double>(TimeSpan.FromSeconds(1), 1.0, s => s);
        Assert.False(d.TryDrain(out _));
    }
}
