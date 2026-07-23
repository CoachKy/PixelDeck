namespace PixelDeck.Emulation.Nes;

internal sealed class NesApu
{
    public const int SampleRate = 48_000;
    private const int CpuClockRate = 1_789_773;

    private static readonly byte[] LengthTable =
    [
        10, 254, 20, 2, 40, 4, 80, 6,
        160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22,
        192, 24, 72, 26, 16, 28, 32, 30
    ];

    private readonly PulseChannel _pulseOne = new(isFirstChannel: true);
    private readonly PulseChannel _pulseTwo = new(isFirstChannel: false);
    private readonly TriangleChannel _triangle = new();
    private readonly NoiseChannel _noise = new();
    private readonly DmcChannel _dmc = new();
    private readonly object _sampleLock = new();
    private readonly float[] _samples = new float[16_384];
    private int _sampleReadIndex;
    private int _sampleWriteIndex;
    private int _sampleCount;
    private long _droppedSampleCount;
    private long _cpuCycle;
    private int _frameCycle;
    private int _frameCounterWriteDelay;
    private int _pendingFrameCounterValue = -1;
    private int _blockFrameCounterTick;
    private bool _fiveStepMode;
    private bool _frameIrqInhibit;
    private bool _frameIrqPending;
    private int _sampleAccumulator;
    private double _highPassPreviousInput;
    private double _highPassPreviousOutput;
    private double _lowPassPreviousOutput;

    public bool IrqPending => _frameIrqPending || _dmc.IrqPending;

    public bool DmcDmaPending => _dmc.DmaPending;

    public ushort DmcDmaAddress => _dmc.DmaAddress;

    public int BufferedSampleCount
    {
        get
        {
            lock (_sampleLock)
            {
                return _sampleCount;
            }
        }
    }

    public long DroppedSampleCount => Interlocked.Read(ref _droppedSampleCount);

    public void Reset(bool softReset)
    {
        _pulseOne.SetEnabled(false);
        _pulseTwo.SetEnabled(false);
        _triangle.SetEnabled(false);
        _noise.SetEnabled(false);
        _dmc.SetEnabled(false);

        if (!softReset)
        {
            _cpuCycle = 0;
            _fiveStepMode = false;
            _sampleAccumulator = 0;
            _highPassPreviousInput = 0;
            _highPassPreviousOutput = 0;
            _lowPassPreviousOutput = 0;
        }

        _frameCycle = 0;
        _frameCounterWriteDelay = 4;
        _pendingFrameCounterValue = _fiveStepMode ? 0x80 : 0x00;
        _blockFrameCounterTick = 0;
        _frameIrqPending = false;
        _frameIrqInhibit = false;
        ClearSamples();
    }

    public void Clock(int cpuCycles)
    {
        for (var cycle = 0; cycle < cpuCycles; cycle++)
        {
            _cpuCycle++;
            _frameCycle++;

            _triangle.ClockTimer();
            _noise.ClockTimer();
            _dmc.ClockTimer();

            if ((_cpuCycle & 1) == 0)
            {
                _pulseOne.ClockTimer();
                _pulseTwo.ClockTimer();
            }

            ClockFrameCounter();

            _sampleAccumulator += SampleRate;
            if (_sampleAccumulator >= CpuClockRate)
            {
                _sampleAccumulator -= CpuClockRate;
                EnqueueSample(CreateMixedSample());
            }
        }
    }

    public byte ReadStatus()
    {
        var status = (byte)0;
        if (_pulseOne.LengthCounter > 0) status |= 0x01;
        if (_pulseTwo.LengthCounter > 0) status |= 0x02;
        if (_triangle.LengthCounter > 0) status |= 0x04;
        if (_noise.LengthCounter > 0) status |= 0x08;
        if (_dmc.BytesRemaining > 0) status |= 0x10;
        if (_frameIrqPending) status |= 0x40;
        if (_dmc.IrqPending) status |= 0x80;
        _frameIrqPending = false;
        return status;
    }

