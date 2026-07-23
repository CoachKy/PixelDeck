namespace PixelDeck.Emulation.Nes;

public enum NametableMirroring
{
    OneScreenLower,
    OneScreenUpper,
    Horizontal,
    Vertical,
    FourScreen
}

public enum NesTimingMode
{
    Ntsc,
    Pal,
    MultipleRegion,
    Dendy
}

public sealed class Cartridge
{
    private readonly IMapper _mapper;
    private readonly byte[] _romFingerprint;
    private readonly CartridgeRam _programRam;
    private readonly string? _batterySavePath;

    private Cartridge(
        IMapper mapper,
        CartridgeRam programRam,
        CartridgeInfo info,
        byte[] romFingerprint,
        string? batterySavePath)
    {
        _mapper = mapper;
        _programRam = programRam;
        MapperNumber = info.MapperNumber;
        SubmapperNumber = info.SubmapperNumber;
        HasBatteryBackedRam = info.HasBatteryBackedRam;
        TimingMode = info.TimingMode;
        DefaultInputDevice = info.DefaultInputDevice;
        _romFingerprint = romFingerprint;
        _batterySavePath = HasBatteryBackedRam ? batterySavePath : null;
        LoadBatterySave();
    }

    public int MapperNumber { get; }

    public int SubmapperNumber { get; }

    public bool HasBatteryBackedRam { get; }

    public NesTimingMode TimingMode { get; }

    public byte DefaultInputDevice { get; }

    public NametableMirroring Mirroring => _mapper.Mirroring;

    public static bool IsMapperSupported(int mapperNumber, int submapperNumber = 0) => (mapperNumber, submapperNumber) switch
    {
        (0, 0) => true,
        (1, 0) => true,
        (2, 0 or 1 or 2) => true,
        (3, 0 or 1 or 2) => true,
        (4, 0 or 4) => true,
        (7, 0 or 1 or 2) => true,
        (66, 0) => true,
        _ => false
    };

    public static CartridgeInfo Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Span<byte> header = stackalloc byte[16];
        using var stream = File.OpenRead(path);
        stream.ReadExactly(header);

        if (header[0] != (byte)'N' || header[1] != (byte)'E' || header[2] != (byte)'S' || header[3] != 0x1A)
        {
            throw new InvalidDataException("The selected file does not contain a valid iNES header.");
        }

