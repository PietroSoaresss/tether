using Orchestration.Core.Models;
using Xunit;

namespace Orchestration.Core.Tests;

public class CameraZoomTests
{
    /// <summary>
    /// The bug this guards against: a clamp on the node's font size. A terminal runs at
    /// <c>(width × zoom) / (charWidth × zoom)</c> columns, so the count only stays constant while the
    /// type is strictly proportional to the zoom. The moment a clamp engages, zoom leaves the
    /// numerator, the column count drifts and the node resizes the pseudoconsole on every notch —
    /// which makes the running shell reflow its output just because someone scrolled.
    /// </summary>
    [Theory]
    [InlineData(13)]  // note
    [InlineData(14)]  // terminal default
    [InlineData(11)]  // a user who picked a small terminal font
    public void FontStaysProportionalAcrossTheWholeLiveZoomRange(double baseSize)
    {
        double expected = baseSize;

        for (double zoom = Camera.CollapseZoom; zoom <= Camera.MaxZoom; zoom += 0.01)
            Assert.Equal(expected, Camera.FontSize(baseSize, zoom) / zoom, 6);
    }

    /// <summary>Below the collapse threshold the node is a card, but a font size must still be > 0.</summary>
    [Fact]
    public void FontStaysPositiveBelowTheCollapseThreshold()
    {
        Assert.True(Camera.FontSize(14, Camera.MinZoom) > 0);
    }

    [Fact]
    public void CollapseThresholdSitsInsideTheZoomRange()
    {
        Assert.InRange(Camera.CollapseZoom, Camera.MinZoom, Camera.MaxZoom);
    }
}