    public void WriteRegister(ushort address, byte value)
    {
        switch (address)
        {
            case >= 0x4000 and <= 0x4003:
                _pulseOne.WriteRegister(address - 0x4000, value);
                break;
            case >= 0x4004 and <= 0x4007:
                _pulseTwo.WriteRegister(address - 0x4004, value);
                break;
            case >= 0x4008 and <= 0x400B:
                _triangle.WriteRegister(address - 0x4008, value);
                break;
            case >= 0x400C and <= 0x400F:
                _noise.WriteRegister(address - 0x400C, value);
                break;
            case >= 0x4010 and <= 0x4013:
                _dmc.WriteRegister(address - 0x4010, value);
                break;
            case 0x4015:
                _pulseOne.SetEnabled((value & 0x01) != 0);
                _pulseTwo.SetEnabled((value & 0x02) != 0);
                _triangle.SetEnabled((value & 0x04) != 0);
                _noise.SetEnabled((value & 0x08) != 0);
                _dmc.SetEnabled((value & 0x10) != 0);
                break;
            case 0x4017:
                _frameIrqInhibit = (value & 0x40) != 0;
                if (_frameIrqInhibit)
                {
                    _frameIrqPending = false;
                }

                _pendingFrameCounterValue = value;
                var writeCycleIsOdd = ((_cpuCycle + 1) & 1) != 0;
                // The write itself is followed by this cycle's APU clock, so
                // one extra count produces the hardware's 3/4-cycle delay.
                _frameCounterWriteDelay = writeCycleIsOdd ? 5 : 4;

                break;
        }
    }

    public int ReadSamples(Span<float> destination)
    {
        lock (_sampleLock)
        {
            var read = Math.Min(destination.Length, _sampleCount);
            for (var index = 0; index < read; index++)
            {
                destination[index] = _samples[_sampleReadIndex];
                _sampleReadIndex = (_sampleReadIndex + 1) % _samples.Length;
            }

            _sampleCount -= read;
            return read;
        }
    }

    public void ClearSamples()
    {
        lock (_sampleLock)
        {
            _sampleReadIndex = 0;
            _sampleWriteIndex = 0;
            _sampleCount = 0;
        }
    }

    public void CompleteDmcDma(byte value) => _dmc.CompleteDma(value);

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(_cpuCycle);
        writer.Write(_frameCycle);
        writer.Write(_frameCounterWriteDelay);
        writer.Write(_pendingFrameCounterValue);
        writer.Write(_blockFrameCounterTick);
        writer.Write(_fiveStepMode);
        writer.Write(_frameIrqInhibit);
        writer.Write(_frameIrqPending);
        writer.Write(_sampleAccumulator);
        writer.Write(_highPassPreviousInput);
        writer.Write(_highPassPreviousOutput);
        writer.Write(_lowPassPreviousOutput);
        _pulseOne.SaveState(writer);
        _pulseTwo.SaveState(writer);
        _triangle.SaveState(writer);
        _noise.SaveState(writer);
        _dmc.SaveState(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        _cpuCycle = reader.ReadInt64();
        _frameCycle = reader.ReadInt32();
        _frameCounterWriteDelay = reader.ReadInt32();
        _pendingFrameCounterValue = reader.ReadInt32();
        _blockFrameCounterTick = reader.ReadInt32();
        _fiveStepMode = reader.ReadBoolean();
        _frameIrqInhibit = reader.ReadBoolean();
        _frameIrqPending = reader.ReadBoolean();
        _sampleAccumulator = reader.ReadInt32();
        _highPassPreviousInput = reader.ReadDouble();
        _highPassPreviousOutput = reader.ReadDouble();
        _lowPassPreviousOutput = reader.ReadDouble();
        _pulseOne.LoadState(reader);
        _pulseTwo.LoadState(reader);
        _triangle.LoadState(reader);
        _noise.LoadState(reader);
        _dmc.LoadState(reader);
        ClearSamples();
    }

    private void ClockFrameCounter()
    {
        if (!_fiveStepMode)
        {
            switch (_frameCycle)
            {
                case 7_456:
                case 22_370:
                    ClockFrameUnits(halfFrame: false);
                    break;
                case 14_912:
                    ClockFrameUnits(halfFrame: true);
                    break;
                case 29_827:
                case 29_828:
                case 29_829:
                    if (!_frameIrqInhibit)
                    {
                        _frameIrqPending = true;
                    }

                    if (_frameCycle == 29_828)
                    {
                        ClockFrameUnits(halfFrame: true);
                    }
                    break;
                case 29_830:
                    _frameCycle = 0;
                    break;
            }
        }
        else
        {
            switch (_frameCycle)
            {
                case 7_456:
                case 22_370:
                    ClockFrameUnits(halfFrame: false);
                    break;
                case 14_912:
                case 37_280:
                    ClockFrameUnits(halfFrame: true);
                    break;
                case 37_282:
                    _frameCycle = 0;
                    break;
            }
        }

        if (_pendingFrameCounterValue >= 0 &&
            --_frameCounterWriteDelay == 0)
        {
            _fiveStepMode = (_pendingFrameCounterValue & 0x80) != 0;
            _pendingFrameCounterValue = -1;
            _frameCycle = 0;

            if (_fiveStepMode && _blockFrameCounterTick == 0)
            {
                ClockQuarterFrame();
                ClockHalfFrame();
                _blockFrameCounterTick = 2;
            }
        }

        if (_blockFrameCounterTick > 0)
        {
            _blockFrameCounterTick--;
        }
    }

