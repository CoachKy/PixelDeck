namespace PixelDeck.Emulation.Nes;

/// <summary>
/// Namco 129/163/175/340 family (iNES mapper 19).
/// </summary>
internal sealed class Mapper19(
    int submapper,
    byte[] prgRom,
    byte[] chr,
    bool chrIsRam,
    NametableMirroring initialMirroring,
    CartridgeRam programRam,
    int externalProgramRamSize) : IMapper
{
    private const int InternalRamSize = 128;
    private const int InternalRamAddressMask = InternalRamSize - 1;

    private readonly byte[] _chrBanks = new byte[12];
    private readonly byte[] _prgBanks = new byte[3];
    private readonly byte[] _ciram = new byte[2_048];
    private readonly float _audioGain = submapper switch
    {
        1 or 2 => 0f,
        3 => 0.5f,
        4 => 0.75f,
        5 => 1f,
        _ => 0.75f
    };
    private ushort _irqCounter;
    private bool _irqEnabled;
    private bool _irqPending;
    private byte _internalRamAddress;
    private bool _internalRamAutoIncrement;
    private byte _ramProtection;
    private bool _disableLowerPatternCiram;
    private bool _disableUpperPatternCiram;
    private bool _soundDisabled;
    private byte _audioDivider = 15;
    private byte _currentAudioChannel;
    private short _serialAudioOutput;

    public NametableMirroring Mirroring => initialMirroring;

    public bool IrqPending => _irqPending;

    public float ExpansionAudioOutput =>
        _soundDisabled ? 0 : (_serialAudioOutput / 120f) * _audioGain;

    public byte CpuRead(ushort address)
    {
        switch (address)
        {
            case >= 0x4800 and < 0x5000:
                return ReadInternalRamPort();
            case >= 0x5000 and < 0x5800:
                return (byte)_irqCounter;
            case >= 0x5800 and < 0x6000:
                return (byte)((_irqCounter >> 8) | (_irqEnabled ? 0x80 : 0));
            case >= 0x6000 and < 0x8000:
                return externalProgramRamSize == 0
                    ? (byte)0
                    : programRam.Read(address - 0x6000);
            case >= 0x8000:
                return ReadProgramRom(address);
            default:
                return 0;
        }
    }

    public void CpuWrite(ushort address, byte value)
    {
        switch (address)
        {
            case >= 0x4800 and < 0x5000:
                WriteInternalRamPort(value);
                return;
            case >= 0x5000 and < 0x5800:
                _irqCounter = (ushort)((_irqCounter & 0x7F00) | value);
                _irqPending = false;
                return;
            case >= 0x5800 and < 0x6000:
                _irqCounter = (ushort)((_irqCounter & 0x00FF) | ((value & 0x7F) << 8));
                _irqEnabled = (value & 0x80) != 0;
                _irqPending = false;
                return;
            case >= 0x6000 and < 0x8000:
                WriteExternalRam(address, value);
                return;
            case >= 0x8000:
                WriteRegister(address, value);
                return;
        }
    }

    public byte PpuRead(ushort address)
    {
        var slot = address / 1_024;
        var bank = _chrBanks[slot];
        if (PatternBankUsesCiram(slot, bank))
        {
            return _ciram[((bank & 1) * 1_024) + (address & 0x03FF)];
        }

        return chr[MapCharacterAddress(bank, address)];
    }

    public void PpuWrite(ushort address, byte value)
    {
        var slot = address / 1_024;
        var bank = _chrBanks[slot];
        if (PatternBankUsesCiram(slot, bank))
        {
            _ciram[((bank & 1) * 1_024) + (address & 0x03FF)] = value;
        }
        else if (chrIsRam)
        {
            chr[MapCharacterAddress(bank, address)] = value;
        }
    }

    public bool TryPpuReadNametable(
        ushort address,
        PpuAccessKind accessKind,
        byte[] nametableRam,
        out byte value)
    {
        var slot = (address >> 10) & 3;
        var bank = _chrBanks[8 + slot];
        if (bank >= 0xE0)
        {
            value = _ciram[((bank & 1) * 1_024) + (address & 0x03FF)];
        }
        else
        {
            value = chr[MapCharacterAddress(bank, address)];
        }

        return true;
    }

    public bool TryPpuWriteNametable(ushort address, byte value, byte[] nametableRam)
    {
        var slot = (address >> 10) & 3;
        var bank = _chrBanks[8 + slot];
        if (bank >= 0xE0)
        {
            _ciram[((bank & 1) * 1_024) + (address & 0x03FF)] = value;
        }
        else if (chrIsRam)
        {
            chr[MapCharacterAddress(bank, address)] = value;
        }

        return true;
    }

    public void ClockCpuCycle()
    {
        if (_irqEnabled && _irqCounter < 0x7FFF)
        {
            _irqCounter++;
            if (_irqCounter == 0x7FFF)
            {
                _irqPending = true;
                _irqEnabled = false;
            }
        }

        if (--_audioDivider == 0)
        {
            _audioDivider = 15;
            ClockAudioChannel();
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        Mapper0.WriteArray(writer, _chrBanks);
        Mapper0.WriteArray(writer, _prgBanks);
        Mapper0.WriteArray(writer, _ciram);
        writer.Write(chrIsRam);
        if (chrIsRam)
        {
            Mapper0.WriteArray(writer, chr);
        }

        writer.Write(_irqCounter);
        writer.Write(_irqEnabled);
        writer.Write(_irqPending);
        writer.Write(_internalRamAddress);
        writer.Write(_internalRamAutoIncrement);
        writer.Write(_ramProtection);
        writer.Write(_disableLowerPatternCiram);
        writer.Write(_disableUpperPatternCiram);
        writer.Write(_soundDisabled);
        writer.Write(_audioDivider);
        writer.Write(_currentAudioChannel);
        writer.Write(_serialAudioOutput);
    }

    public void LoadState(BinaryReader reader)
    {
        Mapper0.ReadArray(reader, _chrBanks);
        Mapper0.ReadArray(reader, _prgBanks);
        Mapper0.ReadArray(reader, _ciram);
        if (reader.ReadBoolean() != chrIsRam)
        {
            throw new InvalidDataException("The save state's CHR memory does not match this cartridge.");
        }

        if (chrIsRam)
        {
            Mapper0.ReadArray(reader, chr);
        }

        _irqCounter = reader.ReadUInt16();
        _irqEnabled = reader.ReadBoolean();
        _irqPending = reader.ReadBoolean();
        _internalRamAddress = reader.ReadByte();
        _internalRamAutoIncrement = reader.ReadBoolean();
        _ramProtection = reader.ReadByte();
        _disableLowerPatternCiram = reader.ReadBoolean();
        _disableUpperPatternCiram = reader.ReadBoolean();
        _soundDisabled = reader.ReadBoolean();
        _audioDivider = reader.ReadByte();
        _currentAudioChannel = reader.ReadByte();
        _serialAudioOutput = reader.ReadInt16();
    }

    private void WriteRegister(ushort address, byte value)
    {
        switch (address & 0xF800)
        {
            case 0x8000:
            case 0x8800:
            case 0x9000:
            case 0x9800:
            case 0xA000:
            case 0xA800:
            case 0xB000:
            case 0xB800:
                _chrBanks[(address - 0x8000) / 0x0800] = value;
                break;
            case 0xC000:
            case 0xC800:
            case 0xD000:
            case 0xD800:
                _chrBanks[8 + ((address - 0xC000) / 0x0800)] = value;
                break;
            case 0xE000:
                _prgBanks[0] = (byte)(value & 0x3F);
                _soundDisabled = (value & 0x40) != 0;
                break;
            case 0xE800:
                _prgBanks[1] = (byte)(value & 0x3F);
                _disableLowerPatternCiram = (value & 0x40) != 0;
                _disableUpperPatternCiram = (value & 0x80) != 0;
                break;
            case 0xF000:
                _prgBanks[2] = (byte)(value & 0x3F);
                break;
            case 0xF800:
                _internalRamAddress = (byte)(value & InternalRamAddressMask);
                _internalRamAutoIncrement = (value & 0x80) != 0;
                _ramProtection = value;
                break;
        }
    }

    private byte ReadProgramRom(ushort address)
    {
        var bankCount = Math.Max(1, prgRom.Length / 8_192);
        var slot = (address - 0x8000) / 8_192;
        var bank = slot < 3 ? _prgBanks[slot] : bankCount - 1;
        bank %= bankCount;
        return prgRom[(bank * 8_192) + (address & 0x1FFF)];
    }

    private void WriteExternalRam(ushort address, byte value)
    {
        if (externalProgramRamSize == 0 || (_ramProtection & 0xF0) != 0x40)
        {
            return;
        }

        var window = (address - 0x6000) / 0x0800;
        if ((_ramProtection & (1 << window)) == 0)
        {
            programRam.Write(address - 0x6000, value);
        }
    }

    private bool PatternBankUsesCiram(int slot, byte bank) =>
        bank >= 0xE0 &&
        (slot < 4 ? !_disableLowerPatternCiram : !_disableUpperPatternCiram);

    private int MapCharacterAddress(byte bank, ushort address)
    {
        var bankCount = Math.Max(1, chr.Length / 1_024);
        return ((bank % bankCount) * 1_024) + (address & 0x03FF);
    }

    private byte ReadInternalRamPort()
    {
        var value = ReadInternalRam(_internalRamAddress);
        IncrementInternalRamAddress();
        return value;
    }

    private void WriteInternalRamPort(byte value)
    {
        WriteInternalRam(_internalRamAddress, value);
        IncrementInternalRamAddress();
    }

    private byte ReadInternalRam(int address) =>
        programRam.Read(externalProgramRamSize + (address & InternalRamAddressMask));

    private void WriteInternalRam(int address, byte value) =>
        programRam.Write(externalProgramRamSize + (address & InternalRamAddressMask), value);

    private void IncrementInternalRamAddress()
    {
        if (_internalRamAutoIncrement && _internalRamAddress < InternalRamAddressMask)
        {
            _internalRamAddress++;
        }
    }

    private void ClockAudioChannel()
    {
        var enabledChannels = ((ReadInternalRam(0x7F) >> 4) & 7) + 1;
        if (_currentAudioChannel >= enabledChannels)
        {
            _currentAudioChannel = 0;
        }

        var channelAddress = 0x78 - (_currentAudioChannel * 8);
        var frequency =
            ReadInternalRam(channelAddress) |
            (ReadInternalRam(channelAddress + 2) << 8) |
            ((ReadInternalRam(channelAddress + 4) & 3) << 16);
        var phase =
            ReadInternalRam(channelAddress + 1) |
            (ReadInternalRam(channelAddress + 3) << 8) |
            (ReadInternalRam(channelAddress + 5) << 16);
        var waveLength = 256 - (ReadInternalRam(channelAddress + 4) & 0xFC);
        phase = (phase + frequency) % (waveLength << 16);
        WriteInternalRam(channelAddress + 1, (byte)phase);
        WriteInternalRam(channelAddress + 3, (byte)(phase >> 8));
        WriteInternalRam(channelAddress + 5, (byte)(phase >> 16));

        var sampleAddress =
            ((phase >> 16) + ReadInternalRam(channelAddress + 6)) & 0xFF;
        var packedSamples = ReadInternalRam(sampleAddress >> 1);
        var sample = (sampleAddress & 1) == 0
            ? packedSamples & 0x0F
            : packedSamples >> 4;
        var volume = ReadInternalRam(channelAddress + 7) & 0x0F;
        _serialAudioOutput = (short)((sample - 8) * volume);
        _currentAudioChannel = (byte)((_currentAudioChannel + 1) % enabledChannels);
    }
}
