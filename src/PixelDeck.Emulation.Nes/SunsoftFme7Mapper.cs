namespace PixelDeck.Emulation.Nes;

/// <summary>
/// Sunsoft FME-7 / 5A / 5B (iNES mapper 69).
/// </summary>
internal sealed class Mapper69(
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring initialMirroring,
    CartridgeRam programRam) : IMapper
{
    private readonly byte[] _chrBanks = new byte[8];
    private readonly byte[] _prgBanks = new byte[4];
    private readonly Sunsoft5BAudio _audio = new();
    private readonly bool _fourScreen = initialMirroring == NametableMirroring.FourScreen;
    private byte _command;
    private NametableMirroring _mirroring = initialMirroring;
    private ushort _irqCounter;
    private bool _irqCounterEnabled;
    private bool _irqEnabled;
    private bool _irqPending;

    public NametableMirroring Mirroring => _fourScreen
        ? NametableMirroring.FourScreen
        : _mirroring;

    public bool IrqPending => _irqPending;

    public float ExpansionAudioOutput => _audio.Output;

    public byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            var control = _prgBanks[0];
            if ((control & 0x40) != 0)
            {
                if ((control & 0x80) == 0)
                {
                    return 0;
                }

                var ramOffset = ((control & 0x3F) * 8_192) + (address - 0x6000);
                return programRam.Read(ramOffset);
            }

            return ReadPrgBank(control & 0x3F, address);
        }

        if (address < 0x8000)
        {
            return 0;
        }

        var bank = address switch
        {
            < 0xA000 => _prgBanks[1] & 0x3F,
            < 0xC000 => _prgBanks[2] & 0x3F,
            < 0xE000 => _prgBanks[3] & 0x3F,
            _ => Math.Max(0, (prgRom.Length / 8_192) - 1)
        };
        return ReadPrgBank(bank, address);
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            var control = _prgBanks[0];
            if ((control & 0xC0) == 0xC0)
            {
                var ramOffset = ((control & 0x3F) * 8_192) + (address - 0x6000);
                programRam.Write(ramOffset, value);
            }

            return;
        }

        switch (address)
        {
            case >= 0x8000 and < 0xA000:
                _command = (byte)(value & 0x0F);
                break;
            case >= 0xA000 and < 0xC000:
                WriteParameter(value);
                break;
            case >= 0xC000 and < 0xE000:
                _audio.SelectRegister(value);
                break;
            case >= 0xE000:
                _audio.WriteSelectedRegister(value);
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

    public void ClockCpuCycle()
    {
        if (_irqCounterEnabled)
        {
            if (_irqCounter == 0)
            {
                _irqCounter = ushort.MaxValue;
                if (_irqEnabled)
                {
                    _irqPending = true;
                }
            }
            else
            {
                _irqCounter--;
            }
        }

        _audio.ClockCpuCycle();
    }

    public void SaveState(BinaryWriter writer)
    {
        Mapper0.WriteArray(writer, _chrBanks);
        Mapper0.WriteArray(writer, _prgBanks);
        writer.Write(chrIsRam);
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        writer.Write(_command);
        writer.Write((int)_mirroring);
        writer.Write(_irqCounter);
        writer.Write(_irqCounterEnabled);
        writer.Write(_irqEnabled);
        writer.Write(_irqPending);
        _audio.SaveState(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        Mapper0.ReadArray(reader, _chrBanks);
        Mapper0.ReadArray(reader, _prgBanks);
        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam)
        {
            Mapper0.ReadArray(reader, chr);
        }

        _command = reader.ReadByte();
        _mirroring = (NametableMirroring)reader.ReadInt32();
        _irqCounter = reader.ReadUInt16();
        _irqCounterEnabled = reader.ReadBoolean();
        _irqEnabled = reader.ReadBoolean();
        _irqPending = reader.ReadBoolean();
        _audio.LoadState(reader);
    }

    private void WriteParameter(byte value)
    {
        switch (_command)
        {
            case <= 7:
                _chrBanks[_command] = value;
                break;
            case 8:
                _prgBanks[0] = value;
                break;
            case 9:
            case 10:
            case 11:
                _prgBanks[_command - 8] = value;
                break;
            case 12 when !_fourScreen:
                _mirroring = (value & 3) switch
                {
                    0 => NametableMirroring.Vertical,
                    1 => NametableMirroring.Horizontal,
                    2 => NametableMirroring.OneScreenLower,
                    _ => NametableMirroring.OneScreenUpper
                };
                break;
            case 13:
                _irqCounterEnabled = (value & 0x80) != 0;
                _irqEnabled = (value & 1) != 0;
                _irqPending = false;
                break;
            case 14:
                _irqCounter = (ushort)((_irqCounter & 0xFF00) | value);
                break;
            case 15:
                _irqCounter = (ushort)((_irqCounter & 0x00FF) | (value << 8));
                break;
        }
    }

    private byte ReadPrgBank(int bank, ushort address)
    {
        var bankCount = Math.Max(1, prgRom.Length / 8_192);
        bank %= bankCount;
        return prgRom[(bank * 8_192) + (address & 0x1FFF)];
    }

    private int MapChrAddress(ushort address)
    {
        var bankCount = Math.Max(1, chr.Length / 1_024);
        var bank = _chrBanks[address / 1_024] % bankCount;
        return (bank * 1_024) + (address & 0x03FF);
    }
}

