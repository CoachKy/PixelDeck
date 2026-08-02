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

/// <summary>
/// The battery-backed store a cartridge carries. The header does not encode
/// this, so it is resolved from the game code; anything unrecognised falls
/// back to EEPROM, which is the most common fitment.
/// </summary>
public enum N64SaveType
{
    None,
    Eeprom4Kbit,
    Eeprom16Kbit,
    Sram256Kbit,
    FlashRam1Mbit
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
        var profile = ResolveProfile(CartridgeId);
        SaveType = profile.SaveType;
        SupportsControllerPak = profile.SupportsControllerPak;
        UsesControllerPak = profile.ControllerPakByDefault;
        UsesTransferPak = profile.UsesTransferPak;
        Cic = BootCodeCrc32 switch
        {
            0x6170A4A1 => N64Cic.Cic6101,
            0x90BB6CB5 => N64Cic.Cic6102,
            0x0B050EE0 => N64Cic.Cic6103,
            0x98BC2C86 => N64Cic.Cic6105,
            0xACC8580A => N64Cic.Cic6106,
            _ => N64Cic.Unknown
        };
        EffectiveEntryPoint = AdjustEntryPointForCic(EntryPoint, Cic);
    }

    public byte[] Rom { get; }

    public N64ImageByteOrder SourceByteOrder { get; }

    public uint ClockRate { get; }

    public uint EntryPoint { get; }

    /// <summary>
    /// Address to which the cartridge's IPL3 actually transfers control.
    /// CIC-6103/6106 boot code relocates the header value before jumping.
    /// </summary>
    public uint EffectiveEntryPoint { get; }

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

    public N64SaveType SaveType { get; }

    /// <summary>
    /// Whether the title contains Controller Pak support. A game may support
    /// both cartridge storage and a Controller Pak (Mario Kart ghost data is
    /// the common example), so this is intentionally separate from
    /// <see cref="SaveType"/>.
    /// </summary>
    public bool SupportsControllerPak { get; }

    /// <summary>
    /// Whether Pixel64 installs a Controller Pak in port one by default.
    /// Controller-Pak-only games need this to save; games whose normal save is
    /// on the cartridge retain their expected default accessory. Mario Kart
    /// is the exception for now because its time-trial ghost data needs a Pak
    /// and PixelDeck does not yet expose a per-game accessory selector.
    /// </summary>
    public bool UsesControllerPak { get; }

    /// <summary>
    /// Whether the title has optional Transfer Pak integration. Pixel64 can
    /// run the N64 portion, but does not yet emulate the attached Game Boy.
    /// </summary>
    public bool UsesTransferPak { get; }

    /// <summary>
    /// N64 headers do not identify save hardware or controller accessories.
    /// The two-character cartridge ID does remain stable across media/region
    /// variants, so resolve the installed retail hardware from that ID. This
    /// table covers the local compatibility collection; unknown cartridges
    /// retain the platform's common 4-Kbit EEPROM fallback.
    /// </summary>
    private static N64CartridgeProfile ResolveProfile(string cartridgeId) => cartridgeId switch
    {
        "DO" => new(N64SaveType.Eeprom16Kbit),                         // Donkey Kong 64
        "GX" => new(N64SaveType.None, true, true),                    // Gauntlet Legends
        "GE" => new(N64SaveType.Eeprom4Kbit),                          // GoldenEye 007
        "ZL" => new(N64SaveType.Sram256Kbit),                          // Ocarina of Time
        "ZS" => new(N64SaveType.FlashRam1Mbit),                        // Majora's Mask
        "KG" => new(N64SaveType.Sram256Kbit, true),                    // MLB Featuring Ken Griffey Jr.
        "MF" => new(N64SaveType.Sram256Kbit, UsesTransferPak: true),   // Mario Golf
        "KT" => new(N64SaveType.Eeprom4Kbit, true, true),              // Mario Kart 64
        "M8" => new(N64SaveType.Eeprom16Kbit, UsesTransferPak: true),  // Mario Tennis
        "G5" => new(N64SaveType.None, true, true),                    // Mystical Ninja Starring Goemon
        "GM" => new(N64SaveType.None, true, true),                    // Goemon's Great Adventure
        "PW" => new(N64SaveType.Eeprom4Kbit),                          // Pilotwings 64
        "ET" => new(N64SaveType.None, true, true),                    // Quest 64
        "FX" => new(N64SaveType.Eeprom4Kbit),                          // Star Fox 64
        "RS" => new(N64SaveType.Eeprom4Kbit),                          // Rogue Squadron
        "SW" => new(N64SaveType.Eeprom4Kbit),                          // Shadows of the Empire
        "NA" => new(N64SaveType.Eeprom4Kbit),                          // Battle for Naboo
        "SM" => new(N64SaveType.Eeprom4Kbit),                          // Super Mario 64
        "WX" => new(N64SaveType.Sram256Kbit, true),                    // WWF WrestleMania 2000
        _ => new(N64SaveType.Eeprom4Kbit)
    };

    /// <summary>
    /// The size in bytes of <see cref="SaveType"/>'s backing store.
    /// </summary>
    public int SaveSize => SaveType switch
    {
        N64SaveType.None => UsesControllerPak ? N64Memory.ControllerPakSize : 0,
        N64SaveType.Eeprom4Kbit => 512,
        N64SaveType.Eeprom16Kbit => 2 * 1024,
        N64SaveType.Sram256Kbit => 32 * 1024,
        N64SaveType.FlashRam1Mbit => 128 * 1024,
        _ => 512
    };

    internal static uint AdjustEntryPointForCic(uint headerEntryPoint, N64Cic cic) =>
        cic switch
        {
            N64Cic.Cic6103 => headerEntryPoint - 0x00100000,
            N64Cic.Cic6106 => headerEntryPoint - 0x00200000,
            _ => headerEntryPoint
        };

    /// <summary>
    /// The conventional file extension for this save type, so a library never
    /// has to guess a store's format from its length.
    /// </summary>
    public string SaveExtension => SaveType switch
    {
        N64SaveType.None when UsesControllerPak => ".mpk",
        N64SaveType.Sram256Kbit => ".sra",
        N64SaveType.FlashRam1Mbit => ".fla",
        _ => ".eep"
    };

    public bool IsSuperMario64UsRevision0 =>
        GameCode == "NSME" &&
        Revision == 0 &&
        HeaderCrc1 == 0x635A2BFF &&
        HeaderCrc2 == 0x8B022326 &&
        Rom.Length == 8 * 1024 * 1024;

    public bool IsPixel64VerifiedTarget =>
        IsSuperMario64UsRevision0 && Cic is N64Cic.Cic6101 or N64Cic.Cic6102;

    public string CompatibilityMessage => IsPixel64VerifiedTarget
        ? "Pixel64 verified route: Super Mario 64 (USA) revision 0 reaches controllable castle gameplay; " +
          "visual output remains partial."
        : Cic == N64Cic.Unknown
            ? "Pixel64 boot attempt enabled. This cartridge uses an unrecognized boot security code, " +
              "so Pixel64 will use its default startup seed and compatibility is unverified."
            : "Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified.";

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

    private readonly record struct N64CartridgeProfile(
        N64SaveType SaveType,
        bool SupportsControllerPak = false,
        bool ControllerPakByDefault = false,
        bool UsesTransferPak = false);
}
