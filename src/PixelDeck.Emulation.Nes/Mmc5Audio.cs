namespace PixelDeck.Emulation.Nes;

/// <summary>
/// MMC5's two pulse generators and 8-bit PCM DAC. The cartridge audio pins are
/// inverted relative to the 2A03 APU, so this source deliberately produces a
/// negative normalized signal for the shared PixelNES mixer.
/// </summary>
internal sealed class Mmc5Audio
{
    private const int ShortFramePeriod = 7_457;
    private const int LongFramePeriod = 7_458;
    private const float MaximumMixedLevel = (15 * 3 * 2) + 255;

    private readonly PulseChannel _pulseOne = new();
    private readonly PulseChannel _pulseTwo = new();

    private int _frameDivider = ShortFramePeriod;
    private bool _useLongFramePeriod;
    private bool _pcmReadMode;
    private bool _pcmIrqEnabled;
    private bool _pcmIrqPending;
    private byte _pcmOutput;

    public bool IrqPending => _pcmIrqEnabled && _pcmIrqPending;

    public float Output =>
        -((_pulseOne.Output * 3) + (_pulseTwo.Output * 3) + _pcmOutput) /
        MaximumMixedLevel;

    public void WriteRegister(ushort address, byte value)
    {
        switch (address)
        {
            case >= 0x5000 and <= 0x5003:
                _pulseOne.WriteRegister(address - 0x5000, value);
                break;
            case >= 0x5004 and <= 0x5007:
                _pulseTwo.WriteRegister(address - 0x5004, value);
                break;
            case 0x5010:
                _pcmReadMode = (value & 0x01) != 0;
                _pcmIrqEnabled = (value & 0x80) != 0;
                break;
            case 0x5011 when !_pcmReadMode:
                WritePcm(value);
                break;
            case 0x5015:
                _pulseOne.SetEnabled((value & 0x01) != 0);
                _pulseTwo.SetEnabled((value & 0x02) != 0);
                break;
        }
    }

    public byte ReadPcmStatus()
    {
        var result = (byte)(
            (_pcmReadMode ? 0x01 : 0) |
            (IrqPending ? 0x80 : 0));
        _pcmIrqPending = false;
        return result;
    }

    public byte ReadPulseStatus() =>
        (byte)(
            (_pulseOne.LengthCounter > 0 ? 0x01 : 0) |
            (_pulseTwo.LengthCounter > 0 ? 0x02 : 0));

    public void ObserveProgramRead(byte value)
    {
        if (_pcmReadMode)
        {
            WritePcm(value);
        }
    }

    public void ClockCpuCycle()
    {
        _pulseOne.ClockTimer();
        _pulseTwo.ClockTimer();

        _frameDivider--;
        if (_frameDivider > 0)
        {
            return;
        }

        _pulseOne.ClockFrameUnit();
        _pulseTwo.ClockFrameUnit();
        _frameDivider = _useLongFramePeriod ? LongFramePeriod : ShortFramePeriod;
        _useLongFramePeriod = !_useLongFramePeriod;
    }

    public void SaveState(BinaryWriter writer)
    {
        _pulseOne.SaveState(writer);
        _pulseTwo.SaveState(writer);
        writer.Write(_frameDivider);
        writer.Write(_useLongFramePeriod);
        writer.Write(_pcmReadMode);
        writer.Write(_pcmIrqEnabled);
        writer.Write(_pcmIrqPending);
        writer.Write(_pcmOutput);
    }

    public void LoadState(BinaryReader reader)
    {
        _pulseOne.LoadState(reader);
        _pulseTwo.LoadState(reader);
        _frameDivider = reader.ReadInt32();
        _useLongFramePeriod = reader.ReadBoolean();
        _pcmReadMode = reader.ReadBoolean();
        _pcmIrqEnabled = reader.ReadBoolean();
        _pcmIrqPending = reader.ReadBoolean();
        _pcmOutput = reader.ReadByte();

        if (_frameDivider is < 1 or > LongFramePeriod)
        {
            throw new InvalidDataException("The save state contains invalid MMC5 audio timing.");
        }
    }

    private void WritePcm(byte value)
    {
        if (value == 0)
        {
            _pcmIrqPending = true;
            return;
        }

        _pcmOutput = value;
        _pcmIrqPending = false;
    }