internal sealed class Sunsoft5BAudio
{
    private static readonly float[] VolumeTable = CreateVolumeTable();

    private readonly byte[] _registers = new byte[16];
    private readonly ushort[] _toneCounters = new ushort[3];
    private readonly bool[] _toneOutputs = new bool[3];
    private byte _selectedRegister;
    private bool _writesEnabled = true;
    private byte _clockDivider = 16;
    private bool _noiseHalfClock;
    private byte _noiseCounter;
    private uint _noiseLfsr = 1;
    private bool _noiseOutput = true;
    private ushort _envelopeCounter;
    private byte _envelopeStep = 31;
    private bool _envelopeAttack;
    private bool _envelopeAlternate;
    private bool _envelopeHold;
    private bool _envelopeContinue;
    private bool _envelopeHolding;

    public float Output
    {
        get
        {
            var output = 0f;
            for (var channel = 0; channel < 3; channel++)
            {
                var toneEnabled = (_registers[7] & (1 << channel)) == 0;
                var noiseEnabled = (_registers[7] & (1 << (channel + 3))) == 0;
                if ((!toneEnabled || _toneOutputs[channel]) &&
                    (!noiseEnabled || _noiseOutput))
                {
                    var volumeRegister = _registers[8 + channel];
                    var level = (volumeRegister & 0x10) != 0
                        ? EnvelopeLevel
                        : FixedLevel(volumeRegister);
                    output += VolumeTable[level];
                }
            }

            return output / 3f;
        }
    }

    public void SelectRegister(byte value)
    {
        _selectedRegister = (byte)(value & 0x0F);
        _writesEnabled = (value & 0xF0) == 0;
    }

    public void WriteSelectedRegister(byte value)
    {
        if (!_writesEnabled)
        {
            return;
        }

        _registers[_selectedRegister] = _selectedRegister switch
        {
            1 or 3 or 5 => (byte)(value & 0x0F),
            6 => (byte)(value & 0x1F),
            8 or 9 or 10 => (byte)(value & 0x1F),
            13 => (byte)(value & 0x0F),
            _ => value
        };

        if (_selectedRegister == 13)
        {
            ResetEnvelope(_registers[13]);
        }
    }

