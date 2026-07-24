namespace PixelDeck.Emulation.Nes;

/// <summary>
/// Tengen RAMBO-1 (iNES mapper 64).
/// </summary>
internal sealed class Mapper64(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring initialMirroring) : IMapper
{
    private readonly byte[] _registers = new byte[16];
    private readonly bool _fourScreen = initialMirroring == NametableMirroring.FourScreen;
    private byte _bankSelect;
    private NametableMirroring _mirroring = initialMirroring;
    private byte _irqLatch;
    private byte _irqCounter;
    private bool _irqReload;
    private bool _irqCycleMode;
    private bool _irqEnabled;
    private bool _irqPending;
    private byte _cpuPrescaler = 4;
    private byte _irqDelay;
    private bool _lastA12;
    private byte _a12LowCycles;

    public NametableMirroring Mirroring => _fourScreen
        ? NametableMirroring.FourScreen
        : _mirroring;

    public bool IrqPending => _irqPending;

    public byte CpuRead(ushort address)
    {
        if (address < 0x8000)
        {
            return 0;
        }

        var bankCount = Math.Max(1, prgRom.Length / 8_192);
        var slot = (address - 0x8000) / 8_192;
        var swap = (_bankSelect & 0x40) != 0;
        var bank = slot switch
        {
            0 => swap ? _registers[15] : _registers[6],
            1 => _registers[7],
            2 => swap ? _registers[6] : _registers[15],
            _ => bankCount - 1
        };

        bank %= bankCount;
        return prgRom[(bank * 8_192) + (address & 0x1FFF)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address < 0x8000)
        {
            return;
        }

        var odd = (address & 1) != 0;
        if (address < 0xA000)
        {
            if (odd)
            {
                _registers[_bankSelect & 0x0F] = value;
            }
            else
            {
                _bankSelect = value;
            }
        }
        else if (address < 0xC000)
        {
            if (!odd && !_fourScreen)
            {
                _mirroring = (value & 1) == 0
                    ? NametableMirroring.Vertical
                    : NametableMirroring.Horizontal;
            }
        }
        else if (address < 0xE000)
        {
            if (odd)
            {
                _irqCycleMode = (value & 1) != 0;
                _irqCounter = 0;
                _irqReload = true;
                _cpuPrescaler = 4;
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
            _irqDelay = 0;
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

    public void ClockCpuCycle()
    {
        if (_irqDelay > 0 && --_irqDelay == 0 && _irqEnabled)
        {
            _irqPending = true;
        }

        if (!_irqCycleMode)
        {
            return;
        }

        if (--_cpuPrescaler == 0)
        {
            _cpuPrescaler = 4;
            ClockIrqCounter();
        }
    }

    public void ClockScanline()
    {
        if (!_irqCycleMode)
        {
            ClockIrqCounter();
        }
    }

    public void ClockPpuAddress(ushort address)
    {
        if (_irqCycleMode)
        {
            return;
        }

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

    public void SaveState(BinaryWriter writer)
    {
        Mapper0.WriteArray(writer, _registers);
        writer.Write(chrIsRam);
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        writer.Write(_bankSelect);
        writer.Write((int)_mirroring);
        writer.Write(_irqLatch);
        writer.Write(_irqCounter);
        writer.Write(_irqReload);
        writer.Write(_irqCycleMode);
        writer.Write(_irqEnabled);
        writer.Write(_irqPending);
        writer.Write(_cpuPrescaler);
        writer.Write(_irqDelay);
        writer.Write(_lastA12);
        writer.Write(_a12LowCycles);
    }

    public void LoadState(BinaryReader reader)
    {
        Mapper0.ReadArray(reader, _registers);
        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam)
        {
            Mapper0.ReadArray(reader, chr);
        }

        _bankSelect = reader.ReadByte();
        _mirroring = (NametableMirroring)reader.ReadInt32();
        _irqLatch = reader.ReadByte();
        _irqCounter = reader.ReadByte();
        _irqReload = reader.ReadBoolean();
        _irqCycleMode = reader.ReadBoolean();
        _irqEnabled = reader.ReadBoolean();
        _irqPending = reader.ReadBoolean();
        _cpuPrescaler = reader.ReadByte();
        _irqDelay = reader.ReadByte();
        _lastA12 = reader.ReadBoolean();
        _a12LowCycles = reader.ReadByte();
    }

    private void ClockIrqCounter()
    {
        if (_irqReload)
        {
            _irqCounter = _irqLatch;
            if (_irqCounter != 0)
            {
                _irqCounter |= 1;
            }

            _irqReload = false;
        }
        else if (_irqCounter == 0)
        {
            _irqCounter = _irqLatch;
        }
        else
        {
            _irqCounter--;
        }

        if (_irqCounter == 0 && _irqEnabled)
        {
            _irqDelay = 4;
        }
    }

    private int MapChrAddress(ushort address)
    {
        var bank = GetChrBank(address / 1_024);
        var bankCount = Math.Max(1, chr.Length / 1_024);
        return ((bank % bankCount) * 1_024) + (address & 0x03FF);
    }

    private int GetChrBank(int slot)
    {
        var inverted = (_bankSelect & 0x80) != 0;
        var oneKilobyteMode = (_bankSelect & 0x20) != 0;
        if (!inverted)
        {
            return slot switch
            {
                0 => oneKilobyteMode ? _registers[0] : _registers[0] & 0xFE,
                1 => oneKilobyteMode ? _registers[8] : (_registers[0] & 0xFE) + 1,
                2 => oneKilobyteMode ? _registers[1] : _registers[1] & 0xFE,
                3 => oneKilobyteMode ? _registers[9] : (_registers[1] & 0xFE) + 1,
                _ => _registers[slot - 2]
            };
        }

        return slot switch
        {
            0 or 1 or 2 or 3 => _registers[slot + 2],
            4 => oneKilobyteMode ? _registers[0] : _registers[0] & 0xFE,
            5 => oneKilobyteMode ? _registers[8] : (_registers[0] & 0xFE) + 1,
            6 => oneKilobyteMode ? _registers[1] : _registers[1] & 0xFE,
            _ => oneKilobyteMode ? _registers[9] : (_registers[1] & 0xFE) + 1
        };
    }
}