        var fileLength = stream.Length;
        return InspectHeader(header, fileLength);
    }

    public static Cartridge Load(
        string path,
        string? batterySavePath = null,
        Mmc3IrqRevision mmc3IrqRevision = Mmc3IrqRevision.Auto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var image = File.ReadAllBytes(path);

        if (image.Length < 16 || image[0] != (byte)'N' || image[1] != (byte)'E' || image[2] != (byte)'S' || image[3] != 0x1A)
        {
            throw new InvalidDataException("The selected file does not contain a valid iNES header.");
        }

        var header = image.AsSpan(0, 16);
        var info = InspectHeader(header, image.Length);
        var flags6 = image[6];
        var mapperNumber = info.MapperNumber;
        var submapperNumber = info.SubmapperNumber;
        var prgLength = GetRomSize(image[4], (byte)(image[9] & 0x0F), info.IsNes20, 16_384);
        var chrLength = GetRomSize(image[5], (byte)(image[9] >> 4), info.IsNes20, 8_192);

        var offset = 16 + ((flags6 & 0x04) != 0 ? 512 : 0);

        if (prgLength == 0 || image.Length < offset + prgLength + chrLength)
        {
            throw new InvalidDataException("The iNES image is truncated or has invalid PRG/CHR sizes.");
        }

        var prgRom = image.AsSpan(offset, prgLength).ToArray();
        offset += prgLength;
        var usesChrRam = chrLength == 0;
        var chrRamSize = Math.Max(info.ChrRamSize + info.ChrNvRamSize, 8_192);
        var chr = usesChrRam ? new byte[chrRamSize] : image.AsSpan(offset, chrLength).ToArray();
        var programRamSize = Math.Max(info.PrgRamSize + info.PrgNvRamSize, info.HasTrainer ? 8_192 : 0);
        var programRam = new CartridgeRam(programRamSize, info.HasBatteryBackedRam);
        if (info.HasTrainer)
        {
            image.AsSpan(16, 512).CopyTo(programRam.Data.AsSpan(0x1000, 512));
        }

        var mirroring = (flags6 & 0x08) != 0
            ? NametableMirroring.FourScreen
            : (flags6 & 0x01) != 0
                ? NametableMirroring.Vertical
                : NametableMirroring.Horizontal;

        if (!IsMapperSupported(mapperNumber, submapperNumber))
        {
            var submapperSuffix = submapperNumber == 0 ? string.Empty : $" submapper {submapperNumber}";
            throw new NotSupportedException($"NES mapper {mapperNumber}{submapperSuffix} is not implemented yet.");
        }

        var hasBusConflicts = submapperNumber switch
        {
            1 => false,
            2 => true,
            _ => mapperNumber is 2 or 3
        };

        IMapper mapper = mapperNumber switch
        {
            0 => new Mapper0(prgRom, chr, usesChrRam, mirroring, programRam),
            1 => new Mapper1(prgRom, chr, usesChrRam, programRam),
            2 => new Mapper2(prgRom, chr, usesChrRam, mirroring, hasBusConflicts, programRam),
            3 => new Mapper3(prgRom, chr, usesChrRam, mirroring, hasBusConflicts, programRam),
            4 => new Mapper4(
                prgRom,
                chr,
                usesChrRam,
                mirroring,
                programRam,
                ResolveMmc3IrqRevision(info, mmc3IrqRevision)),
            7 => new Mapper7(prgRom, chr, usesChrRam, hasBusConflicts, programRam),
            66 => new Mapper66(prgRom, chr, usesChrRam, mirroring, programRam),
            _ => throw new NotSupportedException($"NES mapper {mapperNumber} is not implemented yet.")
        };

        return new Cartridge(
            mapper,
            programRam,
            info,
            System.Security.Cryptography.SHA256.HashData(image),
            batterySavePath);
    }

    internal byte CpuRead(ushort address) => _mapper.CpuRead(address);

    internal void CpuWrite(ushort address, byte value) => _mapper.CpuWrite(address, value);

    internal byte PpuRead(ushort address) => _mapper.PpuRead(address);

    internal void PpuWrite(ushort address, byte value) => _mapper.PpuWrite(address, value);

    internal bool IrqPending => _mapper.IrqPending;

    internal void ClockScanline() => _mapper.ClockScanline();

    internal void ClockPpuAddress(ushort address) => _mapper.ClockPpuAddress(address);

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(MapperNumber);
        writer.Write(_romFingerprint.Length);
        writer.Write(_romFingerprint);
        _programRam.SaveState(writer);
        _mapper.SaveState(writer);
    }

    internal void LoadState(BinaryReader reader)
    {
        if (reader.ReadInt32() != MapperNumber)
        {
            throw new InvalidDataException("The save state was created for a different cartridge mapper.");
        }

        var fingerprintLength = reader.ReadInt32();
        if (fingerprintLength != _romFingerprint.Length)
        {
            throw new InvalidDataException("The save state contains an invalid game fingerprint.");
        }

        var fingerprint = reader.ReadBytes(fingerprintLength);
        if (fingerprint.Length != fingerprintLength || !_romFingerprint.AsSpan().SequenceEqual(fingerprint))
        {
            throw new InvalidDataException("The save state belongs to a different game image.");
        }

        _programRam.LoadState(reader);
        _mapper.LoadState(reader);
    }

    public void FlushBatterySave()
    {
        if (_batterySavePath is null || !_programRam.IsDirty)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_batterySavePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _batterySavePath + ".tmp";
        WriteDurableBytes(temporaryPath, _programRam.Data);
        File.Move(temporaryPath, _batterySavePath, overwrite: true);
        _programRam.MarkClean();
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
        if (finalData?.Length == _programRam.Data.Length)
        {
            finalData.CopyTo(_programRam.Data, 0);
            _programRam.MarkClean();
            return;
        }

        var temporaryExists = File.Exists(temporaryPath);
        var temporaryData = temporaryExists ? File.ReadAllBytes(temporaryPath) : null;
        if (temporaryData?.Length == _programRam.Data.Length)
        {
            File.Move(temporaryPath, _batterySavePath, overwrite: true);
            temporaryData.CopyTo(_programRam.Data, 0);
            _programRam.MarkClean();
            return;
        }

        if (finalExists || temporaryExists)
        {
            var invalidLength = finalData?.Length ?? temporaryData?.Length ?? 0;
            throw new InvalidDataException(
                $"The battery save is {invalidLength} bytes, but this cartridge expects {_programRam.Data.Length} bytes.");
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

    private static CartridgeInfo InspectHeader(ReadOnlySpan<byte> header, long fileLength)
    {
        var flags6 = header[6];
        var flags7 = header[7];
        var isNes20 = (flags7 & 0x0C) == 0x08;
        var mapperNumber = GetMapperNumber(header);
        var submapperNumber = GetSubmapperNumber(header);
        var hasTrainer = (flags6 & 0x04) != 0;
        var hasBattery = (flags6 & 0x02) != 0;
        var prgLength = GetRomSize(header[4], (byte)(header[9] & 0x0F), isNes20, 16_384);
        var chrLength = GetRomSize(header[5], (byte)(header[9] >> 4), isNes20, 8_192);
        var minimumLength = 16L + (hasTrainer ? 512 : 0) + prgLength + chrLength;
        if (prgLength == 0 || fileLength < minimumLength)
        {
            throw new InvalidDataException("The iNES image is truncated or has invalid PRG/CHR sizes.");
        }

        int prgRamSize;
        int prgNvRamSize;
        int chrRamSize;
        int chrNvRamSize;
        NesTimingMode timingMode;
        byte defaultInputDevice;

        if (isNes20)
        {
            prgRamSize = GetRamSize((byte)(header[10] & 0x0F));
            prgNvRamSize = GetRamSize((byte)(header[10] >> 4));
            chrRamSize = GetRamSize((byte)(header[11] & 0x0F));
            chrNvRamSize = GetRamSize((byte)(header[11] >> 4));
            timingMode = (header[12] & 0x03) switch
            {
                1 => NesTimingMode.Pal,
                2 => NesTimingMode.MultipleRegion,
                3 => NesTimingMode.Dendy,
                _ => NesTimingMode.Ntsc
            };
            defaultInputDevice = header[15];
            hasBattery |= prgNvRamSize > 0 || chrNvRamSize > 0;
        }
        else
        {
            var prgRamUnits = header[8] == 0 ? 1 : header[8];
            // Legacy iNES defines zero as one 8 KiB unit. Many homebrew and
            // hardware-test NROM images rely on this work area even though the
            // original cartridge board is not described precisely by the header.
            var inferredPrgRamSize = checked(prgRamUnits * 8_192);
            prgRamSize = hasBattery ? 0 : inferredPrgRamSize;
            prgNvRamSize = hasBattery ? inferredPrgRamSize : 0;
            chrRamSize = chrLength == 0 ? 8_192 : 0;
            chrNvRamSize = 0;
            timingMode = (header[9] & 1) != 0 ? NesTimingMode.Pal : NesTimingMode.Ntsc;
            defaultInputDevice = 0;
        }

        if (chrLength == 0 && chrRamSize + chrNvRamSize == 0)
        {
            chrRamSize = 8_192;
        }

        var consoleType = flags7 & 0x03;
        var mapperSupported = IsMapperSupported(mapperNumber, submapperNumber);
        var timingSupported = timingMode is NesTimingMode.Ntsc or NesTimingMode.MultipleRegion;
        var inputSupported = defaultInputDevice is 0x00 or 0x01 or 0x2A;
        var limitedCompatibility = defaultInputDevice == 0x2A;
        var compatibilityWarning = consoleType switch
        {
            1 => "Nintendo VS. System cartridges are not supported by the current NES core.",
            2 => "PlayChoice-10 cartridges are not supported by the current NES core.",
            3 => "Extended-console NES cartridges are not supported by the current NES core.",
            _ when !timingSupported => $"{timingMode} CPU/PPU timing is not implemented; the current NES core is NTSC-only.",
            _ when !inputSupported =>
                $"This cartridge requires default input device ${defaultInputDevice:X2}, which is not implemented.",
            _ when limitedCompatibility =>
                "This multicart may include Zapper games; standard-controller games are playable, but light-gun games are not.",
            _ => null
        };

        return new CartridgeInfo(
            mapperNumber,
            submapperNumber,
            mapperSupported && consoleType == 0 && timingSupported && inputSupported,
            isNes20,
            hasBattery,
            hasTrainer,
            prgRamSize,
            prgNvRamSize,
            chrRamSize,
            chrNvRamSize,
            timingMode,
            defaultInputDevice,
            consoleType,
            limitedCompatibility,
            compatibilityWarning);
    }

    private static int GetRomSize(byte leastSignificant, byte mostSignificant, bool isNes20, int unitSize)
    {
        if (!isNes20)
        {
            return checked(leastSignificant * unitSize);
        }

        if (mostSignificant != 0x0F)
        {
            return checked((leastSignificant | (mostSignificant << 8)) * unitSize);
        }

        var exponent = leastSignificant >> 2;
        if (exponent > 30)
        {
            throw new InvalidDataException("The NES 2.0 ROM size is too large for this emulator.");
        }

        var multiplier = ((leastSignificant & 0x03) * 2) + 1;
        return checked((1 << exponent) * multiplier);
    }

    private static int GetRamSize(byte shift) => shift == 0 ? 0 : checked(64 << shift);

    private static int GetMapperNumber(ReadOnlySpan<byte> header)
    {
        var mapperNumber = (header[6] >> 4) | (header[7] & 0xF0);
        if ((header[7] & 0x0C) == 0x08)
        {
            mapperNumber |= (header[8] & 0x0F) << 8;
        }

        return mapperNumber;
    }

    private static int GetSubmapperNumber(ReadOnlySpan<byte> header) =>
        (header[7] & 0x0C) == 0x08 ? header[8] >> 4 : 0;

    private static Mmc3IrqRevision ResolveMmc3IrqRevision(
        CartridgeInfo info,
        Mmc3IrqRevision requestedRevision)
    {
        if (!Enum.IsDefined(requestedRevision))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedRevision),
                requestedRevision,
                "Unknown MMC3 IRQ revision.");
        }

        if (requestedRevision != Mmc3IrqRevision.Auto)
        {
            return requestedRevision;
        }

        return info.IsNes20 && info.SubmapperNumber == 4
            ? Mmc3IrqRevision.Nec
            : Mmc3IrqRevision.Sharp;
    }
}

