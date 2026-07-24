namespace PixelDeck.Emulation.Nes;

/// <summary>
/// J.Y. Company ASIC with mapper-90 wiring. Mapper 90 suppresses the ASIC's
/// ROM-nametable and extended-mirroring paths, but retains its flexible
/// PRG/CHR banking, multiplier, accumulator, and IRQ counter.
/// </summary>
internal sealed class Mapper90(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring initialMirroring,
    CartridgeRam programRam) : IMapper
{
    private readonly byte[] _prgBanks = new byte[4];
    private readonly ushort[] _chrBanks = new ushort[8];
    private byte _mode;
    private byte _mirroringRegister;
    private byte _ppuConfiguration;
    private byte _outerBank;
    private byte _multiplyOperand1;
    private byte _multiplyOperand2;
    private ushort _multiplyResult;
    private ushort _multiplyMultiplicand;
    private byte _multiplyMultiplier;
    private byte _multiplyCyclesRemaining;
    private byte _accumulator;
    private byte _testRegister;
    private byte _irqMode;
    private byte _irqPrescaler;
    private byte _irqCounter;
    private byte _irqXor;
    private bool _irqEnabled;
    private bool _irqPending;
    private bool _lastPpuA12;
    private bool _chrLatchLow;
    private bool _chrLatchHigh;
    private NametableMirroring _mirroring = initialMirroring;

    public NametableMirroring Mirroring => _mirroring;

    public bool IrqPending => _irqPending;

    public byte CpuRead(ushort address)
    {
        var register = address & 0xF803;
        if (register is >= 0x5800 and <= 0x5803)
        {
            return register switch
            {
                0x5800 => (byte)_multiplyResult,
                0x5801 => (byte)(_multiplyResult >> 8),
                0x5802 => _accumulator,
                _ => _testRegister
            };
        }

        if (address is >= 0x6000 and < 0x8000)
        {
            if ((_mode & 0x80) != 0)
            {
                var bankingMode = _mode & 0x03;
                var mapped6000Bank = bankingMode switch
                {
                    0 => (_prgBanks[3] << 2) | 3,
                    1 => (_prgBanks[3] << 1) | 1,
                    _ => _prgBanks[3]
                };
                return ReadPrg8K(mapped6000Bank, address);
            }

            return programRam.Read(address - 0x6000);
        }

        if (address < 0x8000)
        {
            return 0;
        }

        var mode = _mode & 0x03;
        var slot = (address - 0x8000) / 8_192;
        var switchLastBank = (_mode & 0x04) != 0;
        var bank = mode switch
        {
            0 => Get32KPrgBank(slot, switchLastBank),
            1 => Get16KPrgBank(slot, switchLastBank),
            2 => Get8KPrgBank(slot, switchLastBank, reverseBits: false),
            _ => Get8KPrgBank(slot, switchLastBank, reverseBits: true)
        };
        return ReadPrg8K(bank, address);
    }

    public void CpuWrite(ushort address, byte value)
    {
        var register = address & 0xF803;
        if (register is >= 0x5800 and <= 0x5803)
        {
            switch (register)
            {
                case 0x5800:
                    _multiplyOperand1 = value;
                    break;
                case 0x5801:
                    _multiplyOperand2 = value;
                    _multiplyResult = 0;
                    _multiplyMultiplicand = _multiplyOperand1;
                    _multiplyMultiplier = value;
                    _multiplyCyclesRemaining = 8;
                    break;
                case 0x5802:
                    _accumulator += value;
                    break;
                case 0x5803:
                    _accumulator = 0;
                    _testRegister = value;
                    break;
            }

            return;
        }

        if (address is >= 0x6000 and < 0x8000)
        {
            if ((_mode & 0x80) == 0)
            {
                programRam.Write(address - 0x6000, value);
            }

            return;
        }

        if ((address & 0xF800) == 0x8000)
        {
            _prgBanks[address & 0x03] = (byte)(value & 0x7F);
            return;
        }

        if ((address & 0xF800) == 0x9000)
        {
            var index = address & 0x07;
            _chrBanks[index] = (ushort)((_chrBanks[index] & 0xFF00) | value);
            return;
        }

        if ((address & 0xF800) == 0xA000)
        {
            var index = address & 0x07;
            _chrBanks[index] = (ushort)((_chrBanks[index] & 0x00FF) | (value << 8));
            return;
        }

        if ((address & 0xF000) == 0xC000)
        {
            WriteIrqRegister(address & 0x07, value);
            return;
        }

        if ((address & 0xF800) != 0xD000)
        {
            return;
        }

        switch (address & 0x03)
        {
            case 0:
                _mode = value;
                break;
            case 1:
                _mirroringRegister = value;
                _mirroring = (value & 0x03) switch
                {
                    0 => NametableMirroring.Vertical,
                    1 => NametableMirroring.Horizontal,
                    2 => NametableMirroring.OneScreenLower,
                    _ => NametableMirroring.OneScreenUpper
                };
                break;
            case 2:
                _ppuConfiguration = value;
                break;
            case 3:
                _outerBank = value;
                break;
        }
    }

    public byte PpuRead(ushort address)
    {
        var mappedAddress = MapChrAddress(address);
        var value = chr[mappedAddress];
        UpdateChrLatch(address);
        return value;
    }

    public void PpuWrite(ushort address, byte value)
    {
        if (chrIsRam && (_ppuConfiguration & 0x40) != 0)
        {
            chr[MapChrAddress(address)] = value;
        }

        UpdateChrLatch(address);
    }

    public void ClockCpuCycle()
    {
        ClockMultiplier();
        if ((_irqMode & 0x03) == 0)
        {
            ClockIrqCounter();
        }
    }

    public void ClockCpuWrite()
    {
        if ((_irqMode & 0x03) == 3)
        {
            ClockIrqCounter();
        }
    }

    public void ClockPpuAddress(ushort address)
    {
        var a12 = (address & 0x1000) != 0;
        if ((_irqMode & 0x03) == 1 && a12 && !_lastPpuA12)
        {
            ClockIrqCounter();
        }

        _lastPpuA12 = a12;
    }

    public void ClockPpuPosition(int scanline, int cycle, bool renderingEnabled)
    {
        if ((_irqMode & 0x03) == 2 &&
            renderingEnabled &&
            (scanline is >= 0 and < 240 || scanline == 261) &&
            cycle is >= 1 and <= 340 &&
            (cycle & 1) != 0)
        {
            // The rendering PPU performs 170 memory reads per scanline.
            ClockIrqCounter();
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        Mapper0.WriteArray(writer, _prgBanks);
        writer.Write(_chrBanks.Length);
        foreach (var bank in _chrBanks)
        {
            writer.Write(bank);
        }

        writer.Write(chrIsRam);
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        writer.Write(_mode);
        writer.Write(_mirroringRegister);
        writer.Write(_ppuConfiguration);
        writer.Write(_outerBank);
        writer.Write(_multiplyOperand1);
        writer.Write(_multiplyOperand2);
        writer.Write(_multiplyResult);
        writer.Write(_multiplyMultiplicand);
        writer.Write(_multiplyMultiplier);
        writer.Write(_multiplyCyclesRemaining);
        writer.Write(_accumulator);
        writer.Write(_testRegister);
        writer.Write(_irqMode);
        writer.Write(_irqPrescaler);
        writer.Write(_irqCounter);
        writer.Write(_irqXor);
        writer.Write(_irqEnabled);
        writer.Write(_irqPending);
        writer.Write(_lastPpuA12);
        writer.Write(_chrLatchLow);
        writer.Write(_chrLatchHigh);
        writer.Write((int)_mirroring);
    }

    public void LoadState(BinaryReader reader)
    {
        Mapper0.ReadArray(reader, _prgBanks);
        if (reader.ReadInt32() != _chrBanks.Length)
        {
            throw new InvalidDataException("The save state contains an incompatible mapper 90 CHR bank count.");
        }

        for (var index = 0; index < _chrBanks.Length; index++)
        {
            _chrBanks[index] = reader.ReadUInt16();
        }

        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam)
        {
            Mapper0.ReadArray(reader, chr);
        }

        _mode = reader.ReadByte();
        _mirroringRegister = reader.ReadByte();
        _ppuConfiguration = reader.ReadByte();
        _outerBank = reader.ReadByte();
        _multiplyOperand1 = reader.ReadByte();
        _multiplyOperand2 = reader.ReadByte();
        _multiplyResult = reader.ReadUInt16();
        _multiplyMultiplicand = reader.ReadUInt16();
        _multiplyMultiplier = reader.ReadByte();
        _multiplyCyclesRemaining = reader.ReadByte();
        _accumulator = reader.ReadByte();
        _testRegister = reader.ReadByte();
        _irqMode = reader.ReadByte();
        _irqPrescaler = reader.ReadByte();
        _irqCounter = reader.ReadByte();
        _irqXor = reader.ReadByte();
        _irqEnabled = reader.ReadBoolean();
        _irqPending = reader.ReadBoolean();
        _lastPpuA12 = reader.ReadBoolean();
        _chrLatchLow = reader.ReadBoolean();
        _chrLatchHigh = reader.ReadBoolean();
        _mirroring = (NametableMirroring)reader.ReadInt32();
    }

    private int Get32KPrgBank(int slot, bool switchLastBank)
    {
        var inner32KBank = switchLastBank
            ? _prgBanks[3]
            : (512 * 1_024 / 32_768) - 1;
        return (inner32KBank * 4) + slot;
    }

    private int Get16KPrgBank(int slot, bool switchLastBank)
    {
        var upperHalf = slot >= 2;
        var inner16KBank = upperHalf
            ? switchLastBank
                ? _prgBanks[3]
                : (512 * 1_024 / 16_384) - 1
            : _prgBanks[1];
        return (inner16KBank * 2) + (slot & 1);
    }

    private int Get8KPrgBank(int slot, bool switchLastBank, bool reverseBits)
    {
        var innerBank = slot switch
        {
            0 => _prgBanks[0],
            1 => _prgBanks[1],
            2 => _prgBanks[2],
            _ => switchLastBank ? _prgBanks[3] : (byte)0x3F
        };
        return reverseBits ? ReverseSevenBits(innerBank) : innerBank;
    }

    private byte ReadPrg8K(int innerBank, ushort address)
    {
        const int banksPerOuterRegion = 512 * 1_024 / 8_192;
        var outer = (_outerBank >> 1) & 0x03;
        var bank = (outer * banksPerOuterRegion) + (innerBank & (banksPerOuterRegion - 1));
        var bankCount = Math.Max(1, prgRom.Length / 8_192);
        return prgRom[((bank % bankCount) * 8_192) + (address & 0x1FFF)];
    }

    private int MapChrAddress(ushort address)
    {
        var chrMode = (_mode >> 3) & 0x03;
        var mmc4Mode = (_outerBank & 0x80) != 0 && chrMode == 1;
        var unitSize = chrMode switch
        {
            0 => 8_192,
            1 => 4_096,
            2 => 2_048,
            _ => 1_024
        };
        var slot = address / unitSize;
        var registerIndex = chrMode switch
        {
            0 => 0,
            1 when mmc4Mode && address < 0x1000 => _chrLatchLow ? 2 : 0,
            1 when mmc4Mode => _chrLatchHigh ? 6 : 4,
            1 => slot * 4,
            2 => slot * 2,
            _ => slot
        };

        var innerBank = _chrBanks[registerIndex];
        var use512KOuterBanks = (_outerBank & 0x20) != 0;
        var outerRegionSize = use512KOuterBanks ? 512 * 1_024 : 256 * 1_024;
        var outer = use512KOuterBanks
            ? (_outerBank >> 3) & 0x03
            : _outerBank & 0x01;
        var banksPerOuterRegion = outerRegionSize / unitSize;
        var bank = (outer * banksPerOuterRegion) + (innerBank & (banksPerOuterRegion - 1));
        var bankCount = Math.Max(1, chr.Length / unitSize);
        return ((bank % bankCount) * unitSize) + (address & (unitSize - 1));
    }

    private void WriteIrqRegister(int register, byte value)
    {
        switch (register)
        {
            case 0:
                SetIrqEnabled((value & 1) != 0);
                break;
            case 1:
                _irqMode = value;
                break;
            case 2:
                SetIrqEnabled(false);
                break;
            case 3:
                _irqEnabled = true;
                break;
            case 4:
                _irqPrescaler = (byte)(value ^ _irqXor);
                break;
            case 5:
                _irqCounter = (byte)(value ^ _irqXor);
                break;
            case 6:
                _irqXor = value;
                break;
            case 7:
                // No known game uses the ASIC's still-undocumented mode.
                break;
        }
    }

    private void ClockMultiplier()
    {
        if (_multiplyCyclesRemaining == 0)
        {
            return;
        }

        if ((_multiplyMultiplier & 1) != 0)
        {
            _multiplyResult += _multiplyMultiplicand;
        }

        _multiplyMultiplicand <<= 1;
        _multiplyMultiplier >>= 1;
        _multiplyCyclesRemaining--;
    }

    private void SetIrqEnabled(bool enabled)
    {
        _irqEnabled = enabled;
        if (!enabled)
        {
            _irqPending = false;
            _irqPrescaler = 0;
        }
    }

    private void ClockIrqCounter()
    {
        if (!_irqEnabled)
        {
            return;
        }

        var direction = (_irqMode >> 6) & 0x03;
        var prescalerMask = (_irqMode & 0x04) != 0 ? 0x07 : 0xFF;
        bool clockCounter;
        if (direction == 1)
        {
            _irqPrescaler++;
            clockCounter = (_irqPrescaler & prescalerMask) == 0;
            if (clockCounter)
            {
                _irqCounter++;
                if (_irqCounter == 0)
                {
                    _irqPending = true;
                }
            }
        }
        else if (direction == 2)
        {
            _irqPrescaler--;
            clockCounter = (_irqPrescaler & prescalerMask) == prescalerMask;
            if (clockCounter)
            {
                _irqCounter--;
                if (_irqCounter == 0xFF)
                {
                    _irqPending = true;
                }
            }
        }
    }

    private void UpdateChrLatch(ushort address)
    {
        if ((_outerBank & 0x80) == 0)
        {
            return;
        }

        if (address is >= 0x0FD8 and <= 0x0FDF)
        {
            _chrLatchLow = false;
        }
        else if (address is >= 0x0FE8 and <= 0x0FEF)
        {
            _chrLatchLow = true;
        }
        else if (address is >= 0x1FD8 and <= 0x1FDF)
        {
            _chrLatchHigh = false;
        }
        else if (address is >= 0x1FE8 and <= 0x1FEF)
        {
            _chrLatchHigh = true;
        }
    }

    private static byte ReverseSevenBits(byte value)
    {
        var reversed = 0;
        for (var bit = 0; bit < 7; bit++)
        {
            reversed = (reversed << 1) | ((value >> bit) & 1);
        }

        return (byte)reversed;
    }
}
