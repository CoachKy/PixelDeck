namespace PixelDeck.Emulation.Nes;

/// <summary>
/// Konami VRC2/VRC4 family used by iNES mappers 21, 22, and 23.
/// </summary>
internal sealed class Mapper21To23 : IMapper
{
    private readonly int _mapperNumber;
    private readonly int _submapperNumber;
    private readonly bool _isNes20;
    private readonly byte[] _prgRom;
    private readonly byte[] _chr;
    private readonly bool _chrIsRam;
    private readonly CartridgeRam _programRam;
    private readonly ushort[] _chrBanks = new ushort[8];
    private readonly bool _fourScreen;
    private readonly bool _vrc2Only;
    private readonly bool _hasVrc2Latch;
    private byte _prgBank0;
    private byte _prgBank1;
    private bool _swapPrg;
    private bool _wramEnabled;
    private byte _vrc2Latch;
    private NametableMirroring _mirroring;
    private byte _irqLatch;
    private byte _irqCounter;
    private int _irqPrescaler = 341;
    private bool _irqCycleMode;
    private bool _irqEnabled;
    private bool _irqEnableAfterAcknowledgement;
    private bool _irqPending;

    public Mapper21To23(
        int mapperNumber,
        int submapperNumber,
        bool isNes20,
        byte[] prgRom,
        byte[] chr,
        bool chrIsRam,
        NametableMirroring initialMirroring,
        CartridgeRam programRam)
    {
        _mapperNumber = mapperNumber;
        _submapperNumber = submapperNumber;
        _isNes20 = isNes20;
        _prgRom = prgRom;
        _chr = chr;
        _chrIsRam = chrIsRam;
        _programRam = programRam;
        _fourScreen = initialMirroring == NametableMirroring.FourScreen;
        _mirroring = initialMirroring;
        _vrc2Only = mapperNumber == 22 ||
                    (mapperNumber == 23 && isNes20 && submapperNumber == 3);
        _hasVrc2Latch = mapperNumber == 22 ||
                        (mapperNumber == 23 && (!isNes20 || submapperNumber is 0 or 3));
    }

    public NametableMirroring Mirroring => _fourScreen
        ? NametableMirroring.FourScreen
        : _mirroring;

    public bool IrqPending => _irqPending;

    public byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            if (_wramEnabled)
            {
                return _programRam.Read(address - 0x6000);
            }

            if (_hasVrc2Latch && address < 0x7000)
            {
                return (byte)(((address >> 8) & 0xFE) | _vrc2Latch);
            }

