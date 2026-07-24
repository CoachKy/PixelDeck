namespace PixelDeck.Emulation.Nes;

internal sealed class Mapper13(
    byte[] prgRom,
    byte[] chrRam,
    NametableMirroring mirroring) : IMapper
{
    private byte _chrBank;

    public NametableMirroring Mirroring => mirroring;

    public byte CpuRead(ushort address) =>
        address >= 0x8000 ? prgRom[(address - 0x8000) % prgRom.Length] : (byte)0;

    public void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x8000)
        {
            _chrBank = (byte)((value & CpuRead(address)) & 0x03);
        }
    }

    public byte PpuRead(ushort address) => chrRam[MapChrAddress(address)];

    public void PpuWrite(ushort address, byte value) => chrRam[MapChrAddress(address)] = value;

    public void SaveState(BinaryWriter writer)
    {
        Mapper0.WriteArray(writer, chrRam);
        writer.Write(_chrBank);
    }

    public void LoadState(BinaryReader reader)
    {
        Mapper0.ReadArray(reader, chrRam);
        _chrBank = reader.ReadByte();
    }

    private int MapChrAddress(ushort address)
    {
        if (address < 0x1000)
        {
            return address;
        }

        var bankCount = Math.Max(1, chrRam.Length / 4_096);
        return ((_chrBank % bankCount) * 4_096) + (address & 0x0FFF);
    }
}

internal sealed class Mapper41(
    byte[] prgRom,
    byte[] chr) : IMapper
{
    private byte _prgBank;
    private byte _chrBank;
    private bool _innerChrEnabled;
    private NametableMirroring _mirroring = NametableMirroring.Vertical;

    public NametableMirroring Mirroring => _mirroring;

    public byte CpuRead(ushort address)
    {
        if (address < 0x8000)
        {
            return 0;
        }

        var bankCount = Math.Max(1, prgRom.Length / 32_768);
        return prgRom[((_prgBank % bankCount) * 32_768) + (address - 0x8000)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and <= 0x67FF)
        {
            _prgBank = (byte)(address & 0x07);
            _chrBank = (byte)((address >> 1) & 0x0C);
            _innerChrEnabled = (address & 0x04) != 0;
            _mirroring = (address & 0x20) == 0
                ? NametableMirroring.Vertical
                : NametableMirroring.Horizontal;
        }
        else if (address >= 0x8000 && _innerChrEnabled)
        {
            _chrBank = (byte)((_chrBank & 0x0C) | ((value & CpuRead(address)) & 0x03));
        }
    }

    public byte PpuRead(ushort address)
    {
        var bankCount = Math.Max(1, chr.Length / 8_192);
        return chr[((_chrBank % bankCount) * 8_192) + (address & 0x1FFF)];
    }

    public void PpuWrite(ushort address, byte value)
    {
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(_prgBank);
        writer.Write(_chrBank);
        writer.Write(_innerChrEnabled);
        writer.Write((int)_mirroring);
    }

    public void LoadState(BinaryReader reader)
    {
        _prgBank = reader.ReadByte();
        _chrBank = reader.ReadByte();
        _innerChrEnabled = reader.ReadBoolean();
        _mirroring = (NametableMirroring)reader.ReadInt32();
    }
}

internal sealed class Mapper113(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam) : IMapper
{
    private byte _prgBank;
    private byte _chrBank;
    private NametableMirroring _mirroring = NametableMirroring.Horizontal;

    public NametableMirroring Mirroring => _mirroring;

    public byte CpuRead(ushort address)
    {
        if (address < 0x8000)
        {
            return 0;
        }

        var bankCount = Math.Max(1, prgRom.Length / 32_768);
        return prgRom[((_prgBank % bankCount) * 32_768) + (address - 0x8000)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if ((address & 0xE100) != 0x4100)
        {
            return;
        }

        _prgBank = (byte)((value >> 3) & 0x07);
        _chrBank = (byte)((value & 0x07) | ((value >> 3) & 0x08));
        _mirroring = (value & 0x80) == 0
            ? NametableMirroring.Horizontal
            : NametableMirroring.Vertical;
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
        writer.Write((int)_mirroring);
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
        _mirroring = (NametableMirroring)reader.ReadInt32();
    }

    private int MapChrAddress(ushort address)
    {
        var bankCount = Math.Max(1, chr.Length / 8_192);
        return ((_chrBank % bankCount) * 8_192) + (address & 0x1FFF);
    }
}

internal sealed class Mapper228(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam) : IMapper
{
    private ushort _registerAddress = 0x8000;
    private byte _registerValue;

    public NametableMirroring Mirroring => (_registerAddress & 0x2000) == 0
        ? NametableMirroring.Vertical
        : NametableMirroring.Horizontal;

    public byte CpuRead(ushort address)
    {
        if (address < 0x8000)
        {
            return 0;
        }

        var selectedChip = (_registerAddress >> 11) & 0x03;
        if (selectedChip == 2 && prgRom.Length >= 1_572_864)
        {
            return 0;
        }

        var storedChip = selectedChip == 3 ? 2 : selectedChip;
        var page = (_registerAddress >> 6) & 0x1F;
        var sameBankInBothWindows = (_registerAddress & 0x20) != 0;
        var bank = sameBankInBothWindows
            ? page
            : (page & 0x1E) | (address < 0xC000 ? 0 : 1);
        var mapped = (storedChip * 524_288) + (bank * 16_384) + (address & 0x3FFF);
        return prgRom[mapped % prgRom.Length];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x8000)
        {
            _registerAddress = address;
            _registerValue = (byte)(value & 0x03);
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
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        writer.Write(_registerAddress);
        writer.Write(_registerValue);
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

        _registerAddress = reader.ReadUInt16();
        _registerValue = reader.ReadByte();
    }

    private int MapChrAddress(ushort address)
    {
        var bank = ((_registerAddress & 0x0F) << 2) | _registerValue;
        var bankCount = Math.Max(1, chr.Length / 8_192);
        return (((int)bank % bankCount) * 8_192) + (address & 0x1FFF);
    }
}
