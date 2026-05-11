using Lifeman.Client.Windows.Collectors;

namespace Lifeman.Client.Windows.Tests;

public sealed class JpegEncoderTests
{
    [Fact]
    public void FitWidth_PassesThrough_When_SmallerThan_Cap()
    {
        var (w, h) = JpegEncoder.FitWidth(800, 600, 1280);
        Assert.Equal(800u, w);
        Assert.Equal(600u, h);
    }

    [Fact]
    public void FitWidth_PassesThrough_When_EqualTo_Cap()
    {
        var (w, h) = JpegEncoder.FitWidth(1280, 720, 1280);
        Assert.Equal(1280u, w);
        Assert.Equal(720u, h);
    }

    [Fact]
    public void FitWidth_Scales_Down_Preserving_AspectRatio()
    {
        var (w, h) = JpegEncoder.FitWidth(3840, 2160, 1280);
        Assert.Equal(1280u, w);
        Assert.Equal(720u, h);
    }

    [Fact]
    public void FitWidth_NeverReturns_ZeroEdges()
    {
        var (w, h) = JpegEncoder.FitWidth(0, 0, 1280);
        Assert.True(w >= 1);
        Assert.True(h >= 1);
    }

    [Fact]
    public void FitWidth_Tall_Portrait_Image()
    {
        var (w, h) = JpegEncoder.FitWidth(2560, 4000, 1280);
        Assert.Equal(1280u, w);
        Assert.Equal(2000u, h);
    }

    [Theory]
    [InlineData(0.0f, 0.01f)]
    [InlineData(-1.0f, 0.01f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(1.0f, 1.0f)]
    [InlineData(2.0f, 1.0f)]
    public void NormalizeQuality_Clamps_To_Range(float input, float expected)
    {
        Assert.Equal(expected, JpegEncoder.NormalizeQuality(input));
    }
}
