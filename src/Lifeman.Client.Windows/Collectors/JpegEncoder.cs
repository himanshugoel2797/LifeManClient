using System.Runtime.Versioning;

namespace Lifeman.Client.Windows.Collectors;

/// Pure-logic helpers for the screen-capture pipeline. Lifted out so the
/// resize math is testable without spinning up a D3D device.
[SupportedOSPlatform("windows10.0.19041.0")]
public static class JpegEncoder
{
    /// Compute the target dimensions for a downscale that caps the
    /// longest *width* edge at <paramref name="maxWidth"/> while
    /// preserving aspect ratio. If the source is already smaller, returns
    /// the source dimensions unchanged. Always returns positive integers
    /// (clamps to >= 1) so encoders never get a zero edge.
    public static (uint Width, uint Height) FitWidth(uint sourceWidth, uint sourceHeight, uint maxWidth)
    {
        if (sourceWidth == 0 || sourceHeight == 0) return (1, 1);
        if (sourceWidth <= maxWidth) return (sourceWidth, sourceHeight);
        var scale = (double)maxWidth / sourceWidth;
        var h = (uint)Math.Max(1, Math.Round(sourceHeight * scale));
        return (maxWidth, h);
    }

    /// Clamp JPEG quality to a sensible 1..100 range.
    public static float NormalizeQuality(float q)
        => q < 0.01f ? 0.01f : (q > 1.0f ? 1.0f : q);
}
