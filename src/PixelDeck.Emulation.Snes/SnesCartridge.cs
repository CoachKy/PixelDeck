using System.Security.Cryptography;
using System.Text;

namespace PixelDeck.Emulation.Snes;

public enum SnesMapMode
{
    LoRom,
    HiRom
}

public sealed record SnesCartridgeInfo(
    string Title,
    SnesMapMode MapMode,
    byte MapModeByte,
    byte CartridgeType,
    int RomSize,
    int RamSize,
    bool IsPal,
    ushort ResetVector,
    bool HasCopierHeader,
    bool IsSupported,
    string CompatibilityMessage);

public sealed class SnesCartridge
{
    private const int CopierHeaderSize = 512;
    private const int LoRomHeaderOffset = 0x7FC0;
    private const int HiRomHeaderOffset = 0xFFC0;

    private readonly byte[] _rom;
    private readonly byte[] _ram;

    private SnesCartridge(byte[] rom, SnesCartridgeInfo info)
    {
        _rom = rom;
        Info = info;
        _ram = info.RamSize == 0 ? [] : new byte[info.RamSize];
        Fingerprint = SHA256.HashData(rom);
    }

    public SnesCartridgeInfo Info { get; }

    public ReadOnlyMemory<byte> Fingerprint { get; }

    public static SnesCartridgeInfo Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ParseImage(File.ReadAllBytes(path)).Info;
    }

    public static SnesCartridge Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var cartridge = ParseImage(File.ReadAllBytes(path));
        if (!cartridge.Info.IsSupported)
        {
            throw new NotSupportedException(cartridge.Info.CompatibilityMessage);
        }

        return cartridge;
    }

    internal byte Read(uint address)
    {
        var bank = (byte)(address >> 16);
        var offset = (ushort)address;

        if (TryGetRamIndex(bank, offset, out var ramIndex))
        {
            return _ram[ramIndex];
        }

        return TryGetRomIndex(bank, offset, out var romIndex) ? _rom[romIndex] : (byte)0xFF;
    }

    internal void Write(uint address, byte value)
    {
        var bank = (byte)(address >> 16);
        var offset = (ushort)address;
        if (TryGetRamIndex(bank, offset, out var ramIndex))
        {
            _ram[ramIndex] = value;
        }
    }

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(Fingerprint.Length);
        writer.Write(Fingerprint.Span);
        writer.Write(_ram.Length);
        writer.Write(_ram);
    }

    internal void LoadState(BinaryReader reader)
    {
        var fingerprintLength = reader.ReadInt32();
        var fingerprint = reader.ReadBytes(fingerprintLength);
        if (!Fingerprint.Span.SequenceEqual(fingerprint))
        {
            throw new InvalidDataException("The save state belongs to a different SNES cartridge.");
        }

        var ramLength = reader.ReadInt32();
        if (ramLength != _ram.Length)
        {
            throw new InvalidDataException("The save state has an incompatible SNES save-RAM size.");
        }

        reader.ReadExactly(_ram);
    }

    private static SnesCartridge ParseImage(byte[] image)
    {
        if (image.Length < LoRomHeaderOffset + 64)
        {
            throw new InvalidDataException("The selected file is too small to contain a SNES cartridge image.");
        }

        var hasCopierHeader = image.Length % 1024 == CopierHeaderSize;
        var rom = hasCopierHeader ? image.AsSpan(CopierHeaderSize).ToArray() : image;
        var candidates = new List<HeaderCandidate>
        {
            ReadCandidate(rom, LoRomHeaderOffset, SnesMapMode.LoRom)
        };
        if (rom.Length >= HiRomHeaderOffset + 64)
        {
            candidates.Add(ReadCandidate(rom, HiRomHeaderOffset, SnesMapMode.HiRom));
        }

        var header = candidates.OrderByDescending(candidate => candidate.Score).First();
        if (header.Score < 8)
        {
            throw new InvalidDataException("PixelDeck could not find a credible SNES cartridge header.");
        }

        var declaredRomSize = header.RomSizeExponent is >= 7 and <= 20
            ? 1 << (header.RomSizeExponent + 10)
            : rom.Length;
        var ramSize = header.RamSizeExponent is > 0 and < 16
            ? 1 << (header.RamSizeExponent + 10)
            : 0;
        var hasSupportedMapByte = header.MapMode switch
        {
            SnesMapMode.LoRom => header.MapModeByte is 0x20 or 0x30,
            SnesMapMode.HiRom => header.MapModeByte is 0x21 or 0x31,
            _ => false
        };
        var hasEnhancementChip = (header.CartridgeType & 0xF0) != 0;
        var isSupported = hasSupportedMapByte && !hasEnhancementChip;
        var compatibility = !hasSupportedMapByte
            ? $"SNES map mode ${header.MapModeByte:X2} is not implemented yet."
            : hasEnhancementChip
                ? $"SNES enhancement-chip cartridge type ${header.CartridgeType:X2} is not implemented yet."
                : $"Compatible with the early local SNES core ({FormatMapMode(header.MapMode)}).";

        var info = new SnesCartridgeInfo(
            header.Title,
            header.MapMode,
            header.MapModeByte,
            header.CartridgeType,
            Math.Max(rom.Length, declaredRomSize),
            ramSize,
            IsPalRegion(header.DestinationCode),
            header.ResetVector,
            hasCopierHeader,
            isSupported,
            compatibility);
        return new SnesCartridge(rom, info);
    }

    private bool TryGetRomIndex(byte bank, ushort address, out int index)
    {
        index = 0;
        if (Info.MapMode == SnesMapMode.LoRom)
        {
            if (address < 0x8000 && (bank < 0x40 || bank is >= 0x70 and < 0x80 || bank is >= 0xF0))
            {
                return false;
            }

            index = (((bank & 0x7F) * 0x8000) + (address & 0x7FFF)) % _rom.Length;
            return true;
        }

        var fullRomBank = bank is >= 0x40 and <= 0x7D or >= 0xC0;
        if (!fullRomBank && address < 0x8000)
        {
            return false;
        }

        index = (((bank & 0x3F) * 0x10000) + address) % _rom.Length;
        return true;
    }

    private bool TryGetRamIndex(byte bank, ushort address, out int index)
    {
        index = 0;
        if (_ram.Length == 0)
        {
            return false;
        }

        if (Info.MapMode == SnesMapMode.LoRom)
        {
            if (bank is not (>= 0x70 and <= 0x7D or >= 0xF0) || address >= 0x8000)
            {
                return false;
            }

            index = ((((bank & 0x0F) * 0x8000) + address) % _ram.Length);
            return true;
        }

        if (bank is not (>= 0x20 and <= 0x3F or >= 0xA0 and <= 0xBF) || address is < 0x6000 or >= 0x8000)
        {
            return false;
        }

        index = ((((bank & 0x1F) * 0x2000) + (address - 0x6000)) % _ram.Length);
        return true;
    }

    private static HeaderCandidate ReadCandidate(byte[] rom, int offset, SnesMapMode expectedMap)
    {
        var titleBytes = rom.AsSpan(offset, 21);
        var title = Encoding.ASCII.GetString(titleBytes).Trim('\0', ' ');
        var mapMode = rom[offset + 0x15];
        var cartridgeType = rom[offset + 0x16];
        var romSize = rom[offset + 0x17];
        var ramSize = rom[offset + 0x18];
        var destination = rom[offset + 0x19];
        var complement = ReadWord(rom, offset + 0x1C);
        var checksum = ReadWord(rom, offset + 0x1E);
        var resetVector = ReadWord(rom, offset + 0x3C);
        var printableTitleBytes = 0;
        foreach (var value in titleBytes)
        {
            if (value is >= 0x20 and <= 0x7E)
            {
                printableTitleBytes++;
            }
        }

        var score = printableTitleBytes >= 18 ? 4 : printableTitleBytes >= 12 ? 2 : -3;
        if ((checksum ^ complement) == 0xFFFF && checksum != 0)
        {
            score += 8;
        }

        if (resetVector >= 0x8000)
        {
            score += 4;
        }

        var encodedMap = (mapMode & 0x0F) switch
        {
            0 => SnesMapMode.LoRom,
            1 => SnesMapMode.HiRom,
            _ => (SnesMapMode?)null
        };
        score += encodedMap == expectedMap ? 4 : -4;

        return new HeaderCandidate(
            title,
            expectedMap,
            mapMode,
            cartridgeType,
            romSize,
            ramSize,
            destination,
            resetVector,
            score);
    }

    private static ushort ReadWord(byte[] data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    private static bool IsPalRegion(byte destinationCode) => destinationCode is >= 0x02 and <= 0x0C;

    private static string FormatMapMode(SnesMapMode mapMode) =>
        mapMode == SnesMapMode.LoRom ? "LoROM" : "HiROM";

    private sealed record HeaderCandidate(
        string Title,
        SnesMapMode MapMode,
        byte MapModeByte,
        byte CartridgeType,
        byte RomSizeExponent,
        byte RamSizeExponent,
        byte DestinationCode,
        ushort ResetVector,
        int Score);
}