public sealed record CartridgeInfo(
    int MapperNumber,
    int SubmapperNumber,
    bool IsSupported,
    bool IsNes20,
    bool HasBatteryBackedRam,
    bool HasTrainer,
    int PrgRamSize,
    int PrgNvRamSize,
    int ChrRamSize,
    int ChrNvRamSize,
    NesTimingMode TimingMode,
    byte DefaultInputDevice,
    int ConsoleType,
    bool IsLimitedCompatibility,
    string? CompatibilityWarning);

internal sealed class CartridgeRam(int size, bool isPersistent)
{
    public byte[] Data { get; } = new byte[size];

    public bool IsDirty { get; private set; }

    public byte Read(int address) => Data.Length == 0 ? (byte)0 : Data[address % Data.Length];

    public void Write(int address, byte value)
    {
        if (Data.Length == 0)
        {
            return;
        }

        var index = address % Data.Length;
        if (Data[index] == value)
        {
            return;
        }

        Data[index] = value;
        IsDirty = isPersistent;
    }

    public void MarkClean() => IsDirty = false;

    public void SaveState(BinaryWriter writer) => Mapper0.WriteArray(writer, Data);

    public void LoadState(BinaryReader reader)
    {
        Mapper0.ReadArray(reader, Data);
        IsDirty = isPersistent;
    }
}

