namespace PixelDeck.Emulation.Nes;

internal sealed class Mapper32(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring initialMirroring) : IMapper
{
    private readonly byte[] _chrBanks = new byte[8];
    private byte _prgBank0;
    private byte _prgBank1;
    private bool _swapPrgBank;
    private readonly bool _fourScreen = initialMirroring == NametableMirroring.FourScreen;
    private NametableMirroring _mirroring = initialMirroring;

    public NametableMirroring Mirroring => _fourScreen
        ? NametableMirroring.FourScreen
        : _mirroring;

    public byte CpuRead(ushort address)
    {
        if (address < 0x8000)
        {
            return 0;
        }

        var bankCount = Math.Max(1, prgRom.Length / 8_192);
        var slot = (address - 0x8000) / 8_192;
        var bank = slot switch
        {
            0 => _swapPrgBank ? bankCount - 2 : _prgBank0,
            1 => _prgBank1,
            2 => _swapPrgBank ? _prgBank0 : bankCount - 2,
            _ => bankCount - 1
        };
        bank = ((bank % bankCount) + bankCount) % bankCount;
        return prgRom[(bank * 8_192) + (address & 0x1FFF)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        switch (address & 0xF000)
        {
            case 0x8000:
                _prgBank0 = (byte)(value & 0x1F);
                break;
            case 0x9000:
                _swapPrgBank = (value & 0x02) != 0;
                if (!_fourScreen)
                {
                    _mirroring = (value & 0x01) == 0
                        ? NametableMirroring.Vertical
                        : NametableMirroring.Horizontal;
                }

                break;
            case 0xA000:
                _prgBank1 = (byte)(value & 0x1F);
                break;
            case 0xB000:
                _chrBanks[address & 0x07] = value;
                break;
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
        Mapper0.WriteArray(writer, _chrBanks);
        writer.Write(chrIsRam);
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        writer.Write(_prgBank0);
        writer.Write(_prgBank1);
        writer.Write(_swapPrgBank);
        writer.Write((int)_mirroring);
    }

    public void LoadState(BinaryReader reader)
    {
        Mapper0.ReadArray(reader, _chrBanks);
        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam)
        {
            Mapper0.ReadArray(reader, chr);
        }

        _prgBank0 = reader.ReadByte();
        _prgBank1 = reader.ReadByte();
        _swapPrgBank = reader.ReadBoolean();
        _mirroring = (NametableMirroring)reader.ReadInt32();
    }

    private int MapChrAddress(ushort address)
    {
        var bankCount = Math.Max(1, chr.Length / 1_024);
        var bank = _chrBanks[address / 1_024] % bankCount;
        return (bank * 1_024) + (address & 0x03FF);
    }
}

internal sealed class Mapper33(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    CartridgeRam programRam) : IMapper
{
    private readonly byte[] _chrBanks = new byte[6];
    private byte _prgBank0;
    private byte _prgBank1;
    private NametableMirroring _mirroring = NametableMirroring.Vertical;

    public NametableMirroring Mirroring => _mirroring;

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

        var bankCount = Math.Max(1, prgRom.Length / 8_192);
        var slot = (address - 0x8000) / 8_192;
        var bank = slot switch
        {
            0 => _prgBank0,
            1 => _prgBank1,
            2 => bankCount - 2,
            _ => bankCount - 1
        };
        bank = ((bank % bankCount) + bankCount) % bankCount;
        return prgRom[(bank * 8_192) + (address & 0x1FFF)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            programRam.Write(address - 0x6000, value);
            return;
        }

        switch (address & 0xA003)
        {
            case 0x8000:
                _prgBank0 = (byte)(value & 0x3F);
                _mirroring = (value & 0x40) == 0
                    ? NametableMirroring.Vertical
                    : NametableMirroring.Horizontal;
                break;
            case 0x8001:
                _prgBank1 = (byte)(value & 0x3F);
                break;
            case 0x8002:
                _chrBanks[0] = value;
                break;
            case 0x8003:
                _chrBanks[1] = value;
                break;
            case 0xA000:
            case 0xA001:
            case 0xA002:
            case 0xA003:
                _chrBanks[2 + (address & 0x03)] = value;
                break;
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
        Mapper0.WriteArray(writer, _chrBanks);
        writer.Write(chrIsRam);
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        writer.Write(_prgBank0);
        writer.Write(_prgBank1);
        writer.Write((int)_mirroring);
    }

