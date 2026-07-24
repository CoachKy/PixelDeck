namespace PixelDeck.Emulation.Nes;

/// <summary>
/// Color Dreams/Wisdom Tree discrete mapper (iNES 11).
/// </summary>
internal sealed class Mapper11(
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

        // The original Color Dreams board has AND-type bus conflicts.
        value &= CpuRead(address);
        _prgBank = value & 0x03;
        _chrBank = value >> 4;
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

        _prgBank = reader.ReadInt32();
        _chrBank = reader.ReadInt32();
    }

    private int MapChrAddress(ushort address)
    {
        var bankCount = Math.Max(1, chr.Length / 8_192);
        var bank = _chrBank % bankCount;
        return (bank * 8_192) + (address & 0x1FFF);
    }
}

/// <summary>
/// BNROM or NINA-001/NINA-002 (iNES 34). NES 2.0 submappers select the
/// board explicitly; legacy images use their CHR capacity to disambiguate.
/// </summary>
internal sealed class Mapper34(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring mirroring,
    CartridgeRam programRam,
    int submapperNumber) : IMapper
{
    private readonly bool _usesNinaRegisters =
        submapperNumber == 1 || (submapperNumber == 0 && !chrIsRam && chr.Length > 8_192);
    private int _prgBank;
    private int _chrBank0;
    private int _chrBank1 = 1;

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
            if (_usesNinaRegisters)
            {
                switch (address)
                {
                    case 0x7FFD:
                        // NINA-001 only wires bit 0, but the documented oversize
                        // extension uses the additional bits for larger images.
                        _prgBank = value;
                        break;
                    case 0x7FFE:
                        _chrBank0 = value & 0x0F;
                        break;
                    case 0x7FFF:
                        _chrBank1 = value & 0x0F;
                        break;
                }
            }

            return;
        }

        if (address >= 0x8000 && (!_usesNinaRegisters || submapperNumber == 0))
        {
            // Legacy iNES mapper 34 could not distinguish BNROM from NINA.
            // Some mapper conversions therefore combine NINA CHR registers
            // with a BNROM-style PRG port. Explicit NES 2.0 boards stay strict.
            _prgBank = _usesNinaRegisters ? value : value & CpuRead(address);
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

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(chrIsRam);
        writer.Write(_usesNinaRegisters);
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        writer.Write(_prgBank);
        writer.Write(_chrBank0);
        writer.Write(_chrBank1);
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader.ReadBoolean() != chrIsRam ||
            reader.ReadBoolean() != _usesNinaRegisters)
        {
            throw new InvalidDataException("The save state's mapper 34 board does not match this cartridge.");
        }

        if (chrIsRam)
        {
            Mapper0.ReadArray(reader, chr);
        }

        _prgBank = reader.ReadInt32();
        _chrBank0 = reader.ReadInt32();
        _chrBank1 = reader.ReadInt32();
    }

    private int MapChrAddress(ushort address)
    {
        if (!_usesNinaRegisters)
        {
            return address % chr.Length;
        }

        var bankCount = Math.Max(1, chr.Length / 4_096);
        var bank = (address < 0x1000 ? _chrBank0 : _chrBank1) % bankCount;
        return (bank * 4_096) + (address & 0x0FFF);
    }
}

/// <summary>
/// Camerica/Codemasters BF909x family (iNES 71).
/// </summary>
internal sealed class Mapper71(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring fixedMirroring,
    CartridgeRam programRam) : IMapper
{
    private int _prgBank;
    private bool _mapperMirroringEnabled;
    private bool _oneScreenUpper;

    public NametableMirroring Mirroring => _mapperMirroringEnabled
        ? _oneScreenUpper
            ? NametableMirroring.OneScreenUpper
            : NametableMirroring.OneScreenLower
        : fixedMirroring;

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
        return prgRom[(bank * 16_384) + (address & 0x3FFF)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            programRam.Write(address - 0x6000, value);
            return;
        }

        if (address is >= 0x9000 and < 0xA000)
        {
            // Only BF9097 (Fire Hawk) decodes this. Enabling it lazily keeps
            // fixed mirroring for the other legacy mapper-71 boards.
            _mapperMirroringEnabled = true;
            _oneScreenUpper = (value & 0x10) != 0;
        }

        if (address >= 0xC000)
        {
            _prgBank = value & 0x0F;
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
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        writer.Write(_prgBank);
        writer.Write(_mapperMirroringEnabled);
        writer.Write(_oneScreenUpper);
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

        _prgBank = reader.ReadInt32();
        _mapperMirroringEnabled = reader.ReadBoolean();
        _oneScreenUpper = reader.ReadBoolean();
    }
}

/// <summary>
/// AVE NINA-03/NINA-06 (iNES 79).
/// </summary>
internal sealed class Mapper79(
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

        if ((address & 0xE100) == 0x4100)
        {
            _prgBank = (value >> 3) & 0x01;
            _chrBank = value & 0x07;
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
        if (chrIsRam)
        {
            chr[address % chr.Length] = value;
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

        _prgBank = reader.ReadInt32();
        _chrBank = reader.ReadInt32();
    }
}

/// <summary>
/// Camerica BF9096 Quattro multicart mapper (iNES 232).
/// </summary>
internal sealed class Mapper232(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring mirroring,
    CartridgeRam programRam) : IMapper
{
    private int _outerBank = Math.Max(0, (prgRom.Length / 65_536) - 1);
    private int _innerBank;

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
        var innerBank = address < 0xC000 ? _innerBank : 3;
        var bank = ((_outerBank * 4) + innerBank) % bankCount;
        return prgRom[(bank * 16_384) + (address & 0x3FFF)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            programRam.Write(address - 0x6000, value);
        }
        else if (address is >= 0x8000 and < 0xC000)
        {
            _outerBank = (value >> 3) & 0x03;
        }
        else if (address >= 0xC000)
        {
            _innerBank = value & 0x03;
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
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        writer.Write(_outerBank);
        writer.Write(_innerBank);
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

        _outerBank = reader.ReadInt32();
        _innerBank = reader.ReadInt32();
    }
}