internal interface IMapper
{
    NametableMirroring Mirroring { get; }

    byte CpuRead(ushort address);

    void CpuWrite(ushort address, byte value);

    byte PpuRead(ushort address);

    void PpuWrite(ushort address, byte value);

    void SaveState(BinaryWriter writer);

    void LoadState(BinaryReader reader);

    bool IrqPending => false;

    void ClockScanline()
    {
    }

    void ClockPpuAddress(ushort address)
    {
    }
}

internal sealed class Mapper0(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring mirroring,
    CartridgeRam programRam) : IMapper
{
    public NametableMirroring Mirroring => mirroring;

    public byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            return programRam.Read(address - 0x6000);
        }

        if (address < 0x8000)
        {
            return 0;
        }

        var offset = address - 0x8000;
        if (prgRom.Length == 16_384)
        {
            offset %= 16_384;
        }

        return prgRom[offset % prgRom.Length];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            programRam.Write(address - 0x6000, value);
        }
    }

    public byte PpuRead(ushort address) => chr[address % chr.Length];

    public void PpuWrite(ushort address, byte value)
    {
        if (chrIsRam)
        {
            chr[address % chr.Length] = value;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(chrIsRam);
        if (chrIsRam) WriteArray(writer, chr);
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam) ReadArray(reader, chr);
    }

    internal static void WriteArray(BinaryWriter writer, byte[] values)
    {
        writer.Write(values.Length);
        writer.Write(values);
    }

    internal static void ReadArray(BinaryReader reader, byte[] destination)
    {
        if (reader.ReadInt32() != destination.Length)
        {
            throw new InvalidDataException("The save state contains an incompatible cartridge memory size.");
        }

        reader.BaseStream.ReadExactly(destination);
    }
}

