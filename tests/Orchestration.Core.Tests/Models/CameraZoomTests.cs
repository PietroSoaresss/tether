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

    /// <summary>
    /// The header is the only thing that says which node this is, so it never shrinks past device
    /// size — at 45% the old proportional chrome was a 20 px bar with a 5 px title.
    /// </summary>
    [Fact]
    public void ChromeNeverPaintsSmallerThanDeviceSize()
    {
        for (double zoom = Camera.MinZoom; zoom <= 1; zoom += 0.01)
            Assert.Equal(1, Camera.ChromeScale(zoom));

        Assert.Equal(2, Camera.ChromeScale(2));  // and still grows with the zoom above 1×
    }

    /// <summary>
    /// A label the user typed on the canvas stays readable all the way out — but never comes back
    /// bigger than the size they picked, which is what a bare floor would do to a 10 px label.
    /// </summary>
    [Theory]
    [InlineData(10)]  // the smallest the text tool offers
    [InlineData(18)]  // default
    [InlineData(96)]  // the largest
    public void LabelStaysReadableAcrossTheWholeZoomRange(double baseSize)
    {
        for (double zoom = Camera.MinZoom; zoom <= Camera.MaxZoom; zoom += 0.01)
        {
            double size = Camera.LabelSize(baseSize, zoom);
            Assert.True(size >= Math.Min(baseSize, Camera.MinLabelSize), $"illegible at {zoom}");
            Assert.True(size >= baseSize * zoom, $"smaller than the canvas at {zoom}");
        }

        Assert.Equal(baseSize, Camera.LabelSize(baseSize, 1));
    }

    [Fact]
    public void CollapseThresholdSitsInsideTheZoomRange()
    {
        Assert.InRange(Camera.CollapseZoom, Camera.MinZoom, Camera.MaxZoom);
    }
}
