namespace PixelDeck.Emulation.Nes;

internal sealed class Mapper9And10(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring initialMirroring,
    CartridgeRam programRam,
    bool isMmc4) : IMapper
{
    private byte _prgBank;
    private byte _chrFd0;
    private byte _chrFe0;
    private byte _chrFd1;
    private byte _chrFe1;
    private byte _latch0 = 0xFE;
    private byte _latch1 = 0xFE;
    private NametableMirroring _mirroring = initialMirroring;

    public NametableMirroring Mirroring => _mirroring;

    public byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            return isMmc4 ? programRam.Read(address - 0x6000) : (byte)0;
        }

        if (address < 0x8000)
        {
            return 0;
        }

        if (isMmc4)
        {
            var bankCount = Math.Max(1, prgRom.Length / 16_384);
            var bank = address < 0xC000 ? _prgBank % bankCount : bankCount - 1;
            return prgRom[(bank * 16_384) + (address & 0x3FFF)];
        }

        var eightKilobyteBanks = Math.Max(1, prgRom.Length / 8_192);
        var slot = (address - 0x8000) / 8_192;
        var selectedBank = slot == 0
            ? _prgBank % eightKilobyteBanks
            : Math.Max(0, eightKilobyteBanks - 4 + slot);
        return prgRom[(selectedBank * 8_192) + (address & 0x1FFF)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            if (isMmc4)
            {
                programRam.Write(address - 0x6000, value);
            }

            return;
        }

        switch (address & 0xF000)
        {
            case 0xA000:
                _prgBank = (byte)(value & 0x0F);
                break;
            case 0xB000:
                _chrFd0 = (byte)(value & 0x1F);
                break;
            case 0xC000:
                _chrFe0 = (byte)(value & 0x1F);
                break;
            case 0xD000:
                _chrFd1 = (byte)(value & 0x1F);
                break;
            case 0xE000:
                _chrFe1 = (byte)(value & 0x1F);
                break;
            case 0xF000:
                _mirroring = (value & 1) == 0
                    ? NametableMirroring.Vertical
                    : NametableMirroring.Horizontal;
                break;
        }
    }

    public byte PpuRead(ushort address)
    {
        var mappedAddress = MapChrAddress(address);
        var value = chr[mappedAddress];
        UpdateLatch(address);
        return value;
    }

    public void PpuWrite(ushort address, byte value)
    {
        if (chrIsRam)
        {
            chr[MapChrAddress(address)] = value;
        }

        UpdateLatch(address);
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(chrIsRam);
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        writer.Write(_prgBank);
        writer.Write(_chrFd0);
        writer.Write(_chrFe0);
        writer.Write(_chrFd1);
        writer.Write(_chrFe1);
        writer.Write(_latch0);
        writer.Write(_latch1);
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
        _chrFd0 = reader.ReadByte();
        _chrFe0 = reader.ReadByte();
        _chrFd1 = reader.ReadByte();
        _chrFe1 = reader.ReadByte();
        _latch0 = reader.ReadByte();
        _latch1 = reader.ReadByte();
        _mirroring = (NametableMirroring)reader.ReadInt32();
    }

    private int MapChrAddress(ushort address)
    {
        var bank = address < 0x1000
            ? _latch0 == 0xFD ? _chrFd0 : _chrFe0
            : _latch1 == 0xFD ? _chrFd1 : _chrFe1;
        var bankCount = Math.Max(1, chr.Length / 4_096);
        return ((bank % bankCount) * 4_096) + (address & 0x0FFF);
    }

    private void UpdateLatch(ushort address)
    {
        if (address is >= 0x1FD8 and <= 0x1FDF)
        {
            _latch1 = 0xFD;
        }
        else if (address is >= 0x1FE8 and <= 0x1FEF)
        {
            _latch1 = 0xFE;
        }
        else if ((isMmc4 && address is >= 0x0FD8 and <= 0x0FDF) ||
                 (!isMmc4 && address == 0x0FD8))
        {
            _latch0 = 0xFD;
        }
        else if ((isMmc4 && address is >= 0x0FE8 and <= 0x0FEF) ||
                 (!isMmc4 && address == 0x0FE8))
        {
            _latch0 = 0xFE;
        }
    }
}