internal sealed class Mapper1(byte[] prgRom, byte[] chr, bool chrIsRam, CartridgeRam programRam) : IMapper
{
    private byte _shiftRegister = 0x10;
    private byte _control = 0x0C;
    private byte _chrBank0;
    private byte _chrBank1;
    private byte _prgBank;

    public NametableMirroring Mirroring => (_control & 0x03) switch
    {
        0 => NametableMirroring.OneScreenLower,
        1 => NametableMirroring.OneScreenUpper,
        2 => NametableMirroring.Vertical,
        _ => NametableMirroring.Horizontal
    };

    public byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            return (_prgBank & 0x10) == 0 ? programRam.Read(address - 0x6000) : (byte)0;
        }

        if (address < 0x8000)
        {
            return 0;
        }

        var bankCount = Math.Max(1, prgRom.Length / 16_384);
        var mode = (_control >> 2) & 0x03;
        int bank;
        int bankOffset;

        if (mode <= 1)
        {
            bank = (_prgBank & 0x0E) % bankCount;
            bankOffset = address - 0x8000;
            return prgRom[((bank * 16_384) + bankOffset) % prgRom.Length];
        }

        if (address < 0xC000)
        {
            bank = mode == 2 ? 0 : (_prgBank & 0x0F) % bankCount;
            bankOffset = address - 0x8000;
        }
        else
        {
            bank = mode == 2 ? (_prgBank & 0x0F) % bankCount : bankCount - 1;
            bankOffset = address - 0xC000;
        }

        return prgRom[(bank * 16_384) + bankOffset];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            if ((_prgBank & 0x10) == 0)
            {
                programRam.Write(address - 0x6000, value);
            }

            return;
        }

        if (address < 0x8000)
        {
            return;
        }

        if ((value & 0x80) != 0)
        {
            _shiftRegister = 0x10;
            _control |= 0x0C;
            return;
        }

        var commit = (_shiftRegister & 1) != 0;
        _shiftRegister >>= 1;
        _shiftRegister |= (byte)((value & 1) << 4);

        if (!commit)
        {
            return;
        }

        if (address < 0xA000) _control = _shiftRegister;
        else if (address < 0xC000) _chrBank0 = _shiftRegister;
        else if (address < 0xE000) _chrBank1 = _shiftRegister;
        else _prgBank = _shiftRegister;

        _shiftRegister = 0x10;
    }

    public byte PpuRead(ushort address) => chr[MapChrAddress(address)];

    public void PpuWrite(ushort address, byte value)
    {
        if (chrIsRam)
        {
            chr[MapChrAddress(address)] = value;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(chrIsRam);
        if (chrIsRam) Mapper0.WriteArray(writer, chr);
        writer.Write(_shiftRegister);
        writer.Write(_control);
        writer.Write(_chrBank0);
        writer.Write(_chrBank1);
        writer.Write(_prgBank);
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam) Mapper0.ReadArray(reader, chr);
        _shiftRegister = reader.ReadByte();
        _control = reader.ReadByte();
        _chrBank0 = reader.ReadByte();
        _chrBank1 = reader.ReadByte();
        _prgBank = reader.ReadByte();
    }

    private int MapChrAddress(ushort address)
    {
        var fourKilobyteBanks = Math.Max(1, chr.Length / 4_096);
        if ((_control & 0x10) == 0)
        {
            var bank = (_chrBank0 & 0x1E) % fourKilobyteBanks;
            return ((bank * 4_096) + address) % chr.Length;
        }

        var selectedBank = address < 0x1000 ? _chrBank0 : _chrBank1;
        var offset = address & 0x0FFF;
        return (((selectedBank % fourKilobyteBanks) * 4_096) + offset) % chr.Length;
    }
}