    private void ClockFrameUnits(bool halfFrame)
    {
        if (_blockFrameCounterTick != 0)
        {
            return;
        }

        ClockQuarterFrame();
        if (halfFrame)
        {
            ClockHalfFrame();
        }

        _blockFrameCounterTick = 2;
    }

    private void ClockQuarterFrame()
    {
        _pulseOne.ClockEnvelope();
        _pulseTwo.ClockEnvelope();
        _triangle.ClockLinearCounter();
        _noise.ClockEnvelope();
    }

    private void ClockHalfFrame()
    {
        _pulseOne.ClockLengthAndSweep();
        _pulseTwo.ClockLengthAndSweep();
        _triangle.ClockLength();
        _noise.ClockLength();
    }

    private float CreateMixedSample()
    {
        var pulseOne = _pulseOne.Output;
        var pulseTwo = _pulseTwo.Output;
        var triangle = _triangle.Output;
        var noise = _noise.Output;
        var dmc = _dmc.Output;
        var pulseSum = pulseOne + pulseTwo;
        var pulseOutput = pulseSum == 0 ? 0 : 95.88 / ((8128.0 / pulseSum) + 100.0);
        var tndInput = (triangle / 8227.0) + (noise / 12241.0) + (dmc / 22638.0);
        var tndOutput = tndInput == 0 ? 0 : 159.79 / ((1.0 / tndInput) + 100.0);
        var mixed = pulseOutput + tndOutput;

        const double highPassCutoff = 90.0;
        var highPassRc = 1.0 / (2.0 * Math.PI * highPassCutoff);
        var samplePeriod = 1.0 / SampleRate;
        var highPassAlpha = highPassRc / (highPassRc + samplePeriod);
        var highPassed = highPassAlpha * (_highPassPreviousOutput + mixed - _highPassPreviousInput);
        _highPassPreviousInput = mixed;
        _highPassPreviousOutput = highPassed;

        const double lowPassCutoff = 14_000.0;
        var lowPassRc = 1.0 / (2.0 * Math.PI * lowPassCutoff);
        var lowPassAlpha = samplePeriod / (lowPassRc + samplePeriod);
        _lowPassPreviousOutput += lowPassAlpha * (highPassed - _lowPassPreviousOutput);
        return SoftLimit(_lowPassPreviousOutput * 1.8);
    }

    private void EnqueueSample(float sample)
    {
        lock (_sampleLock)
        {
            if (_sampleCount == _samples.Length)
            {
                _sampleReadIndex = (_sampleReadIndex + 1) % _samples.Length;
                _sampleCount--;
                Interlocked.Increment(ref _droppedSampleCount);
            }

            _samples[_sampleWriteIndex] = sample;
            _sampleWriteIndex = (_sampleWriteIndex + 1) % _samples.Length;
            _sampleCount++;
        }
    }

    private static float SoftLimit(double sample)
    {
        const double knee = 0.9;
        var magnitude = Math.Abs(sample);
        if (magnitude <= knee)
        {
            return (float)sample;
        }

        var overKnee = magnitude - knee;
        var limited = knee + ((1.0 - knee) * overKnee / (overKnee + (1.0 - knee)));
        return (float)Math.CopySign(limited, sample);
    }

    private sealed class PulseChannel(bool isFirstChannel)
    {
        private static readonly byte[][] DutySequences =
        [
            [0, 1, 0, 0, 0, 0, 0, 0],
            [0, 1, 1, 0, 0, 0, 0, 0],
            [0, 1, 1, 1, 1, 0, 0, 0],
            [1, 0, 0, 1, 1, 1, 1, 1]
        ];

        private bool _enabled;
        private byte _duty;
        private bool _lengthHalt;
        private bool _constantVolume;
        private byte _envelopePeriod;
        private bool _envelopeStart;
        private byte _envelopeDivider;
        private byte _envelopeDecay;
        private bool _sweepEnabled;
        private byte _sweepPeriod;
        private bool _sweepNegate;
        private byte _sweepShift;
        private bool _sweepReload;
        private byte _sweepDivider;
        private ushort _timerPeriod;
        private ushort _timer;
        private byte _sequence;