    public void ClockCpuCycle()
    {
        if (--_clockDivider != 0)
        {
            return;
        }

        _clockDivider = 16;
        ClockTones();
        ClockEnvelope();
        _noiseHalfClock = !_noiseHalfClock;
        if (!_noiseHalfClock)
        {
            ClockNoise();
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        Mapper0.WriteArray(writer, _registers);
        foreach (var counter in _toneCounters)
        {
            writer.Write(counter);
        }
        foreach (var output in _toneOutputs)
        {
            writer.Write(output);
        }

        writer.Write(_selectedRegister);
        writer.Write(_writesEnabled);
        writer.Write(_clockDivider);
        writer.Write(_noiseHalfClock);
        writer.Write(_noiseCounter);
        writer.Write(_noiseLfsr);
        writer.Write(_noiseOutput);
        writer.Write(_envelopeCounter);
        writer.Write(_envelopeStep);
        writer.Write(_envelopeAttack);
        writer.Write(_envelopeAlternate);
        writer.Write(_envelopeHold);
        writer.Write(_envelopeContinue);
        writer.Write(_envelopeHolding);
    }

    public void LoadState(BinaryReader reader)
    {
        Mapper0.ReadArray(reader, _registers);
        for (var channel = 0; channel < _toneCounters.Length; channel++)
        {
            _toneCounters[channel] = reader.ReadUInt16();
        }
        for (var channel = 0; channel < _toneOutputs.Length; channel++)
        {
            _toneOutputs[channel] = reader.ReadBoolean();
        }

        _selectedRegister = reader.ReadByte();
        _writesEnabled = reader.ReadBoolean();
        _clockDivider = reader.ReadByte();
        _noiseHalfClock = reader.ReadBoolean();
        _noiseCounter = reader.ReadByte();
        _noiseLfsr = reader.ReadUInt32();
        _noiseOutput = reader.ReadBoolean();
        _envelopeCounter = reader.ReadUInt16();
        _envelopeStep = reader.ReadByte();
        _envelopeAttack = reader.ReadBoolean();
        _envelopeAlternate = reader.ReadBoolean();
        _envelopeHold = reader.ReadBoolean();
        _envelopeContinue = reader.ReadBoolean();
        _envelopeHolding = reader.ReadBoolean();
    }

    private byte EnvelopeLevel => _envelopeAttack
        ? (byte)(_envelopeStep ^ 0x1F)
        : _envelopeStep;

    private static byte FixedLevel(byte volumeRegister)
    {
        var volume = volumeRegister & 0x0F;
        return volume == 0 ? (byte)0 : (byte)((volume * 2) + 1);
    }

    private void ClockTones()
    {
        for (var channel = 0; channel < 3; channel++)
        {
            var period = Math.Max(
                1,
                _registers[channel * 2] |
                ((_registers[(channel * 2) + 1] & 0x0F) << 8));
            _toneCounters[channel]++;
            if (_toneCounters[channel] >= period)
            {
                _toneCounters[channel] = 0;
                _toneOutputs[channel] = !_toneOutputs[channel];
            }
        }
    }

    private void ClockNoise()
    {
        var period = Math.Max(1, _registers[6] & 0x1F);
        if (++_noiseCounter < period)
        {
            return;
        }

        _noiseCounter = 0;
        // Shifting right makes the documented 16/13 taps appear as bits 0/3.
        var feedback = (_noiseLfsr ^ (_noiseLfsr >> 3)) & 1;
        _noiseLfsr = (_noiseLfsr >> 1) | (feedback << 16);
        _noiseOutput = (_noiseLfsr & 1) != 0;
    }

    private void ClockEnvelope()
    {
        if (_envelopeHolding)
        {
            return;
        }

        var period = Math.Max(1, _registers[11] | (_registers[12] << 8));
        if (++_envelopeCounter < period)
        {
            return;
        }

        _envelopeCounter = 0;
        if (_envelopeStep > 0)
        {
            _envelopeStep--;
            return;
        }

        if (!_envelopeContinue)
        {
            _envelopeHolding = true;
            _envelopeAttack = false;
            return;
        }

        if (_envelopeAlternate)
        {
            _envelopeAttack = !_envelopeAttack;
        }

        if (_envelopeHold)
        {
            _envelopeHolding = true;
            return;
        }

        _envelopeStep = 31;
    }

    private void ResetEnvelope(byte shape)
    {
        _envelopeCounter = 0;
        _envelopeStep = 31;
        _envelopeContinue = (shape & 0x08) != 0;
        _envelopeAttack = (shape & 0x04) != 0;
        _envelopeAlternate = (shape & 0x02) != 0;
        _envelopeHold = (shape & 0x01) != 0;
        _envelopeHolding = false;
    }

    private static float[] CreateVolumeTable()
    {
        var table = new float[32];
        // Envelope levels 0 and 1 both fall below the DAC's audible step.
        for (var level = 2; level < table.Length; level++)
        {
            table[level] = (float)Math.Pow(10, ((level - 31) * 1.5) / 20.0);
        }

        return table;
    }
}
