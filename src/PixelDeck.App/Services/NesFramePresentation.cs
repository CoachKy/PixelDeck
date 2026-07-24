namespace PixelDeck.App.Services;

internal static class NesFramePresentation
{
    internal const int HorizontalOverscanPixels = 8;
    private const uint OpaqueBlack = 0xFF000000;

    internal static void MaskHorizontalOverscan(Span<uint> pixels, int width, int height)
    {
        if (width < HorizontalOverscanPixels * 2 ||
            height <= 0 ||
            pixels.Length != width * height)
        {
            throw new ArgumentException("The NES presentation frame dimensions are invalid.", nameof(pixels));
        }

        for (var row = 0; row < height; row++)
        {
            var scanline = pixels.Slice(row * width, width);
            scanline[..HorizontalOverscanPixels].Fill(OpaqueBlack);
            scanline[^HorizontalOverscanPixels..].Fill(OpaqueBlack);
        }
    }
}
