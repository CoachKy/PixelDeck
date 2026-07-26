using System.Text;

namespace PixelDeck.Emulation.N64;

public enum N64ImageByteOrder
{
    BigEndian,
    ByteSwapped,
    LittleEndian
}

public enum N64VideoRegion
{
    Ntsc,
    Pal
}

public enum N64Cic
{
    Unknown,
    Cic6101,
    Cic6102,
    Cic6103,
    Cic6105,
    Cic6106
}

public sealed class N64Cartridge
{
    private const uint BigEndianMagic = 0x80371240;
    private const uint ByteSwappedMagic = 0x37804012;
    private const uint LittleEndianMagic = 0x40123780;

    private N64Cartridge(byte[] rom, N64ImageByteOrder sourceByteOrder)
    {
        Rom = rom;
        SourceByteOrder = sourceByteOrder;
        ClockRate = ReadUInt32(0x04);
        EntryPoint = ReadUInt32(0x08);
        ReleaseAddress = ReadUInt32(0x0C);
        HeaderCrc1 = ReadUInt32(0x10);
        HeaderCrc2 = ReadUInt32(0x14);
        Title = DecodeTitle(rom.AsSpan(0x20, 20));
        MediaFormat = (char)rom[0x3B];
        CartridgeId = Encoding.ASCII.GetString(rom, 0x3C, 2);
        CountryCode = (char)rom[0x3E];
        Revision = rom[0x3F];
        GameCode = $"{MediaFormat}{CartridgeId}{CountryCode}";
        VideoRegion = GetVideoRegion(CountryCode);
        BootCodeCrc32 = ComputeCrc32(rom.AsSpan(0x40, 0xFC0));
        Cic = BootCodeCrc32 switch
        {
            0x6170A4A1 => N64Cic.Cic6101,
            0x90BB6CB5 => N64Cic.Cic6102,
            0x0B050EE0 => N64Cic.Cic6103,
            0x98BC2C86 => N64Cic.Cic6105,
            0xACC8580A => N64Cic.Cic6106,
            _ => N64Cic.Unknown
        };
    }

    public byte[] Rom { get; }

    public N64ImageByteOrder SourceByteOrder { get; }

    public uint ClockRate { get; }

    public uint EntryPoint { get; }

    public uint ReleaseAddress { get; }

    public uint HeaderCrc1 { get; }

    public uint HeaderCrc2 { get; }

    public string Title { get; }

    public char MediaFormat { get; }

    public string CartridgeId { get; }

    public char CountryCode { get; }

    public byte Revision { get; }

    public string GameCode { get; }

    public N64VideoRegion VideoRegion { get; }

    public uint BootCodeCrc32 { get; }

    public N64Cic Cic { get; }

    public bool IsSuperMario64UsRevision0 =>
        GameCode == "NSME" &&
        Revision == 0 &&
        HeaderCrc1 == 0x635A2BFF &&
        HeaderCrc2 == 0x8B022326 &&
        Rom.Length == 8 * 1024 * 1024;

    public bool IsPixel64TargetSupported =>
        IsSuperMario64UsRevision0 && Cic is N64Cic.Cic6101 or N64Cic.Cic6102;

    public string CompatibilityMessage => IsPixel64TargetSupported
        ? "Pixel64 development target: Super Mario 64 (USA) revision 0. " +
          "Cartridge loading and the base R4300i/VI hardware path are active; " +
          "RSP/RDP gameplay rendering is still under development."
        : "Pixel64 currently targets Super Mario 64 (USA) revision 0 only.";

    public static N64Cartridge Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return FromBytes(File.ReadAllBytes(path));
    }

    public static N64Cartridge Inspect(string path) => Load(path);

    public static N64Cartridge FromBytes(ReadOnlySpan<byte> source)
    {
        if (source.Length < 0x1000 || (source.Length & 3) != 0)
        {
            throw new InvalidDataException(
                "Nintendo 64 cartridge images must contain a complete 4 KiB header/boot block and be word aligned.");
        }

        var magic = ReadUInt32BigEndian(source, 0);
        var byteOrder = magic switch
        {
            BigEndianMagic => N64ImageByteOrder.BigEndian,
            ByteSwappedMagic => N64ImageByteOrder.ByteSwapped,
            LittleEndianMagic => N64ImageByteOrder.LittleEndian,
            _ => throw new InvalidDataException("The file does not contain a recognized Nintendo 64 cartridge header.")
        };

        var rom = source.ToArray();
        switch (byteOrder)
        {
            case N64ImageByteOrder.ByteSwapped:
                for (var offset = 0; offset < rom.Length; offset += 2)
                {
                    (rom[offset], rom[offset + 1]) = (rom[offset + 1], rom[offset]);
                }

                break;
            case N64ImageByteOrder.LittleEndian:
                for (var offset = 0; offset < rom.Length; offset += 4)
                {
                    (rom[offset], rom[offset + 3]) = (rom[offset + 3], rom[offset]);
                    (rom[offset + 1], rom[offset + 2]) = (rom[offset + 2], rom[offset + 1]);
                }

                break;
        }

        return new N64Cartridge(rom, byteOrder);
    }

    public byte ReadByte(int offset) =>
        offset >= 0 && offset < Rom.Length ? Rom[offset] : (byte)0;

    private uint ReadUInt32(int offset) => ReadUInt32BigEndian(Rom, offset);

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data, int offset) =>
        ((uint)data[offset] << 24) |
        ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) |
        data[offset + 3];

    private static string DecodeTitle(ReadOnlySpan<byte> title)
    {
        Span<char> characters = stackalloc char[title.Length];
        var length = 0;
        foreach (var value in title)
        {
            if (value is >= 0x20 and <= 0x7E)
            {
                characters[length++] = (char)value;
            }
            else if (value == 0)
            {
                characters[length++] = ' ';
            }
        }

        return new string(characters[..length]).Trim();
    }

    private static N64VideoRegion GetVideoRegion(char countryCode) => countryCode switch
    {
        '7' or 'A' or 'E' or 'J' => N64VideoRegion.Ntsc,
        _ => N64VideoRegion.Pal
    };

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ 0xEDB88320
                    : crc >> 1;
            }
        }

        return ~crc;
    }
}
