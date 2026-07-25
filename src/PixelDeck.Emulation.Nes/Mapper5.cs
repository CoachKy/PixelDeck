namespace PixelDeck.Emulation.Nes;

/// <summary>
/// Nintendo MMC5 (NES mapper 5).
///
/// The mapper exposes 8 KiB-granular PRG banking, independent background and
/// sprite CHR banks, per-nametable CIRAM/ExRAM/fill selection, extended
/// attributes, scanline IRQs, protected work RAM, the hardware multiplier, and
/// the complete pulse/PCM expansion-audio block. Vertical split rendering
/// remains separate because it requires additional fetch-pipeline integration.
/// </summary>
internal sealed class Mapper5(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    CartridgeRam programRam) : IMapper
{
    private readonly Mmc5Audio _audio = new();
    private readonly byte[] _exRam = new byte[1_024];
    private readonly byte[] _prgBanks = [0, 0, 0, 0, 0xFF];
    private readonly ushort[] _chrBanks = new ushort[12];

    private byte _prgMode = 3;
    private byte _chrMode;
    private byte _prgRamProtect1;
    private byte _prgRamProtect2;
    private byte _exRamMode;
    private byte _nametableMapping;
    private byte _fillTile;
    private byte _fillColor;
    private byte _chrUpperBits;
    private bool _lastChrRegisterWasA = true;
    private bool _largeSprites;

    private byte _splitControl;
    private byte _splitScroll;
    private byte _splitBank;

    private byte _irqTarget;
    private byte _scanlineCounter;
    private bool _irqEnabled;
    private bool _irqPending;
    private bool _inFrame;

    private byte _multiplierOne;
    private byte _multiplierTwo;

    private int _extendedAttributeTileOffset;
    private bool _extendedAttributeValid;

    public NametableMirroring Mirroring => NametableMirroring.Horizontal;

    public bool IrqPending => (_irqEnabled && _irqPending) || _audio.IrqPending;

    public float ExpansionAudioOutput => _audio.Output;

    public byte CpuRead(ushort address)
    {
        if (address is 0xFFFA or 0xFFFB)
        {
            // MMC5 uses the NMI vector fetch as an additional end-of-frame
            // indication. This keeps a stale scanline IRQ from leaking into
            // the next frame when software does not read $5204 first.
            _inFrame = false;
            _scanlineCounter = 0;
            _irqPending = false;
        }

        if (address == 0x5204)
        {
            var status = (byte)((_irqPending ? 0x80 : 0) | (_inFrame ? 0x40 : 0));
            _irqPending = false;
            return status;
        }

        if (address == 0x5010)
        {
            return _audio.ReadPcmStatus();
        }

        if (address == 0x5015)
        {
            return _audio.ReadPulseStatus();
        }

        if (address == 0x5205)
        {
            return (byte)(_multiplierOne * _multiplierTwo);
        }

        if (address == 0x5206)
        {
            return (byte)((_multiplierOne * _multiplierTwo) >> 8);
        }

        if (address is >= 0x5C00 and <= 0x5FFF)
        {
            return _exRamMode >= 2 ? _exRam[address - 0x5C00] : (byte)0;
        }

        if (address is >= 0x6000 and < 0x8000)
        {
            return ReadProgramRam(_prgBanks[0], address & 0x1FFF);
        }

        if (address < 0x8000)
        {
            return 0;
        }

        var mapping = ResolveProgramMapping(address);
        byte value;
        if (!mapping.IsRom)
        {
            value = ReadProgramRam(mapping.Bank, address & 0x1FFF);
        }
        else
        {
            var bankCount = Math.Max(1, prgRom.Length / 8_192);
            var bank = mapping.Bank % bankCount;
            value = prgRom[(bank * 8_192) + (address & 0x1FFF)];
        }

        if (address <= 0xBFFF)
        {
            _audio.ObserveProgramRead(value);
        }

        return value;
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is (>= 0x5000 and <= 0x5007) or 0x5010 or 0x5011 or 0x5015)
        {
            _audio.WriteRegister(address, value);
            return;
        }

