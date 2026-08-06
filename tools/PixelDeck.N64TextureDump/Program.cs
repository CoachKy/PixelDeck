using System.Text;
using PixelDeck.Emulation.N64;

// Dumps every texture Pixel64 decodes for a cartridge, as PNGs plus a manifest.
//
// Texture faults are invisible on screen: a texture that decodes to flat white
// and a surface that was never textured look the same. Being able to open the
// decoded texels turns "the ground has no texture" into a specific, checkable
// claim about a specific tile.
//
// Output is game-derived imagery. It stays local; do not commit it.

var rom = args.Length > 0 ? args[0] : null;
if (rom is null || !File.Exists(rom))
{
    Console.Error.WriteLine("usage: dotnet run -- <rom> [--fields N] [--output <dir>] [--max N]");
    return 2;
}

var fields = int.TryParse(Value("--fields"), out var f) ? f : 1800;
var maximum = int.TryParse(Value("--max"), out var m) ? m : 64;
var output = Value("--output")
    ?? Path.Combine("artifacts", "n64-textures", Path.GetFileNameWithoutExtension(rom));

var machine = N64Machine.Load(rom);
machine.Renderer.TextureCaptureEnabled = true;
for (var field = 0; field < fields; field++)
{
    machine.RunFrame();
}

var textures = machine.Renderer.CapturedTextures;
Directory.CreateDirectory(output);

var manifest = new StringBuilder();
manifest.AppendLine("index,format,width,height,palette,lutMode,bitsPerTexel,tmemBitOffset,decodes,uniformWhite");

var written = 0;
foreach (var texture in textures.Take(maximum))
{
    var name = $"{written:D3}-{texture.FormatName.Replace(' ', '_')}-{texture.Width}x{texture.Height}" +
               (texture.IsUniformWhite ? "-WHITE" : string.Empty);
    WritePng(Path.Combine(output, name + ".png"), texture);
    manifest.AppendLine(
        $"{written},{texture.FormatName},{texture.Width},{texture.Height},{texture.Palette}," +
        $"{texture.TextureLutMode},{texture.BitsPerTexel},{texture.BaseBitOffset}," +
        $"{texture.SampleCount},{texture.IsUniformWhite}");
    written++;
}

File.WriteAllText(Path.Combine(output, "textures.csv"), manifest.ToString());

var white = textures.Count(texture => texture.IsUniformWhite);
Console.WriteLine($"{textures.Count} distinct textures decoded, {written} written to {output}");
Console.WriteLine($"uniform white: {white} ({(textures.Count == 0 ? 0 : 100.0 * white / textures.Count):F1}%)");
foreach (var group in textures.GroupBy(texture => texture.FormatName).OrderByDescending(g => g.Count()))
{
    Console.WriteLine($"  {group.Key,-12} {group.Count(),4} textures, {group.Count(t => t.IsUniformWhite),4} white");
}

return 0;

string? Value(string flag)
{
    var index = Array.IndexOf(args, flag);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

// Minimal uncompressed-deflate PNG writer: avoids a System.Drawing dependency
// and keeps the tool runnable anywhere the emulator builds.
static void WritePng(string path, N64DecodedTexture texture)
{
    var width = texture.Width;
    var height = texture.Height;
    var raw = new byte[height * ((width * 4) + 1)];
    for (var y = 0; y < height; y++)
    {
        raw[y * ((width * 4) + 1)] = 0; // no filter
        Array.Copy(
            texture.Rgba,
            y * width * 4,
            raw,
            (y * ((width * 4) + 1)) + 1,
            width * 4);
    }

    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

    var header = new byte[13];
    WriteBigEndian(header, 0, width);
    WriteBigEndian(header, 4, height);
    header[8] = 8;  // bit depth
    header[9] = 6;  // RGBA
    WriteChunk(writer, "IHDR", header);
    WriteChunk(writer, "IDAT", ZlibStore(raw));
    WriteChunk(writer, "IEND", []);

    static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    static void WriteChunk(BinaryWriter writer, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, data.Length);
        writer.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        writer.Write(typeBytes);
        writer.Write(data);
        Span<byte> crc = stackalloc byte[4];
        WriteBigEndian(crc, unchecked((int)Crc32(typeBytes, data)));
        writer.Write(crc);

        static void WriteBigEndian(Span<byte> buffer, int value)
        {
            buffer[0] = (byte)(value >> 24);
            buffer[1] = (byte)(value >> 16);
            buffer[2] = (byte)(value >> 8);
            buffer[3] = (byte)value;
        }
    }

    static byte[] ZlibStore(byte[] data)
    {
        using var memory = new MemoryStream();
        memory.WriteByte(0x78);
        memory.WriteByte(0x01);
        var offset = 0;
        while (offset < data.Length)
        {
            var block = Math.Min(65535, data.Length - offset);
            memory.WriteByte((byte)(offset + block >= data.Length ? 1 : 0));
            memory.WriteByte((byte)(block & 0xFF));
            memory.WriteByte((byte)(block >> 8));
            memory.WriteByte((byte)(~block & 0xFF));
            memory.WriteByte((byte)(~block >> 8 & 0xFF));
            memory.Write(data, offset, block);
            offset += block;
        }

        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        var adler = (b << 16) | a;
        memory.WriteByte((byte)(adler >> 24));
        memory.WriteByte((byte)(adler >> 16));
        memory.WriteByte((byte)(adler >> 8));
        memory.WriteByte((byte)adler);
        return memory.ToArray();
    }

    static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in type) crc = Step(crc, value);
        foreach (var value in data) crc = Step(crc, value);
        return crc ^ 0xFFFFFFFFu;

        static uint Step(uint crc, byte value)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(crc & 1));
            }

            return crc;
        }
    }
}
