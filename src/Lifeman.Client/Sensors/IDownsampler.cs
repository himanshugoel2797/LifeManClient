namespace Lifeman.Client.Sensors;

/// A downsampler eats raw sensor samples and intermittently emits a
/// summary covering the window since the last emit. Lives in shared core
/// so both Android and (future) WearOS sensor collectors can reuse it.
///
/// Implementations are NOT required to be thread-safe — the collector
/// driving them owns the synchronisation (sensors fire on the
/// SensorManager thread; the collector marshals through a Channel).
public interface IDownsampler<TSample>
{
    /// Push one raw sample into the window.
    void Add(TSample sample, DateTimeOffset at);

    /// Returns true if a summary is ready to be emitted. When true, the
    /// sampler resets its internal window and the caller should ship the
    /// result. When false, the caller keeps adding samples.
    bool TryDrain(out DownsampleResult result);
}

/// One summarised window. Payload is downsampler-specific JSON.
public readonly record struct DownsampleResult(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    int SampleCount,
    string Trigger,
    object Payload);
