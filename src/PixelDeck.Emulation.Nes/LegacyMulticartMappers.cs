namespace PixelDeck.Emulation.Nes;

/// <summary>
/// FFE/Super Magic Card mode 4 (legacy iNES mapper 8, NES 2.0 mapper 6
/// submapper 4). The single latch selects 32 KiB PRG and 8 KiB CHR banks.
/// </summary>
internal sealed class Mapper8(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring mirroring,
    CartridgeRam programRam) : IMapper
{
    private byte _prgBank;
    private byte _chrBank;

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

        if (address >= 0x8000)
        {
            _prgBank = (byte)((value >> 4) & 0x03);
            _chrBank = (byte)(value & 0x03);
        }
    }

    public byte PpuRead(ushort address)
    {
        var bankCount = Math.Max(1, chr.Length / 8_192);
        var bank = _chrBank % bankCount;
        return chr[(bank * 8_192) + (address & 0x1FFF)];
    }

    public void PpuWrite(ushort address, byte value)
    {
        // Super Magic Card mode 4 write-protects CHR memory. Keep the
        // iNES CHR-RAM allocation in the state for format consistency, but
        // do not let the emulated console mutate it in this mode.
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(chrIsRam);
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        writer.Write(_prgBank);
        writer.Write(_chrBank);
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam)
        {
            Mapper0.ReadArray(reader, chr);
        }

        _prgBank = reader.ReadByte();
        _chrBank = reader.ReadByte();
    }
}

/// <summary>
/// K-1029/K-1030P multicart board (iNES mapper 15). Address bits select one
/// of four PRG banking modes while the data latch supplies the bank, mirroring,
/// and mode-2 half-bank bit.
/// </summary>
internal sealed class Mapper15(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring initialMirroring,
    CartridgeRam programRam) : IMapper
{
    private readonly bool _fourScreen = initialMirroring == NametableMirroring.FourScreen;
    private readonly bool _mapperHackCompatibility = prgRom.Length < 1_048_576;
    private byte _register;
    private byte _mode;
    private NametableMirroring _mirroring = initialMirroring == NametableMirroring.FourScreen
        ? NametableMirroring.FourScreen
        : NametableMirroring.Vertical;

    public NametableMirroring Mirroring => _fourScreen
        ? NametableMirroring.FourScreen
        : _mirroring;

    public byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            // Real K-1029 boards do not decode PRG RAM, but most mapper-15
            // images are mapper hacks and expect an ordinary 8 KiB window.
            return programRam.Read(address - 0x6000);
        }

        if (address < 0x8000)
        {
            return 0;
        }

        return prgRom[MapPrgAddress(address)];
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

        _mode = (byte)(address & 0x03);
        _register = value;
        if (!_fourScreen)
        {
            _mirroring = (value & 0x40) == 0
                ? NametableMirroring.Vertical
                : NametableMirroring.Horizontal;
        }
    }

    public byte PpuRead(ushort address) => chr[address % chr.Length];

    public void PpuWrite(ushort address, byte value)
    {
        if (!chrIsRam)
        {
            return;
        }

        // Modes 0 and 3 write-protect CHR RAM on the two physical multicarts.
        // Smaller ROMs labeled mapper 15 are mapper hacks; established mapper
        // behavior leaves their CHR RAM writable in every mode.
        if (_mapperHackCompatibility || _mode is 1 or 2)
        {
            chr[address % chr.Length] = value;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(chrIsRam);
        writer.Write(_mapperHackCompatibility);
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        writer.Write(_register);
        writer.Write(_mode);
        writer.Write((int)_mirroring);
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader.ReadBoolean() != chrIsRam ||
            reader.ReadBoolean() != _mapperHackCompatibility)
        {
            throw new InvalidDataException("The save state's mapper 15 board does not match this cartridge.");
        }

        if (chrIsRam)
        {
            Mapper0.ReadArray(reader, chr);
        }

        _register = reader.ReadByte();
        _mode = reader.ReadByte();
        _mirroring = (NametableMirroring)reader.ReadInt32();
    }

    private int MapPrgAddress(ushort address)
    {
        var selected16KBank = _register & 0x3F;
        return _mode switch
        {
            // NROM-256: the CPU supplies A14; register bit 0 is ignored.
            0 => Map16KBank(
                (selected16KBank & 0x3E) | (address < 0xC000 ? 0 : 1),
                address),

            // UNROM: switch the lower 16 KiB and fix the upper half to bank
            // 7 within the selected 128 KiB outer region.
            1 => Map16KBank(
                address < 0xC000
                    ? selected16KBank
                    : (selected16KBank & 0x38) | 0x07,
                address),

            // NROM-64: register bit 7 supplies A13 and the same 8 KiB bank is
            // repeated through all four CPU windows.
            2 => Map8KBank((selected16KBank << 1) | (_register >> 7), address),

            // NROM-128: repeat the selected 16 KiB bank in both CPU halves.
            _ => Map16KBank(selected16KBank, address)
        };
    }

    private int Map16KBank(int bank, ushort address)
    {
        var bankCount = Math.Max(1, prgRom.Length / 16_384);
        return ((bank % bankCount) * 16_384) + (address & 0x3FFF);
    }

    private int Map8KBank(int bank, ushort address)
    {
        var bankCount = Math.Max(1, prgRom.Length / 8_192);
        return ((bank % bankCount) * 8_192) + (address & 0x1FFF);
    }
}

/// <summary>
/// Generic GNROM-like board whose latch is decoded in expansion space
/// (iNES mapper 240).
/// </summary>
internal sealed class Mapper240(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring mirroring,
    CartridgeRam programRam) : IMapper
{
    private byte _prgBank;
    private byte _chrBank;

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
        return prgRom[((_prgBank % bankCount) * 32_768) + (address - 0x8000)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x4020 and < 0x6000)
        {
            _prgBank = (byte)(value >> 4);
            _chrBank = (byte)(value & 0x0F);
        }
        else if (address is >= 0x6000 and < 0x8000)
        {
            programRam.Write(address - 0x6000, value);
        }
    }

    public byte PpuRead(ushort address)
    {
        var bankCount = Math.Max(1, chr.Length / 8_192);
        return chr[((_chrBank % bankCount) * 8_192) + (address & 0x1FFF)];
    }

    public void PpuWrite(ushort address, byte value)
    {
        if (chrIsRam)
        {
            var bankCount = Math.Max(1, chr.Length / 8_192);
            chr[((_chrBank % bankCount) * 8_192) + (address & 0x1FFF)] = value;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(chrIsRam);
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        writer.Write(_prgBank);
        writer.Write(_chrBank);
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam)
        {
            Mapper0.ReadArray(reader, chr);
        }

        _prgBank = reader.ReadByte();
        _chrBank = reader.ReadByte();
    }
}
