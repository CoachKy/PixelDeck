using PixelDeck.App.Services;

namespace PixelDeck.App.Tests;

public sealed class NesFramePresentationTests
{
    [Fact]
    public void HorizontalOverscanMaskHidesBothEightPixelEdgesOnly()
    {
        const int width = 256;
        const int height = 2;
        var frame = Enumerable.Range(0, width * height)
            .Select(index => 0xFF000000u | (uint)index)
            .ToArray();
        var original = frame.ToArray();

        NesFramePresentation.MaskHorizontalOverscan(frame, width, height);

        for (var row = 0; row < height; row++)
        {
            var rowStart = row * width;
            Assert.All(frame.AsSpan(rowStart, 8).ToArray(), pixel => Assert.Equal(0xFF000000u, pixel));
            Assert.Equal(
                original.AsSpan(rowStart + 8, width - 16).ToArray(),
                frame.AsSpan(rowStart + 8, width - 16).ToArray());
            Assert.All(
                frame.AsSpan(rowStart + width - 8, 8).ToArray(),
                pixel => Assert.Equal(0xFF000000u, pixel));
        }
    }

    [Fact]
    public void HorizontalOverscanMaskRejectsMalformedFrames()
    {
        Assert.Throws<ArgumentException>(() =>
            NesFramePresentation.MaskHorizontalOverscan(new uint[255 * 240], 256, 240));
    }
}