internal sealed class Mapper2(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring mirroring,
    bool hasBusConflicts,
    CartridgeRam programRam) : IMapper
{
    private int _prgBank;

    public NametableMirroring Mirroring => mirroring;

    public byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            return programRam.Read(address - 0x6000);
        }

        if (address < 0x8000)
        {
            return 0;
        }

        var bankCount = Math.Max(1, prgRom.Length / 16_384);
        var bank = address < 0xC000 ? _prgBank % bankCount : bankCount - 1;
        var offset = address & 0x3FFF;
        return prgRom[(bank * 16_384) + offset];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            programRam.Write(address - 0x6000, value);
            return;
        }

        if (address >= 0x8000)
        {
            if (hasBusConflicts)
            {
                value &= CpuRead(address);
            }

            _prgBank = value;
        }
    }

    public byte PpuRead(ushort address) => chr[address % chr.Length];

    public void PpuWrite(ushort address, byte value)
    {
        if (chrIsRam)
        {
            chr[address % chr.Length] = value;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(chrIsRam);
        if (chrIsRam) Mapper0.WriteArray(writer, chr);
        writer.Write(_prgBank);
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam) Mapper0.ReadArray(reader, chr);
        _prgBank = reader.ReadInt32();
    }
}

internal sealed class Mapper3(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring mirroring,
    bool hasBusConflicts,
    CartridgeRam programRam) : IMapper
{
    private int _chrBank;

    public NametableMirroring Mirroring => mirroring;

    public byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            return programRam.Read(address - 0x6000);
        }

        if (address < 0x8000)
        {
            return 0;
        }

        return prgRom[(address - 0x8000) % prgRom.Length];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            programRam.Write(address - 0x6000, value);
            return;
        }

        if (address < 0x8000)
        {
            return;
        }

        if (hasBusConflicts)
        {
            value &= CpuRead(address);
        }

        _chrBank = value;
    }

    public byte PpuRead(ushort address) => chr[MapChrAddress(address)];

    public void PpuWrite(ushort address, byte value)
    {
        if (chrIsRam)
        {
            chr[MapChrAddress(address)] = value;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(chrIsRam);
        if (chrIsRam) Mapper0.WriteArray(writer, chr);
        writer.Write(_chrBank);
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam) Mapper0.ReadArray(reader, chr);
        _chrBank = reader.ReadInt32();
    }

    private int MapChrAddress(ushort address)
    {
        var bankCount = Math.Max(1, chr.Length / 8_192);
        var bank = _chrBank % bankCount;
        return (bank * 8_192) + (address & 0x1FFF);
    }
}