    private sealed class PulseChannel
    {
        private static readonly byte[][] DutySequences =
        [
            [0, 1, 0, 0, 0, 0, 0, 0],
            [0, 1, 1, 0, 0, 0, 0, 0],
            [0, 1, 1, 1, 1, 0, 0, 0],
            [1, 0, 0, 1, 1, 1, 1, 1]
        ];

        private static readonly byte[] LengthTable =
        [
            10, 254, 20, 2, 40, 4, 80, 6,
            160, 8, 60, 10, 14, 12, 26, 14,
            12, 16, 24, 18, 48, 20, 96, 22,
            192, 24, 72, 26, 16, 28, 32, 30
        ];

        private bool _enabled;
        private byte _duty;
        private bool _lengthHalt;
        private bool _constantVolume;
        private byte _envelopePeriod;
        private bool _envelopeStart;
        private byte _envelopeDivider;
        private byte _envelopeDecay;
        private ushort _timerPeriod;
        private ushort _timer;
        private byte _sequence;

        public byte LengthCounter { get; private set; }

        public byte Output =>
            _enabled &&
            LengthCounter > 0 &&
            DutySequences[_duty][_sequence] != 0
                ? (_constantVolume ? _envelopePeriod : _envelopeDecay)
                : (byte)0;

        public void WriteRegister(int register, byte value)
        {
            switch (register)
            {
                case 0:
                    _duty = (byte)(value >> 6);
                    _lengthHalt = (value & 0x20) != 0;
                    _constantVolume = (value & 0x10) != 0;
                    _envelopePeriod = (byte)(value & 0x0F);
                    break;
                case 1:
                    // MMC5 pulse channels do not contain sweep units.
                    break;
                case 2:
                    _timerPeriod = (ushort)((_timerPeriod & 0x0700) | value);
                    break;
                case 3:
                    _timerPeriod =
                        (ushort)((_timerPeriod & 0x00FF) | ((value & 0x07) << 8));
                    if (_enabled)
                    {
                        LengthCounter = LengthTable[value >> 3];
                    }

                    _sequence = 0;
                    _envelopeStart = true;
                    break;
            }
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled)
            {
                LengthCounter = 0;
            }
        }

        public void ClockTimer()
        {
            if (_timer == 0)
            {
                _timer = (ushort)(((_timerPeriod + 1) * 2) - 1);
                _sequence = (byte)((_sequence + 1) & 0x07);
            }
            else
            {
                _timer--;
            }
        }

        public void ClockFrameUnit()
        {
            if (!_lengthHalt && LengthCounter > 0)
            {
                LengthCounter--;
            }

            if (_envelopeStart)
            {
                _envelopeStart = false;
                _envelopeDecay = 15;
                _envelopeDivider = _envelopePeriod;
            }
            else if (_envelopeDivider > 0)
            {
                _envelopeDivider--;
            }
            else
            {
                _envelopeDivider = _envelopePeriod;
                if (_envelopeDecay > 0)
                {
                    _envelopeDecay--;
                }
                else if (_lengthHalt)
                {
                    _envelopeDecay = 15;
                }
            }
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(_enabled);
            writer.Write(_duty);
            writer.Write(_lengthHalt);
            writer.Write(_constantVolume);
            writer.Write(_envelopePeriod);
            writer.Write(_envelopeStart);
            writer.Write(_envelopeDivider);
            writer.Write(_envelopeDecay);
            writer.Write(_timerPeriod);
            writer.Write(_timer);
            writer.Write(_sequence);
            writer.Write(LengthCounter);
        }

        public void LoadState(BinaryReader reader)
        {
            _enabled = reader.ReadBoolean();
            _duty = reader.ReadByte();
            _lengthHalt = reader.ReadBoolean();
            _constantVolume = reader.ReadBoolean();
            _envelopePeriod = reader.ReadByte();
            _envelopeStart = reader.ReadBoolean();
            _envelopeDivider = reader.ReadByte();
            _envelopeDecay = reader.ReadByte();
            _timerPeriod = reader.ReadUInt16();
            _timer = reader.ReadUInt16();
            _sequence = reader.ReadByte();
            LengthCounter = reader.ReadByte();

            if (_duty > 3 ||
                _envelopePeriod > 15 ||
                _envelopeDivider > 15 ||
                _envelopeDecay > 15 ||
                _timerPeriod > 0x07FF ||
                _timer > 4_095 ||
                _sequence > 7)
            {
                throw new InvalidDataException(
                    "The save state contains invalid MMC5 pulse-channel state.");
            }
        }
    }
}
