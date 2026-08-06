using System.IO.Compression;
using System.Text;

namespace CruisnDiag;

// Minimal PNG writer so the harness has no image dependency.
internal static class Png
{
    public static void WriteArgb(string path, ReadOnlySpan<uint> pixels, int width, int height)
    {
        var raw = new byte[height * ((width * 4) + 1)];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * ((width * 4) + 1);
            raw[rowStart] = 0;
            for (var x = 0; x < width; x++)
            {
                var pixel = pixels[(y * width) + x];
                var offset = rowStart + 1 + (x * 4);
                raw[offset + 0] = (byte)(pixel >> 16);
                raw[offset + 1] = (byte)(pixel >> 8);
                raw[offset + 2] = (byte)pixel;
                raw[offset + 3] = 0xFF;
            }
        }

        Write(path, raw, width, height);
    }

    public static void WriteRgba(string path, byte[] rgba, int width, int height)
    {
        var raw = new byte[height * ((width * 4) + 1)];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * ((width * 4) + 1);
            raw[rowStart] = 0;
            Array.Copy(rgba, y * width * 4, raw, rowStart + 1, width * 4);
        }

        Write(path, raw, width, height);
    }

    private static void Write(string path, byte[] raw, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        BigEndian(header, 0, width);
        BigEndian(header, 4, height);
        header[8] = 8;
        header[9] = 6;
        Chunk(writer, "IHDR", header);
        Chunk(writer, "IDAT", Zlib(raw));
        Chunk(writer, "IEND", []);
    }

    private static void BigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void Chunk(BinaryWriter writer, string type, byte[] data)
    {
        var length = new byte[4];
        BigEndian(length, 0, data.Length);
        writer.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        writer.Write(typeBytes);
        writer.Write(data);
        var crc = new byte[4];
        BigEndian(crc, 0, unchecked((int)Crc32(typeBytes, data)));
        writer.Write(crc);
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        output.WriteByte(0x78);
        output.WriteByte(0x01);
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        var adler = Adler32(data);
        output.WriteByte((byte)(adler >> 24));
        output.WriteByte((byte)(adler >> 16));
        output.WriteByte((byte)(adler >> 8));
        output.WriteByte((byte)adler);
        return output.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < 256; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private static uint Crc32(byte[] first, byte[] second)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in first)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        foreach (var value in second)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