        public byte LengthCounter { get; private set; }

        public byte Output
        {
            get
            {
                var target = SweepTarget;
                if (!_enabled || LengthCounter == 0 || _timerPeriod < 8 || target > 0x07FF || DutySequences[_duty][_sequence] == 0)
                {
                    return 0;
                }

                return _constantVolume ? _envelopePeriod : _envelopeDecay;
            }
        }

        private int SweepTarget
        {
            get
            {
                var change = _timerPeriod >> _sweepShift;
                return _sweepNegate
                    ? _timerPeriod - change - (isFirstChannel ? 1 : 0)
                    : _timerPeriod + change;
            }
        }

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
                    _sweepEnabled = (value & 0x80) != 0;
                    _sweepPeriod = (byte)((value >> 4) & 0x07);
                    _sweepNegate = (value & 0x08) != 0;
                    _sweepShift = (byte)(value & 0x07);
                    _sweepReload = true;
                    break;
                case 2:
                    _timerPeriod = (ushort)((_timerPeriod & 0x0700) | value);
                    break;
                case 3:
                    _timerPeriod = (ushort)((_timerPeriod & 0x00FF) | ((value & 0x07) << 8));
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
                _timer = _timerPeriod;
                _sequence = (byte)((_sequence + 1) & 0x07);
            }
            else
            {
                _timer--;
            }
        }

        public void ClockEnvelope()
        {
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
                if (_envelopeDecay > 0) _envelopeDecay--;
                else if (_lengthHalt) _envelopeDecay = 15;
            }
        }

        public void ClockLengthAndSweep()
        {
            if (!_lengthHalt && LengthCounter > 0)
            {
                LengthCounter--;
            }

            if (_sweepDivider == 0 && _sweepEnabled && _sweepShift > 0 && _timerPeriod >= 8 && SweepTarget <= 0x07FF)
            {
                _timerPeriod = (ushort)Math.Max(0, SweepTarget);
            }

            if (_sweepDivider == 0 || _sweepReload)
            {
                _sweepDivider = _sweepPeriod;
                _sweepReload = false;
            }
            else
            {
                _sweepDivider--;
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
            writer.Write(_sweepEnabled);
            writer.Write(_sweepPeriod);
            writer.Write(_sweepNegate);
            writer.Write(_sweepShift);
            writer.Write(_sweepReload);
            writer.Write(_sweepDivider);
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
            _sweepEnabled = reader.ReadBoolean();
            _sweepPeriod = reader.ReadByte();
            _sweepNegate = reader.ReadBoolean();
            _sweepShift = reader.ReadByte();
            _sweepReload = reader.ReadBoolean();
            _sweepDivider = reader.ReadByte();
            _timerPeriod = reader.ReadUInt16();
            _timer = reader.ReadUInt16();
            _sequence = reader.ReadByte();
            LengthCounter = reader.ReadByte();
        }
    }

    private sealed class TriangleChannel
    {
        private static readonly byte[] Sequence =
        [15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

        private bool _enabled;
        private bool _controlFlag;
        private byte _linearReloadValue;
        private byte _linearCounter;
        private bool _linearReloadFlag;
        private ushort _timerPeriod;
        private ushort _timer;
        private byte _sequence;

        public byte LengthCounter { get; private set; }

        public byte Output => _enabled && LengthCounter > 0 && _linearCounter > 0 && _timerPeriod > 1
            ? Sequence[_sequence]
            : (byte)0;

        public void WriteRegister(int register, byte value)
        {
            switch (register)
            {
                case 0:
                    _controlFlag = (value & 0x80) != 0;
                    _linearReloadValue = (byte)(value & 0x7F);
                    break;
                case 2:
                    _timerPeriod = (ushort)((_timerPeriod & 0x0700) | value);
                    break;
                case 3:
                    _timerPeriod = (ushort)((_timerPeriod & 0x00FF) | ((value & 0x07) << 8));
                    if (_enabled)
                    {
                        LengthCounter = LengthTable[value >> 3];
                    }

                    _linearReloadFlag = true;
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
                _timer = _timerPeriod;
                if (_enabled && LengthCounter > 0 && _linearCounter > 0 && _timerPeriod > 1)
                {
                    _sequence = (byte)((_sequence + 1) & 0x1F);
                }
            }
            else
            {
                _timer--;
            }
        }

        public void ClockLinearCounter()
        {
            if (_linearReloadFlag)
            {
                _linearCounter = _linearReloadValue;
            }
            else if (_linearCounter > 0)
            {
                _linearCounter--;
            }

            if (!_controlFlag)
            {
                _linearReloadFlag = false;
            }
        }

        public void ClockLength()
        {
            if (!_controlFlag && LengthCounter > 0)
            {
                LengthCounter--;
            }
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(_enabled);
            writer.Write(_controlFlag);
            writer.Write(_linearReloadValue);
            writer.Write(_linearCounter);
            writer.Write(_linearReloadFlag);
            writer.Write(_timerPeriod);
            writer.Write(_timer);
            writer.Write(_sequence);
            writer.Write(LengthCounter);
        }

        public void LoadState(BinaryReader reader)
        {
            _enabled = reader.ReadBoolean();
            _controlFlag = reader.ReadBoolean();
            _linearReloadValue = reader.ReadByte();
            _linearCounter = reader.ReadByte();
            _linearReloadFlag = reader.ReadBoolean();
            _timerPeriod = reader.ReadUInt16();
            _timer = reader.ReadUInt16();
            _sequence = reader.ReadByte();
            LengthCounter = reader.ReadByte();
        }
    }

    private sealed class NoiseChannel
    {
        private static readonly ushort[] PeriodTable =
        [4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1_016, 2_034, 4_068];

        private bool _enabled;
        private bool _lengthHalt;
        private bool _constantVolume;
        private byte _envelopePeriod;
        private bool _envelopeStart;
        private byte _envelopeDivider;
        private byte _envelopeDecay;
        private bool _mode;
        private ushort _timerPeriod = 4;
        private ushort _timer;
        private ushort _shiftRegister = 1;

        public byte LengthCounter { get; private set; }

        public byte Output => _enabled && LengthCounter > 0 && (_shiftRegister & 1) == 0
            ? _constantVolume ? _envelopePeriod : _envelopeDecay
            : (byte)0;

        public void WriteRegister(int register, byte value)
        {
            switch (register)
            {
                case 0:
                    _lengthHalt = (value & 0x20) != 0;
                    _constantVolume = (value & 0x10) != 0;
                    _envelopePeriod = (byte)(value & 0x0F);
                    break;
                case 2:
                    _mode = (value & 0x80) != 0;
                    _timerPeriod = PeriodTable[value & 0x0F];
                    break;
                case 3:
                    if (_enabled)
                    {
                        LengthCounter = LengthTable[value >> 3];
                    }

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
                _timer = _timerPeriod;
                var tap = _mode ? 6 : 1;
                var feedback = (_shiftRegister & 1) ^ ((_shiftRegister >> tap) & 1);
                _shiftRegister = (ushort)((_shiftRegister >> 1) | (feedback << 14));
            }
            else
            {
                _timer--;
            }
        }

        public void ClockEnvelope()
        {
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
                if (_envelopeDecay > 0) _envelopeDecay--;
                else if (_lengthHalt) _envelopeDecay = 15;
            }
        }

        public void ClockLength()
        {
            if (!_lengthHalt && LengthCounter > 0)
            {
                LengthCounter--;
            }
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(_enabled);
            writer.Write(_lengthHalt);
            writer.Write(_constantVolume);
            writer.Write(_envelopePeriod);
            writer.Write(_envelopeStart);
            writer.Write(_envelopeDivider);
            writer.Write(_envelopeDecay);
            writer.Write(_mode);
            writer.Write(_timerPeriod);
            writer.Write(_timer);
            writer.Write(_shiftRegister);
            writer.Write(LengthCounter);
        }

        public void LoadState(BinaryReader reader)
        {
            _enabled = reader.ReadBoolean();
            _lengthHalt = reader.ReadBoolean();
            _constantVolume = reader.ReadBoolean();
            _envelopePeriod = reader.ReadByte();
            _envelopeStart = reader.ReadBoolean();
            _envelopeDivider = reader.ReadByte();
            _envelopeDecay = reader.ReadByte();
            _mode = reader.ReadBoolean();
            _timerPeriod = reader.ReadUInt16();
            _timer = reader.ReadUInt16();
            _shiftRegister = reader.ReadUInt16();
            LengthCounter = reader.ReadByte();
        }
    }

    private sealed class DmcChannel
    {
        private static readonly ushort[] RateTable =
        [428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54];

        private bool _enabled;
        private bool _irqEnabled;
        private bool _loop;
        private ushort _timerPeriod = 428;
        private ushort _timer;
        private byte _output;
        private byte _sampleAddress;
        private byte _sampleLength;
        private ushort _currentAddress;
        private ushort _bytesRemaining;
        private int _sampleBuffer = -1;
        private byte _shiftRegister;
        private byte _bitsRemaining = 8;
        private bool _silence = true;
        private bool _dmaPending;

        public bool IrqPending { get; private set; }

        public bool DmaPending => _dmaPending;

        public ushort DmaAddress => _currentAddress;

        public ushort BytesRemaining => _bytesRemaining;

        public byte Output => _output;

        public void WriteRegister(int register, byte value)
        {
            switch (register)
            {
                case 0:
                    _irqEnabled = (value & 0x80) != 0;
                    _loop = (value & 0x40) != 0;
                    _timerPeriod = RateTable[value & 0x0F];
                    if (!_irqEnabled)
                    {
                        IrqPending = false;
                    }

                    break;
                case 1:
                    _output = (byte)(value & 0x7F);
                    break;
                case 2:
                    _sampleAddress = value;
                    break;
                case 3:
                    _sampleLength = value;
                    break;
            }
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            IrqPending = false;
            if (!enabled)
            {
                _bytesRemaining = 0;
                _dmaPending = false;
            }
            else if (_bytesRemaining == 0)
            {
                RestartSample();
            }
        }

        public void ClockTimer()
        {
            FillSampleBuffer();
            if (_timer > 0)
            {
                _timer--;
                return;
            }

            _timer = (ushort)(_timerPeriod - 1);
            if (!_silence)
            {
                if ((_shiftRegister & 1) != 0)
                {
                    if (_output <= 125) _output += 2;
                }
                else if (_output >= 2)
                {
                    _output -= 2;
                }
            }

            _shiftRegister >>= 1;
            if (--_bitsRemaining != 0)
            {
                return;
            }

            _bitsRemaining = 8;
            if (_sampleBuffer < 0)
            {
                _silence = true;
            }
            else
            {
                _silence = false;
                _shiftRegister = (byte)_sampleBuffer;
                _sampleBuffer = -1;
            }
        }

        public void CompleteDma(byte value)
        {
            if (!_dmaPending)
            {
                return;
            }

            _dmaPending = false;
            _sampleBuffer = value;
            _currentAddress++;
            if (_currentAddress == 0)
            {
                _currentAddress = 0x8000;
            }

            _bytesRemaining--;
            if (_bytesRemaining != 0)
            {
                return;
            }

            if (_loop)
            {
                RestartSample();
            }
            else if (_irqEnabled)
            {
                IrqPending = true;
            }
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(_enabled);
            writer.Write(_irqEnabled);
            writer.Write(_loop);
            writer.Write(_timerPeriod);
            writer.Write(_timer);
            writer.Write(_output);
            writer.Write(_sampleAddress);
            writer.Write(_sampleLength);
            writer.Write(_currentAddress);
            writer.Write(_bytesRemaining);
            writer.Write(_sampleBuffer);
            writer.Write(_shiftRegister);
            writer.Write(_bitsRemaining);
            writer.Write(_silence);
            writer.Write(_dmaPending);
            writer.Write(IrqPending);
        }

        public void LoadState(BinaryReader reader)
        {
            _enabled = reader.ReadBoolean();
            _irqEnabled = reader.ReadBoolean();
            _loop = reader.ReadBoolean();
            _timerPeriod = reader.ReadUInt16();
            _timer = reader.ReadUInt16();
            _output = reader.ReadByte();
            _sampleAddress = reader.ReadByte();
            _sampleLength = reader.ReadByte();
            _currentAddress = reader.ReadUInt16();
            _bytesRemaining = reader.ReadUInt16();
            _sampleBuffer = reader.ReadInt32();
            _shiftRegister = reader.ReadByte();
            _bitsRemaining = reader.ReadByte();
            _silence = reader.ReadBoolean();
            _dmaPending = reader.ReadBoolean();
            IrqPending = reader.ReadBoolean();
        }

        private void FillSampleBuffer()
        {
            if (_sampleBuffer >= 0 || _bytesRemaining == 0 || _dmaPending)
            {
                return;
            }

            _dmaPending = true;
        }

        private void RestartSample()
        {
            _currentAddress = (ushort)(0xC000 | (_sampleAddress << 6));
            _bytesRemaining = (ushort)((_sampleLength << 4) | 1);
        }
    }
}
