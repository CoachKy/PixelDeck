using System.Security.Cryptography;
using System.Text;

namespace PixelDeck.Emulation.Snes;

public enum SnesMapMode
{
    LoRom,
    HiRom,
    ExHiRom
}

public sealed record SnesCartridgeInfo(
    string Title,
    SnesMapMode MapMode,
    byte MapModeByte,
    byte CartridgeType,
    int RomSize,
    int RamSize,
    bool HasBatteryBackedRam,
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
    private const int ExHiRomHeaderOffset = 0x40FFC0;
    private const int StandardHiRomCapacity = 4 * 1024 * 1024;

    private readonly byte[] _rom;
    private readonly byte[] _ram;
    private readonly string? _batterySavePath;
    private bool _ramDirty;

    private SnesCartridge(byte[] rom, SnesCartridgeInfo info, string? batterySavePath = null)
    {
        _rom = rom;
        Info = info;
        _ram = info.RamSize == 0 ? [] : new byte[info.RamSize];
        // Fresh save RAM must not read as all zeros: games validate their
        // battery data with checksums, and Ken Griffey Jr. Presents MLB's
        // roster check passes on a zero-filled block, leaving every player
        // name blank. The 0xFF fill matches uninitialized SRAM on real
        // cartridges (and snes9x/bsnes), so first boot rebuilds defaults.
        _ram.AsSpan().Fill(0xFF);
        _batterySavePath = info.HasBatteryBackedRam ? batterySavePath : null;
        Fingerprint = SHA256.HashData(rom);
        LoadBatterySave();
    }

    public SnesCartridgeInfo Info { get; }

    public ReadOnlyMemory<byte> Fingerprint { get; }

    internal bool HasDsp1 => IsDsp1Board(Info.MapMode, Info.MapModeByte, Info.CartridgeType);

    internal bool HasCx4 =>
        Info.MapMode == SnesMapMode.LoRom &&
        Info.CartridgeType == 0xF3;

    /// <summary>
    /// SA-1 boards report map mode $23 with cartridge types $32-$35. Kirby
    /// Super Star, Kirby's Dream Land 3 and Super Mario RPG are all $23/$35.
    /// </summary>
    internal bool HasSa1 =>
        Info.MapModeByte == 0x23 &&
        Info.CartridgeType is >= 0x32 and <= 0x35;

    /// <summary>
    /// The S-DD1 graphics decompression board: Star Ocean ($45, with battery
    /// RAM) and Street Fighter Alpha 2 ($43).
    /// </summary>
    internal bool HasSdd1 =>
        Info.MapModeByte == 0x32 &&
        Info.CartridgeType is 0x43 or 0x45;

    /// <summary>The Super FX / GSU boards.</summary>
    internal bool HasSuperFx =>
        Info.MapMode == SnesMapMode.LoRom &&
        Info.CartridgeType is 0x13 or 0x14 or 0x15 or 0x1A;

    public static SnesCartridgeInfo Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ParseImage(File.ReadAllBytes(path)).Info;
    }

    public static SnesCartridge Load(string path, string? batterySavePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var parsed = ParseImage(File.ReadAllBytes(path));
        if (!parsed.Info.IsSupported)
        {
            throw new NotSupportedException(parsed.Info.CompatibilityMessage);
        }

        return new SnesCartridge(parsed._rom, parsed.Info, batterySavePath);
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
            if (_ram[ramIndex] != value)
            {
                _ram[ramIndex] = value;
                _ramDirty = true;
            }
        }
    }

    /// <summary>
    /// Raw ROM image, for coprocessors that apply their own bank mapping
    /// rather than going through the console's memory map.
    /// </summary>
    internal ReadOnlySpan<byte> RomSpan => _rom;

    internal byte ReadCx4RomByte(uint address, int displacement = 0)
    {
        // CX4 source pointers use the cartridge's raw LoROM byte layout:
        // bank selects a 32 KiB page and the high address bit is ignored.
        var mappedAddress =
            (int)(((address & 0xFF0000) >> 1) + (address & 0x7FFF)) +
            displacement;
        return _rom[MirrorAddress(mappedAddress, _rom.Length)];
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
        if (fingerprintLength != Fingerprint.Length)
        {
            throw new InvalidDataException("The SNES save state contains an invalid game fingerprint.");
        }

        var fingerprint = reader.ReadBytes(fingerprintLength);
        if (fingerprint.Length != fingerprintLength || !Fingerprint.Span.SequenceEqual(fingerprint))
        {
            throw new InvalidDataException("The save state belongs to a different SNES cartridge.");
        }

        var ramLength = reader.ReadInt32();
        if (ramLength != _ram.Length)
        {
            throw new InvalidDataException("The save state has an incompatible SNES save-RAM size.");
        }

        reader.ReadExactly(_ram);
        _ramDirty = true;
    }

    public void FlushBatterySave()
    {
        if (_batterySavePath is null || !_ramDirty)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_batterySavePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _batterySavePath + ".tmp";
        WriteDurableBytes(temporaryPath, _ram);
        File.Move(temporaryPath, _batterySavePath, overwrite: true);
        _ramDirty = false;
    }

    private void LoadBatterySave()
    {
        if (_batterySavePath is null)
        {
            return;
        }

        var temporaryPath = _batterySavePath + ".tmp";
        var finalExists = File.Exists(_batterySavePath);
        var finalData = finalExists ? File.ReadAllBytes(_batterySavePath) : null;
        if (finalData?.Length == _ram.Length)
        {
            finalData.CopyTo(_ram, 0);
            _ramDirty = false;
            return;
        }

        var temporaryExists = File.Exists(temporaryPath);
        var temporaryData = temporaryExists ? File.ReadAllBytes(temporaryPath) : null;
        if (temporaryData?.Length == _ram.Length)
        {
            File.Move(temporaryPath, _batterySavePath, overwrite: true);
            temporaryData.CopyTo(_ram, 0);
            _ramDirty = false;
            return;
        }

        if (finalExists || temporaryExists)
        {
            var invalidLength = finalData?.Length ?? temporaryData?.Length ?? 0;
            throw new InvalidDataException(
                $"The SNES battery save is {invalidLength} bytes, but this cartridge expects {_ram.Length} bytes.");
        }
    }

    private static void WriteDurableBytes(string path, ReadOnlySpan<byte> data)
    {
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4_096,
            FileOptions.WriteThrough);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
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
        if (rom.Length >= ExHiRomHeaderOffset + 64)
        {
            candidates.Add(ReadCandidate(rom, ExHiRomHeaderOffset, SnesMapMode.ExHiRom));
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
        // The SNES hardware itself never reads this byte; it exists purely
        // for tools. Real commercial carts occasionally ship with stray bits
        // here (e.g. Contra III's $53, Super Adventure Island's $44 instead
        // of $20), so once the checksum has already proven this is the
        // genuine header at this offset, the map mode that offset was
        // located under is trusted over a literal byte match.
        var hasSupportedMapByte =
            header.MapMode is SnesMapMode.LoRom or SnesMapMode.HiRom or SnesMapMode.ExHiRom &&
            (header.ChecksumValid ||
                header.MapMode switch
                {
                    // $23 is the SA-1 board and $32 the S-DD1 board; both sit on
                    // a LoROM layout. Translation patches often leave the
                    // checksum stale, so the map byte has to be accepted on its
                    // own for those carts to load at all.
                    SnesMapMode.LoRom => header.MapModeByte is 0x20 or 0x30 or 0x23 or 0x32,
                    SnesMapMode.HiRom => header.MapModeByte is 0x21 or 0x31,
                    SnesMapMode.ExHiRom => header.MapModeByte is 0x25 or 0x35,
                    _ => false
                });
        // The low nibble describes the installed storage while the high
        // nibble selects a coprocessor family. $03-$06 with high nibble zero
        // are DSP-family boards. PixelSNES currently implements DSP-1, the
        // variant used by standard $03-$06 DSP boards such as Super Mario Kart.
        var hasDsp1 = IsDsp1Board(header.MapMode, header.MapModeByte, header.CartridgeType);
        var hasCx4 =
            header.MapMode == SnesMapMode.LoRom &&
            header.CartridgeType == 0xF3;
        var hasSa1 =
            header.MapModeByte == 0x23 &&
            header.CartridgeType is >= 0x32 and <= 0x35;
        var hasSdd1 =
            header.MapModeByte == 0x32 &&
            header.CartridgeType is 0x43 or 0x45;
        // Super FX boards: $13 is Star Fox's GSU-1, $15 and $1A the GSU-2 used
        // by Yoshi's Island, Stunt Race FX and Star Fox 2.
        var hasSuperFx =
            header.MapMode == SnesMapMode.LoRom &&
            header.CartridgeType is 0x13 or 0x14 or 0x15 or 0x1A;
        var hasUnsupportedEnhancementChip =
            header.CartridgeType > 0x02 &&
            !hasDsp1 &&
            !hasCx4 &&
            !hasSa1 &&
            !hasSdd1 &&
            !hasSuperFx;
        var hasBatteryBackedRam = header.CartridgeType is 0x02 or 0x05 or 0x06;
        var isPal = IsPalRegion(header.DestinationCode);
        var isSupported = hasSupportedMapByte && !hasUnsupportedEnhancementChip;
        // PAL only changes the field rate and scanline count (50 Hz/312 lines
        // vs. NTSC's 60 Hz/262), both of which are already read from
        // SnesCartridgeInfo.IsPal by SnesBus/SnesPpu/SnesMachine rather than
        // assumed. The CPU/PPU/APU cycle ratios that matter for emulation
        // stay identical across regions, so there was no functional reason
        // to reject these carts.
        var region = isPal ? "PAL" : "NTSC";
        var compatibility = !hasSupportedMapByte
            ? $"SNES map mode ${header.MapModeByte:X2} is not implemented yet."
            : hasUnsupportedEnhancementChip
                ? $"SNES enhancement-chip cartridge type ${header.CartridgeType:X2} is not implemented yet."
                : hasSuperFx
                    ? $"PixelSNES Super FX support ({FormatMapMode(header.MapMode)}, {region}) is in progress. The GSU core, memory map, instruction cache and pixel plotter are active, but timing and the plot hardware are approximate, so this cartridge may not play correctly yet."
                : hasSdd1
                    ? $"PixelSNES S-DD1 support ({FormatMapMode(header.MapMode)}, {region}) is in progress. The bank mapper and DMA-driven graphics decompressor are active, but the decompressor has not been verified against real output yet, so tiles may be wrong."
                : hasSa1
                    ? $"PixelSNES SA-1 support ({FormatMapMode(header.MapMode)}, {region}) is in progress. The coprocessor's CPU, memory map, register file, timers, arithmetic unit, variable-length bit processing and DMA are active. Dual-processor timing is approximate, so this cartridge may not play correctly yet."
                : hasDsp1
                    ? $"Compatible with PixelSNES ({FormatMapMode(header.MapMode)}, {region}, DSP-1). CPU, PPU video, and S-DSP stereo audio are active."
                    : hasCx4
                        ? $"Compatible with PixelSNES ({FormatMapMode(header.MapMode)}, {region}, CX4). CPU, PPU video, CX4 graphics commands, and S-DSP stereo audio are active."
                    : $"Compatible with PixelSNES ({FormatMapMode(header.MapMode)}, {region}). CPU, PPU video, and S-DSP stereo audio are active.";

        var info = new SnesCartridgeInfo(
            header.Title,
            header.MapMode,
            header.MapModeByte,
            header.CartridgeType,
            Math.Max(rom.Length, declaredRomSize),
            ramSize,
            hasBatteryBackedRam,
            isPal,
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

            var mappedAddress = ((bank & 0x7F) * 0x8000) + (address & 0x7FFF);
            index = MirrorAddress(mappedAddress, _rom.Length);
            return true;
        }

        if (Info.MapMode == SnesMapMode.ExHiRom)
        {
            if (bank is 0x7E or 0x7F ||
                (address < 0x8000 && (bank < 0x40 || bank is >= 0x80 and < 0xC0)))
            {
                return false;
            }

            var mappedAddress = ((bank & 0x3F) * 0x10000) + address;
            if (bank < 0x80)
            {
                var extendedSize = _rom.Length - StandardHiRomCapacity;
                index = StandardHiRomCapacity + MirrorAddress(mappedAddress, extendedSize);
            }
            else
            {
                index = MirrorAddress(
                    mappedAddress,
                    Math.Min(_rom.Length, StandardHiRomCapacity));
            }

            return true;
        }

        var fullRomBank = bank is >= 0x40 and <= 0x7D or >= 0xC0;
        if (!fullRomBank && address < 0x8000)
        {
            return false;
        }

        var hiRomAddress = ((bank & 0x3F) * 0x10000) + address;
        index = MirrorAddress(hiRomAddress, _rom.Length);
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
        var checksumValid = (checksum ^ complement) == 0xFFFF && checksum != 0;
        if (checksumValid)
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
            5 => SnesMapMode.ExHiRom,
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
            score,
            checksumValid);
    }

    private static ushort ReadWord(byte[] data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    private static int MirrorAddress(int address, int size)
    {
        if (size <= 0)
        {
            throw new InvalidDataException("The SNES ROM contains an empty mapped region.");
        }

        if (address < size)
        {
            return address;
        }

        var mask = 1 << 30;
        while ((address & mask) == 0)
        {
            mask >>= 1;
        }

        return size <= (address & mask)
            ? MirrorAddress(address - mask, size)
            : mask + MirrorAddress(address - mask, size - mask);
    }

    private static bool IsPalRegion(byte destinationCode) => destinationCode is >= 0x02 and <= 0x0C;

    private static bool IsDsp1Board(SnesMapMode mapMode, byte mapModeByte, byte cartridgeType) =>
        // Type $03 with a $30 map byte denotes DSP-4. Type $05 with a
        // $20 map byte denotes DSP-2; DSP-3 is another LoROM variant.
        // The HiROM $05 board used by Super Mario Kart is unambiguously DSP-1.
        (cartridgeType == 0x03 && mapModeByte != 0x30) ||
        (cartridgeType == 0x05 && mapMode == SnesMapMode.HiRom);

    private static string FormatMapMode(SnesMapMode mapMode) =>
        mapMode switch
        {
            SnesMapMode.LoRom => "LoROM",
            SnesMapMode.HiRom => "HiROM",
            SnesMapMode.ExHiRom => "ExHiROM",
            _ => "unknown"
        };

    private sealed record HeaderCandidate(
        string Title,
        SnesMapMode MapMode,
        byte MapModeByte,
        byte CartridgeType,
        byte RomSizeExponent,
        byte RamSizeExponent,
        byte DestinationCode,
        ushort ResetVector,
        int Score,
        bool ChecksumValid);
}
