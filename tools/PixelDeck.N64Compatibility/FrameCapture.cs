namespace PixelDeck.N64Compatibility;

internal static class FrameCapture
{
    public static void WriteBitmap(string path, ReadOnlySpan<uint> pixels, int width, int height)
    {
        if (pixels.Length != width * height)
        {
            throw new ArgumentException("The frame dimensions do not match its pixel data.", nameof(pixels));
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The capture path has no parent directory.", nameof(path)));

        const int fileHeaderSize = 14;
        const int dibHeaderSize = 40;
        var rowBytes = checked(width * 3);
        var rowPadding = (4 - (rowBytes & 3)) & 3;
        var imageBytes = checked((rowBytes + rowPadding) * height);
        var fileBytes = checked(fileHeaderSize + dibHeaderSize + imageBytes);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileBytes);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(fileHeaderSize + dibHeaderSize);
        writer.Write(dibHeaderSize);
        writer.Write(width);
        writer.Write(height);
        writer.Write((ushort)1);
        writer.Write((ushort)24);
        writer.Write(0);
        writer.Write(imageBytes);
        writer.Write(2_835);
        writer.Write(2_835);
        writer.Write(0);
        writer.Write(0);

        Span<byte> padding = stackalloc byte[3];
        for (var row = height - 1; row >= 0; row--)
        {
            var rowStart = row * width;
            for (var column = 0; column < width; column++)
            {
                var pixel = pixels[rowStart + column];
                writer.Write((byte)pixel);
                writer.Write((byte)(pixel >> 8));
                writer.Write((byte)(pixel >> 16));
            }

            writer.Write(padding[..rowPadding]);
        }
    }
}
