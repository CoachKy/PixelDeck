using System.IO.Compression;
using System.Text;
namespace BootDiag;
internal static class Shot
{
    public static void Write(string path, ReadOnlySpan<uint> pixels, int width, int height)
    {
        var raw = new byte[height * ((width * 4) + 1)];
        for (var y = 0; y < height; y++)
        {
            var row = y * ((width * 4) + 1);
            for (var x = 0; x < width; x++)
            {
                var p = pixels[(y * width) + x];
                var o = row + 1 + (x * 4);
                raw[o] = (byte)(p >> 16); raw[o + 1] = (byte)(p >> 8); raw[o + 2] = (byte)p; raw[o + 3] = 0xFF;
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var s = File.Create(path);
        using var w = new BinaryWriter(s);
        w.Write(new byte[] { 0x89, 80, 78, 71, 13, 10, 26, 10 });
        var h = new byte[13];
        BE(h, 0, width); BE(h, 4, height); h[8] = 8; h[9] = 6;
        Chunk(w, "IHDR", h); Chunk(w, "IDAT", Zlib(raw)); Chunk(w, "IEND", []);
    }
    static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static void Chunk(BinaryWriter w, string t, byte[] d)
    {
        var l = new byte[4]; BE(l, 0, d.Length); w.Write(l);
        var tb = Encoding.ASCII.GetBytes(t); w.Write(tb); w.Write(d);
        var c = new byte[4]; BE(c, 0, unchecked((int)Crc(tb, d))); w.Write(c);
    }
    static byte[] Zlib(byte[] d)
    {
        using var m = new MemoryStream(); m.WriteByte(0x78); m.WriteByte(1);
        using (var z = new DeflateStream(m, CompressionLevel.Fastest, true)) z.Write(d, 0, d.Length);
        uint a = 1, b = 0; foreach (var v in d) { a = (a + v) % 65521; b = (b + a) % 65521; }
        var ad = (b << 16) | a;
        m.WriteByte((byte)(ad >> 24)); m.WriteByte((byte)(ad >> 16)); m.WriteByte((byte)(ad >> 8)); m.WriteByte((byte)ad);
        return m.ToArray();
    }
    static readonly uint[] T = Build();
    static uint[] Build() { var t = new uint[256]; for (uint i = 0; i < 256; i++) { var c = i; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; } return t; }
    static uint Crc(byte[] a, byte[] b) { var c = 0xFFFFFFFFu; foreach (var v in a) c = T[(c ^ v) & 0xFF] ^ (c >> 8); foreach (var v in b) c = T[(c ^ v) & 0xFF] ^ (c >> 8); return c ^ 0xFFFFFFFFu; }
}
