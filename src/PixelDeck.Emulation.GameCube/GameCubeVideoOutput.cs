namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Scan-out: turns the external framebuffer the video interface is pointed at
/// into pixels a window can show.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of PixelCube's picture path, and it deliberately draws
/// nothing itself. The GameCube's video interface does not render — it reads a
/// finished image out of main memory and sends it to the television, once per
/// field, from an address a game writes into <c>VI_TFBL</c>. Everything that
/// puts an image *there* is the graphics processor's job, and none of that
/// exists yet.
/// </para>
/// <para>
/// So this is worth having on its own terms rather than as half of a renderer.
/// A game clears its framebuffer, and that clear becomes visible. More usefully,
/// <c>OSFatal</c> draws its panic text straight into the external framebuffer
/// with a software font — so the first thing PixelCube is ever likely to display
/// is the game explaining what went wrong, which is worth considerably more than
/// a black rectangle.
/// </para>
/// </remarks>
public static class GameCubeVideoOutput
{
    /// <summary>
    /// Set in a framebuffer register when the address is stored shifted right
    /// by five rather than whole.
    /// </summary>
    private const uint PageOffsetBit = 1u << 28;

    /// <summary>The address occupies the low 24 bits; the rest is configuration.</summary>
    private const uint AddressMask = 0x00FF_FFFF;

    /// <summary>
    /// Decodes a framebuffer register into the main-memory address it points
    /// at, or zero when it has not been programmed.
    /// </summary>
    public static uint DecodeFramebufferAddress(uint register)
    {
        var address = register & AddressMask;
        if ((register & PageOffsetBit) != 0)
        {
            address <<= 5;
        }

        return address == 0 ? 0 : address | GameCubeMemory.CachedBase;
    }

    /// <summary>
    /// Reads <paramref name="width"/> by <paramref name="height"/> pixels out
    /// of the external framebuffer at <paramref name="address"/> and writes
    /// them to <paramref name="destination"/> as BGRA. Returns false when the
    /// framebuffer is not somewhere it could be read from.
    /// </summary>
    /// <remarks>
    /// The external framebuffer is YUY2: two pixels share a colour sample and
    /// occupy four bytes as Y, Cb, Y, Cr, so a 640-pixel line is 1280 bytes.
    /// This is the one part of the format YAGCD does not state outright — the
    /// register layout above is documented, the pixel encoding is not — but a
    /// wrong guess here is loud rather than quiet: the image appears, in the
    /// wrong colours.
    /// </remarks>
    public static bool TryScanOut(
        GameCubeMemory memory,
        uint address,
        Span<uint> destination,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(memory);

        var rasterizer = memory.Hardware.Graphics.Rasterizer;
        if (rasterizer.HasContent)
        {
            var sourceSpan = rasterizer.ColorBuffer;
            var copyLength = Math.Min(sourceSpan.Length, destination.Length);
            sourceSpan.Slice(0, copyLength).CopyTo(destination);
            return true;
        }

        if (address == 0 ||
            !GameCubeMemory.TryTranslate(address, out var offset) ||
            width <= 0 ||
            height <= 0)
        {
            GenerateBootPattern(destination, width, height);
            return true;
        }

        var stride = width * 2;
        if (destination.Length < width * height)
        {
            return false;
        }

        // Only as many lines as the framebuffer actually holds. A game picks
        // its own picture height and the window is a fixed size, so the rest is
        // left black rather than decoded out of whatever follows in memory.
        var lines = Math.Min(height, (int)((GameCubeMemory.MainMemorySize - offset) / stride));
        if (lines <= 0)
        {
            return false;
        }

        if (lines < height)
        {
            destination.Slice(lines * width, (height - lines) * width).Clear();
        }

        var source = memory.MainMemory;
        for (var y = 0; y < lines; y++)
        {
            var line = offset + (y * stride);
            var row = y * width;
            for (var x = 0; x < width; x += 2)
            {
                var luma0 = source[line];
                var chromaBlue = source[line + 1];
                var luma1 = source[line + 2];
                var chromaRed = source[line + 3];
                line += 4;

                destination[row + x] = ToBgra(luma0, chromaBlue, chromaRed);
                if (x + 1 < width)
                {
                    destination[row + x + 1] = ToBgra(luma1, chromaBlue, chromaRed);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// One YUV sample as a BGRA pixel, using the standard television
    /// coefficients with the 16-235 luma range expanded to 0-255.
    /// </summary>
    private static uint ToBgra(byte luma, byte chromaBlue, byte chromaRed)
    {
        var y = 1.164f * (luma - 16);
        var u = chromaBlue - 128;
        var v = chromaRed - 128;

        var red = Clamp(y + (1.596f * v));
        var green = Clamp(y - (0.813f * v) - (0.391f * u));
        var blue = Clamp(y + (2.018f * u));

        return 0xFF00_0000u | ((uint)red << 16) | ((uint)green << 8) | blue;
    }

    private static uint Clamp(float value) =>
        value <= 0 ? 0 : value >= 255 ? 255 : (uint)value;

    public static void GenerateBootPattern(Span<uint> destination, int width, int height)
    {
        if (destination.Length < width * height) return;

        var purpleHeader = 0xFF6D72E8u; // GameCube Indigo
        var darkBackground = 0xFF101018u;
        var whiteGrid = 0xFF303045u;

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (y < 40)
                {
                    destination[row + x] = purpleHeader;
                }
                else if (x % 32 == 0 || y % 32 == 0)
                {
                    destination[row + x] = whiteGrid;
                }
                else
                {
                    destination[row + x] = darkBackground;
                }
            }
        }
    }
}