internal sealed class Mapper4(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring initialMirroring,
    CartridgeRam programRam,
    Mmc3IrqRevision irqRevision) : IMapper
{
    private readonly byte[] _registers = new byte[8];
    private byte _bankSelect;
    private readonly bool _fourScreen = initialMirroring == NametableMirroring.FourScreen;
    private NametableMirroring _mirroring = initialMirroring;
    private bool _prgRamEnabled = true;
    private bool _prgRamWriteProtected;
    private byte _irqLatch;
    private byte _irqCounter;
    private bool _irqReload;
    private bool _irqEnabled;
    private bool _irqPending;
    private bool _lastA12;
    private byte _a12LowCycles;

    public NametableMirroring Mirroring => _fourScreen
        ? NametableMirroring.FourScreen
        : _mirroring;

    public bool IrqPending => _irqPending;

    public byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            return _prgRamEnabled ? programRam.Read(address - 0x6000) : (byte)0;
        }

        if (address < 0x8000)
        {
            return 0;
        }

        var bankCount = Math.Max(1, prgRom.Length / 8_192);
        var slot = (address - 0x8000) / 8_192;
        var prgMode = (_bankSelect & 0x40) != 0;
        var bank = slot switch
        {
            0 => prgMode ? bankCount - 2 : _registers[6] & 0x3F,
            1 => _registers[7] & 0x3F,
            2 => prgMode ? _registers[6] & 0x3F : bankCount - 2,
            _ => bankCount - 1
        };

        bank = ((bank % bankCount) + bankCount) % bankCount;
        return prgRom[(bank * 8_192) + (address & 0x1FFF)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            if (_prgRamEnabled && !_prgRamWriteProtected)
            {
                programRam.Write(address - 0x6000, value);
            }

            return;
        }

        if (address < 0x8000)
        {
            return;
        }

        var odd = (address & 1) != 0;
        if (address < 0xA000)
        {
            if (odd) _registers[_bankSelect & 0x07] = value;
            else _bankSelect = value;
        }
        else if (address < 0xC000)
        {
            if (odd)
            {
                _prgRamEnabled = (value & 0x80) != 0;
                _prgRamWriteProtected = (value & 0x40) != 0;
            }
            else if (!_fourScreen)
            {
                _mirroring = (value & 1) == 0 ? NametableMirroring.Vertical : NametableMirroring.Horizontal;
            }
        }
        else if (address < 0xE000)
        {
            if (odd)
            {
                _irqCounter = 0;
                _irqReload = true;
            }
            else
            {
                _irqLatch = value;
            }
        }
        else if (odd)
        {
            _irqEnabled = true;
        }
        else
        {
            _irqEnabled = false;
            _irqPending = false;
        }
    }

    public byte PpuRead(ushort address) => chr[MapChrAddress(address)];

    public void PpuWrite(ushort address, byte value)
    {
        if (chrIsRam)
        {
            chr[MapChrAddress(address)] = value;
        }
    }

    public void ClockScanline()
    {
        ClockIrqCounter();
    }

    public void ClockPpuAddress(ushort address)
    {
        var a12 = (address & 0x1000) != 0;
        if (!a12)
        {
            if (_a12LowCycles < byte.MaxValue)
            {
                _a12LowCycles++;
            }
        }
        else
        {
            if (!_lastA12 && _a12LowCycles >= 8)
            {
                ClockIrqCounter();
            }

            _a12LowCycles = 0;
        }

        _lastA12 = a12;
    }

    private void ClockIrqCounter()
    {
        var previousCounter = _irqCounter;
        var reloadRequested = _irqReload;
        if (_irqCounter == 0 || _irqReload)
        {
            _irqCounter = _irqLatch;
            _irqReload = false;
        }
        else
        {
            _irqCounter--;
        }

        var triggerIrq = irqRevision switch
        {
            Mmc3IrqRevision.Sharp => _irqCounter == 0,
            Mmc3IrqRevision.Nec => (previousCounter > 0 || reloadRequested) && _irqCounter == 0,
            _ => throw new InvalidOperationException("MMC3 must be created with a resolved IRQ revision.")
        };

        if (triggerIrq && _irqEnabled)
        {
            _irqPending = true;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        Mapper0.WriteArray(writer, _registers);
        writer.Write(chrIsRam);
        if (chrIsRam) Mapper0.WriteArray(writer, chr);
        writer.Write(_bankSelect);
        writer.Write((int)_mirroring);
        writer.Write(_prgRamEnabled);
        writer.Write(_prgRamWriteProtected);
        writer.Write(_irqLatch);
        writer.Write(_irqCounter);
        writer.Write(_irqReload);
        writer.Write(_irqEnabled);
        writer.Write(_irqPending);
        writer.Write(_lastA12);
        writer.Write(_a12LowCycles);
        writer.Write((int)irqRevision);
    }

    public void LoadState(BinaryReader reader)
    {
        Mapper0.ReadArray(reader, _registers);
        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam) Mapper0.ReadArray(reader, chr);
        _bankSelect = reader.ReadByte();
        _mirroring = (NametableMirroring)reader.ReadInt32();
        _prgRamEnabled = reader.ReadBoolean();
        _prgRamWriteProtected = reader.ReadBoolean();
        _irqLatch = reader.ReadByte();
        _irqCounter = reader.ReadByte();
        _irqReload = reader.ReadBoolean();
        _irqEnabled = reader.ReadBoolean();
        _irqPending = reader.ReadBoolean();
        _lastA12 = reader.ReadBoolean();
        _a12LowCycles = reader.ReadByte();
        if ((Mmc3IrqRevision)reader.ReadInt32() != irqRevision)
        {
            throw new InvalidDataException("The save state uses a different MMC3 IRQ revision.");
        }
    }

    private int MapChrAddress(ushort address)
    {
        var slot = address / 1_024;
        var inverted = (_bankSelect & 0x80) != 0;
        int bank;

        if (!inverted)
        {
            bank = slot switch
            {
                0 => _registers[0] & 0xFE,
                1 => (_registers[0] & 0xFE) + 1,
                2 => _registers[1] & 0xFE,
                3 => (_registers[1] & 0xFE) + 1,
                _ => _registers[slot - 2]
            };
        }
        else
        {
            bank = slot switch
            {
                0 or 1 or 2 or 3 => _registers[slot + 2],
                4 => _registers[0] & 0xFE,
                5 => (_registers[0] & 0xFE) + 1,
                6 => _registers[1] & 0xFE,
                _ => (_registers[1] & 0xFE) + 1
            };
        }

        var bankCount = Math.Max(1, chr.Length / 1_024);
        return ((bank % bankCount) * 1_024) + (address & 0x03FF);
    }
}