        if (address is >= 0x5C00 and <= 0x5FFF)
        {
            var index = address - 0x5C00;
            switch (_exRamMode)
            {
                case 0:
                case 1:
                    _exRam[index] = _inFrame ? value : (byte)0;
                    break;
                case 2:
                    _exRam[index] = value;
                    break;
            }

            return;
        }

        if (address is >= 0x6000 and < 0x8000)
        {
            WriteProgramRam(_prgBanks[0], address & 0x1FFF, value);
            return;
        }

        if (address >= 0x8000)
        {
            var mapping = ResolveProgramMapping(address);
            if (!mapping.IsRom)
            {
                WriteProgramRam(mapping.Bank, address & 0x1FFF, value);
            }

            return;
        }

        if (address is >= 0x5113 and <= 0x5117)
        {
            _prgBanks[address - 0x5113] = value;
            return;
        }

        if (address is >= 0x5120 and <= 0x512B)
        {
            var index = address - 0x5120;
            _chrBanks[index] = (ushort)(value | (_chrUpperBits << 8));
            _lastChrRegisterWasA = index < 8;
            return;
        }

        switch (address)
        {
            case 0x5100:
                _prgMode = (byte)(value & 0x03);
                break;
            case 0x5101:
                _chrMode = (byte)(value & 0x03);
                break;
            case 0x5102:
                _prgRamProtect1 = (byte)(value & 0x03);
                break;
            case 0x5103:
                _prgRamProtect2 = (byte)(value & 0x03);
                break;
            case 0x5104:
                _exRamMode = (byte)(value & 0x03);
                _extendedAttributeValid = false;
                break;
            case 0x5105:
                _nametableMapping = value;
                break;
            case 0x5106:
                _fillTile = value;
                break;
            case 0x5107:
                _fillColor = (byte)(value & 0x03);
                break;
            case 0x5130:
                _chrUpperBits = (byte)(value & 0x03);
                break;
            case 0x5200:
                _splitControl = value;
                break;
            case 0x5201:
                _splitScroll = value;
                break;
            case 0x5202:
                _splitBank = value;
                break;
            case 0x5203:
                _irqTarget = value;
                break;
            case 0x5204:
                _irqEnabled = (value & 0x80) != 0;
                break;
            case 0x5205:
                _multiplierOne = value;
                break;
            case 0x5206:
                _multiplierTwo = value;
                break;
        }
    }

    public byte PpuRead(ushort address) => PpuRead(address, PpuAccessKind.Cpu);

    public byte PpuRead(ushort address, PpuAccessKind accessKind)
    {
        if (_exRamMode == 1 &&
            accessKind == PpuAccessKind.Background &&
            _extendedAttributeValid)
        {
            var attributes = _exRam[_extendedAttributeTileOffset];
            var bank = ((_chrUpperBits << 6) | (attributes & 0x3F));
            var mapped = (bank * 4_096) + (address & 0x0FFF);
            return chr[mapped % chr.Length];
        }

        return chr[MapChrAddress(address, accessKind)];
    }

    public void PpuWrite(ushort address, byte value)
    {
        if (chrIsRam)
        {
            chr[MapChrAddress(address, PpuAccessKind.Cpu)] = value;
        }
    }

    public bool TryPpuReadNametable(
        ushort address,
        PpuAccessKind accessKind,
        byte[] nametableRam,
        out byte value)
    {
        var normalized = (address - 0x2000) & 0x0FFF;
        var table = normalized >> 10;
        var offset = normalized & 0x03FF;

        if (_exRamMode == 1 && accessKind == PpuAccessKind.Background)
        {
            if (offset < 0x03C0)
            {
                _extendedAttributeTileOffset = offset;
                _extendedAttributeValid = true;
            }
            else if (_extendedAttributeValid)
            {
                var palette = _exRam[_extendedAttributeTileOffset] >> 6;
                value = (byte)(palette * 0x55);
                return true;
            }
        }

        var source = (_nametableMapping >> (table * 2)) & 0x03;
        value = source switch
        {
            0 => nametableRam[offset],
            1 => nametableRam[0x0400 + offset],
            2 when _exRamMode <= 1 => _exRam[offset],
            3 when offset < 0x03C0 => _fillTile,
            3 => ReplicatePalette(_fillColor),
            _ => 0
        };
        return true;
    }

    public bool TryPpuWriteNametable(ushort address, byte value, byte[] nametableRam)
    {
        var normalized = (address - 0x2000) & 0x0FFF;
        var table = normalized >> 10;
        var offset = normalized & 0x03FF;
        var source = (_nametableMapping >> (table * 2)) & 0x03;
        switch (source)
        {
            case 0:
                nametableRam[offset] = value;
                break;
            case 1:
                nametableRam[0x0400 + offset] = value;
                break;
            case 2 when _exRamMode <= 1:
                _exRam[offset] = value;
                break;
        }

        return true;
    }

    public void ClockPpuPosition(int scanline, int cycle, bool renderingEnabled)
    {
        if (!renderingEnabled || scanline is < 0 or >= 240)
        {
            _inFrame = false;
            _scanlineCounter = 0;
            _extendedAttributeValid = false;
            return;
        }

        if (cycle != 0)
        {
            return;
        }

        if (!_inFrame)
        {
            _inFrame = true;
            _scanlineCounter = 0;
            return;
        }

        _scanlineCounter++;
        if (_scanlineCounter == _irqTarget)
        {
            _irqPending = true;
        }
    }

    public void SetPpuControl(byte value) => _largeSprites = (value & 0x20) != 0;

    public void ClockCpuCycle() => _audio.ClockCpuCycle();

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(chrIsRam);
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        Mapper0.WriteArray(writer, _exRam);
        Mapper0.WriteArray(writer, _prgBanks);
        writer.Write(_chrBanks.Length);
        foreach (var bank in _chrBanks)
        {
            writer.Write(bank);
        }

        writer.Write(_prgMode);
        writer.Write(_chrMode);
        writer.Write(_prgRamProtect1);
        writer.Write(_prgRamProtect2);
        writer.Write(_exRamMode);
        writer.Write(_nametableMapping);
        writer.Write(_fillTile);
        writer.Write(_fillColor);
        writer.Write(_chrUpperBits);
        writer.Write(_lastChrRegisterWasA);
        writer.Write(_largeSprites);
        writer.Write(_splitControl);
        writer.Write(_splitScroll);
        writer.Write(_splitBank);
        writer.Write(_irqTarget);
        writer.Write(_scanlineCounter);
        writer.Write(_irqEnabled);
        writer.Write(_irqPending);
        writer.Write(_inFrame);
        writer.Write(_multiplierOne);
        writer.Write(_multiplierTwo);
        writer.Write(_extendedAttributeTileOffset);
        writer.Write(_extendedAttributeValid);
        _audio.SaveState(writer);
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

        Mapper0.ReadArray(reader, _exRam);
        Mapper0.ReadArray(reader, _prgBanks);
        if (reader.ReadInt32() != _chrBanks.Length)
        {
            throw new InvalidDataException("The save state contains incompatible MMC5 CHR banks.");
        }

        for (var index = 0; index < _chrBanks.Length; index++)
        {
            _chrBanks[index] = reader.ReadUInt16();
        }

        _prgMode = reader.ReadByte();
        _chrMode = reader.ReadByte();
        _prgRamProtect1 = reader.ReadByte();
        _prgRamProtect2 = reader.ReadByte();
        _exRamMode = reader.ReadByte();
        _nametableMapping = reader.ReadByte();
        _fillTile = reader.ReadByte();
        _fillColor = reader.ReadByte();
        _chrUpperBits = reader.ReadByte();
        _lastChrRegisterWasA = reader.ReadBoolean();
        _largeSprites = reader.ReadBoolean();
        _splitControl = reader.ReadByte();
        _splitScroll = reader.ReadByte();
        _splitBank = reader.ReadByte();
        _irqTarget = reader.ReadByte();
        _scanlineCounter = reader.ReadByte();
        _irqEnabled = reader.ReadBoolean();
        _irqPending = reader.ReadBoolean();
        _inFrame = reader.ReadBoolean();
        _multiplierOne = reader.ReadByte();
        _multiplierTwo = reader.ReadByte();
        _extendedAttributeTileOffset = reader.ReadInt32();
        _extendedAttributeValid = reader.ReadBoolean();
        _audio.LoadState(reader);

        if (_prgMode > 3 || _chrMode > 3 || _exRamMode > 3 ||
            _extendedAttributeTileOffset is < 0 or >= 1_024)
        {
            throw new InvalidDataException("The save state contains invalid MMC5 mapper state.");
        }
    }

    private (int Bank, bool IsRom) ResolveProgramMapping(ushort address)
    {
        var slot = (address - 0x8000) / 8_192;
        byte register;
        int bank;
        bool forcedRom;

        switch (_prgMode)
        {
            case 0:
                register = _prgBanks[4];
                bank = (register & 0x7C) + slot;
                forcedRom = true;
                break;
            case 1:
                if (slot < 2)
                {
                    register = _prgBanks[2];
                    bank = (register & 0x7E) + slot;
                    forcedRom = false;
                }
                else
                {
                    register = _prgBanks[4];
                    bank = (register & 0x7E) + (slot - 2);
                    forcedRom = true;
                }

                break;
            case 2:
                if (slot < 2)
                {
                    register = _prgBanks[2];
                    bank = (register & 0x7E) + slot;
                    forcedRom = false;
                }
                else if (slot == 2)
                {
                    register = _prgBanks[3];
                    bank = register & 0x7F;
                    forcedRom = false;
                }
                else
                {
                    register = _prgBanks[4];
                    bank = register & 0x7F;
                    forcedRom = true;
                }

                break;
            default:
                register = _prgBanks[slot + 1];
                bank = register & 0x7F;
                forcedRom = slot == 3;
                break;
        }

        return (bank, forcedRom || (register & 0x80) != 0);
    }

    private int MapChrAddress(ushort address, PpuAccessKind accessKind)
    {
        var slot = (address & 0x1FFF) / 1_024;
        var useSetA = !_largeSprites || accessKind switch
        {
            PpuAccessKind.Sprite => true,
            PpuAccessKind.Background => false,
            _ => _lastChrRegisterWasA
        };

        int bank;
        switch (_chrMode)
        {
            case 0:
                bank = (_chrBanks[useSetA ? 7 : 11] << 3) + slot;
                break;
            case 1:
                var fourKilobyteRegister = useSetA
                    ? (slot < 4 ? 3 : 7)
                    : 11;
                bank = (_chrBanks[fourKilobyteRegister] << 2) + (slot & 3);
                break;
            case 2:
                var twoKilobyteRegister = useSetA
                    ? ((slot >> 1) * 2) + 1
                    : 9 + (((slot >> 1) & 1) * 2);
                bank = (_chrBanks[twoKilobyteRegister] << 1) + (slot & 1);
                break;
            default:
                var oneKilobyteRegister = useSetA ? slot : 8 + (slot & 3);
                bank = _chrBanks[oneKilobyteRegister];
                break;
        }

        var bankCount = Math.Max(1, chr.Length / 1_024);
        return ((bank % bankCount) * 1_024) + (address & 0x03FF);
    }

    private byte ReadProgramRam(int bank, int offset) =>
        programRam.Read((bank * 8_192) + offset);

    private void WriteProgramRam(int bank, int offset, byte value)
    {
        if (_prgRamProtect1 == 0x02 && _prgRamProtect2 == 0x01)
        {
            programRam.Write((bank * 8_192) + offset, value);
        }
    }

    private static byte ReplicatePalette(byte palette) =>
        (byte)(palette | (palette << 2) | (palette << 4) | (palette << 6));
}