            return 0;
        }

        if (address < 0x8000)
        {
            return 0;
        }

        var bankCount = Math.Max(1, _prgRom.Length / 8_192);
        var slot = (address - 0x8000) / 8_192;
        int bank;
        if (_vrc2Only)
        {
            bank = slot switch
            {
                0 => _prgBank0,
                1 => _prgBank1,
                2 => bankCount - 2,
                _ => bankCount - 1
            };
        }
        else
        {
            bank = slot switch
            {
                0 => _swapPrg ? bankCount - 2 : _prgBank0,
                1 => _prgBank1,
                2 => _swapPrg ? _prgBank0 : bankCount - 2,
                _ => bankCount - 1
            };
        }

        bank = ((bank % bankCount) + bankCount) % bankCount;
        return _prgRom[(bank * 8_192) + (address & 0x1FFF)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            if (_wramEnabled)
            {
                _programRam.Write(address - 0x6000, value);
            }

            if (_hasVrc2Latch && address < 0x7000)
            {
                _vrc2Latch = (byte)(value & 1);
            }

            return;
        }

        if (address < 0x8000)
        {
            return;
        }

        var selector = DecodeRegisterSelector(address);
        switch (address & 0xF000)
        {
            case 0x8000:
                _prgBank0 = (byte)(value & 0x1F);
                break;
            case 0x9000:
                WriteControl(selector, value);
                break;
            case 0xA000:
                _prgBank1 = (byte)(value & 0x1F);
                break;
            case >= 0xB000 and <= 0xE000:
                WriteChrRegister((address >> 12) - 0x0B, selector, value);
                break;
            case 0xF000 when !_vrc2Only:
                WriteIrqRegister(selector, value);
                break;
        }
    }

    public byte PpuRead(ushort address) => _chr[MapChrAddress(address)];

    public void PpuWrite(ushort address, byte value)
    {
        if (_chrIsRam)
        {
            _chr[MapChrAddress(address)] = value;
        }
    }

    public void ClockCpuCycle()
    {
        if (!_irqEnabled || _vrc2Only)
        {
            return;
        }

        if (_irqCycleMode)
        {
            ClockIrqCounter();
            return;
        }

        _irqPrescaler -= 3;
        if (_irqPrescaler <= 0)
        {
            _irqPrescaler += 341;
            ClockIrqCounter();
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        foreach (var bank in _chrBanks)
        {
            writer.Write(bank);
        }
        writer.Write(_chrIsRam);
        if (_chrIsRam)
        {
            Mapper0.WriteArray(writer, _chr);
        }

        writer.Write(_mapperNumber);
        writer.Write(_submapperNumber);
        writer.Write(_isNes20);
        writer.Write(_prgBank0);
        writer.Write(_prgBank1);
        writer.Write(_swapPrg);
        writer.Write(_wramEnabled);
        writer.Write(_vrc2Latch);
        writer.Write((int)_mirroring);
        writer.Write(_irqLatch);
        writer.Write(_irqCounter);
        writer.Write(_irqPrescaler);
        writer.Write(_irqCycleMode);
        writer.Write(_irqEnabled);
        writer.Write(_irqEnableAfterAcknowledgement);
        writer.Write(_irqPending);
    }

    public void LoadState(BinaryReader reader)
    {
        for (var index = 0; index < _chrBanks.Length; index++)
        {
            _chrBanks[index] = reader.ReadUInt16();
        }
        if (reader.ReadBoolean() != _chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (_chrIsRam)
        {
            Mapper0.ReadArray(reader, _chr);
        }

        if (reader.ReadInt32() != _mapperNumber ||
            reader.ReadInt32() != _submapperNumber ||
            reader.ReadBoolean() != _isNes20)
        {
            throw new InvalidDataException("The save state's VRC board does not match this cartridge.");
        }

        _prgBank0 = reader.ReadByte();
        _prgBank1 = reader.ReadByte();
        _swapPrg = reader.ReadBoolean();
        _wramEnabled = reader.ReadBoolean();
        _vrc2Latch = reader.ReadByte();
        _mirroring = (NametableMirroring)reader.ReadInt32();
        _irqLatch = reader.ReadByte();
        _irqCounter = reader.ReadByte();
        _irqPrescaler = reader.ReadInt32();
        _irqCycleMode = reader.ReadBoolean();
        _irqEnabled = reader.ReadBoolean();
        _irqEnableAfterAcknowledgement = reader.ReadBoolean();
        _irqPending = reader.ReadBoolean();
    }

    private void WriteControl(int selector, byte value)
    {
        if (_vrc2Only || selector != 2)
        {
            if (!_fourScreen && (_vrc2Only || selector == 0))
            {
                _mirroring = (value & (_vrc2Only ? 1 : 3)) switch
                {
                    0 => NametableMirroring.Vertical,
                    1 => NametableMirroring.Horizontal,
                    2 => NametableMirroring.OneScreenLower,
                    _ => NametableMirroring.OneScreenUpper
                };
            }

            return;
        }

        _wramEnabled = (value & 1) != 0;
        _swapPrg = (value & 2) != 0;
    }

    private void WriteChrRegister(int registerPair, int selector, byte value)
    {
        var bankIndex = (registerPair * 2) + (selector >> 1);
        if (bankIndex is < 0 or >= 8)
        {
            return;
        }

        if ((selector & 1) == 0)
        {
            _chrBanks[bankIndex] = (ushort)((_chrBanks[bankIndex] & 0x1F0) | (value & 0x0F));
        }
        else
        {
            var highMask = _vrc2Only ? 0x0F : 0x1F;
            _chrBanks[bankIndex] = (ushort)((_chrBanks[bankIndex] & 0x0F) | ((value & highMask) << 4));
        }
    }

    private void WriteIrqRegister(int selector, byte value)
    {
        switch (selector)
        {
            case 0:
                _irqLatch = (byte)((_irqLatch & 0xF0) | (value & 0x0F));
                break;
            case 1:
                _irqLatch = (byte)((_irqLatch & 0x0F) | ((value & 0x0F) << 4));
                break;
            case 2:
                _irqEnableAfterAcknowledgement = (value & 1) != 0;
                _irqEnabled = (value & 2) != 0;
                _irqCycleMode = (value & 4) != 0;
                _irqPending = false;
                _irqPrescaler = 341;
                if (_irqEnabled)
                {
                    _irqCounter = _irqLatch;
                }

                break;
            case 3:
                _irqPending = false;
                _irqEnabled = _irqEnableAfterAcknowledgement;
                break;
        }
    }

    private void ClockIrqCounter()
    {
        if (_irqCounter == byte.MaxValue)
        {
            _irqCounter = _irqLatch;
            _irqPending = true;
        }
        else
        {
            _irqCounter++;
        }
    }

    private int DecodeRegisterSelector(ushort address)
    {
        var low = address & 0x0FFF;
        return _mapperNumber switch
        {
            22 => ((low >> 1) & 1) | ((low & 1) << 1),
            21 when _submapperNumber == 1 => (low >> 1) & 3,
            21 when _submapperNumber == 2 => (low >> 6) & 3,
            21 => (low & 0x06) != 0 ? (low >> 1) & 3 : (low >> 6) & 3,
            23 when _submapperNumber is 1 or 3 => low & 3,
            23 when _submapperNumber == 2 => (low >> 2) & 3,
            23 => (low & 3) != 0 ? low & 3 : (low >> 2) & 3,
            _ => 0
        };
    }

    private int MapChrAddress(ushort address)
    {
        var bank = _chrBanks[address / 1_024];
        if (_mapperNumber == 22)
        {
            bank >>= 1;
        }

        var bankCount = Math.Max(1, _chr.Length / 1_024);
        return ((bank % bankCount) * 1_024) + (address & 0x03FF);
    }
}