internal sealed class Mapper7(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    bool hasBusConflicts,
    CartridgeRam programRam) : IMapper
{
    private int _prgBank;
    private bool _oneScreenUpper;

    public NametableMirroring Mirroring => _oneScreenUpper
        ? NametableMirroring.OneScreenUpper
        : NametableMirroring.OneScreenLower;

    public byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            return programRam.Read(address - 0x6000);
        }

        if (address < 0x8000)
        {
            return 0;
        }

        var bankCount = Math.Max(1, prgRom.Length / 32_768);
        var bank = _prgBank % bankCount;
        return prgRom[(bank * 32_768) + (address - 0x8000)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            programRam.Write(address - 0x6000, value);
            return;
        }

        if (address < 0x8000)
        {
            return;
        }

        if (hasBusConflicts)
        {
            value &= CpuRead(address);
        }

        _prgBank = value & 0x07;
        _oneScreenUpper = (value & 0x10) != 0;
    }

    public byte PpuRead(ushort address) => chr[address % chr.Length];

    public void PpuWrite(ushort address, byte value)
    {
        if (chrIsRam)
        {
            chr[address % chr.Length] = value;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(chrIsRam);
        if (chrIsRam) Mapper0.WriteArray(writer, chr);
        writer.Write(_prgBank);
        writer.Write(_oneScreenUpper);
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam) Mapper0.ReadArray(reader, chr);
        _prgBank = reader.ReadInt32();
        _oneScreenUpper = reader.ReadBoolean();
    }
}

internal sealed class Mapper66(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring mirroring,
    CartridgeRam programRam) : IMapper
{
    private int _prgBank;
    private int _chrBank;

    public NametableMirroring Mirroring => mirroring;

    public byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            return programRam.Read(address - 0x6000);
        }

        if (address < 0x8000)
        {
            return 0;
        }

        var bankCount = Math.Max(1, prgRom.Length / 32_768);
        var bank = _prgBank % bankCount;
        return prgRom[(bank * 32_768) + (address - 0x8000)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            programRam.Write(address - 0x6000, value);
            return;
        }

        if (address < 0x8000)
        {
            return;
        }

        // GxROM boards have bus conflicts: the ROM value and CPU value are ANDed.
        value &= CpuRead(address);
        _chrBank = value & 0x03;
        _prgBank = (value >> 4) & 0x03;
    }

    public byte PpuRead(ushort address)
    {
        var bankCount = Math.Max(1, chr.Length / 8_192);
        var bank = _chrBank % bankCount;
        return chr[(bank * 8_192) + (address % 8_192)];
    }

    public void PpuWrite(ushort address, byte value)
    {
        if (chrIsRam)
        {
            chr[address % chr.Length] = value;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(chrIsRam);
        if (chrIsRam) Mapper0.WriteArray(writer, chr);
        writer.Write(_prgBank);
        writer.Write(_chrBank);
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam) Mapper0.ReadArray(reader, chr);
        _prgBank = reader.ReadInt32();
        _chrBank = reader.ReadInt32();
    }
}