    public void LoadState(BinaryReader reader)
    {
        Mapper0.ReadArray(reader, _chrBanks);
        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam)
        {
            Mapper0.ReadArray(reader, chr);
        }

        _prgBank0 = reader.ReadByte();
        _prgBank1 = reader.ReadByte();
        _mirroring = (NametableMirroring)reader.ReadInt32();
    }

    private int MapChrAddress(ushort address)
    {
        int bank;
        int bankSize;
        int offset;
        if (address < 0x1000)
        {
            bank = _chrBanks[address < 0x0800 ? 0 : 1];
            bankSize = 2_048;
            offset = address & 0x07FF;
        }
        else
        {
            bank = _chrBanks[2 + ((address - 0x1000) / 1_024)];
            bankSize = 1_024;
            offset = address & 0x03FF;
        }

        var bankCount = Math.Max(1, chr.Length / bankSize);
        return ((bank % bankCount) * bankSize) + offset;
    }
}

internal sealed class Mapper75(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring initialMirroring) : IMapper
{
    private readonly byte[] _prgBanks = new byte[3];
    private readonly byte[] _chrBanks = new byte[2];
    private readonly bool _fourScreen = initialMirroring == NametableMirroring.FourScreen;
    private NametableMirroring _mirroring = initialMirroring;

    public NametableMirroring Mirroring => _fourScreen
        ? NametableMirroring.FourScreen
        : _mirroring;

    public byte CpuRead(ushort address)
    {
        if (address < 0x8000)
        {
            return 0;
        }

        var bankCount = Math.Max(1, prgRom.Length / 8_192);
        var slot = (address - 0x8000) / 8_192;
        var bank = slot < 3 ? _prgBanks[slot] : bankCount - 1;
        bank = ((bank % bankCount) + bankCount) % bankCount;
        return prgRom[(bank * 8_192) + (address & 0x1FFF)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        switch (address & 0xF000)
        {
            case 0x8000:
                _prgBanks[0] = (byte)(value & 0x0F);
                break;
            case 0x9000:
                _chrBanks[0] = (byte)((_chrBanks[0] & 0x0F) | ((value & 0x02) << 3));
                _chrBanks[1] = (byte)((_chrBanks[1] & 0x0F) | ((value & 0x04) << 2));
                if (!_fourScreen)
                {
                    _mirroring = (value & 0x01) == 0
                        ? NametableMirroring.Vertical
                        : NametableMirroring.Horizontal;
                }

                break;
            case 0xA000:
                _prgBanks[1] = (byte)(value & 0x0F);
                break;
            case 0xC000:
                _prgBanks[2] = (byte)(value & 0x0F);
                break;
            case 0xE000:
                _chrBanks[0] = (byte)((_chrBanks[0] & 0x10) | (value & 0x0F));
                break;
            case 0xF000:
                _chrBanks[1] = (byte)((_chrBanks[1] & 0x10) | (value & 0x0F));
                break;
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
        Mapper0.WriteArray(writer, _prgBanks);
        Mapper0.WriteArray(writer, _chrBanks);
        writer.Write(chrIsRam);
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        writer.Write((int)_mirroring);
    }

    public void LoadState(BinaryReader reader)
    {
        Mapper0.ReadArray(reader, _prgBanks);
        Mapper0.ReadArray(reader, _chrBanks);
        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam)
        {
            Mapper0.ReadArray(reader, chr);
        }

        _mirroring = (NametableMirroring)reader.ReadInt32();
    }

    private int MapChrAddress(ushort address)
    {
        var bankCount = Math.Max(1, chr.Length / 4_096);
        var bank = _chrBanks[address < 0x1000 ? 0 : 1] % bankCount;
        return (bank * 4_096) + (address & 0x0FFF);
    }
}
