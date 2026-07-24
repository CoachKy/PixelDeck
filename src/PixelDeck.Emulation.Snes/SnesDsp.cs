namespace PixelDeck.Emulation.Snes;

internal sealed class SnesDsp
{
    public const int SampleRate = 32_000;

    private static readonly int[] CounterRates =
    [
        0, 2048, 1536, 1280, 1024, 768, 640, 512,
        384, 320, 256, 192, 160, 128, 96, 80,
        64, 48, 40, 32, 24, 20, 16, 12,
        10, 8, 6, 5, 4, 3, 2, 1
    ];

    private static readonly int[] CounterOffsets =
    [
        0, 0, 1040, 536, 0, 1040, 536, 0,
        1040, 536, 0, 1040, 536, 0, 1040, 536,
        0, 1040, 536, 0, 1040, 536, 0, 1040,
        536, 0, 1040, 536, 0, 1040, 0, 0
    ];

    private readonly byte[] _ram;
    private readonly byte[] _registers = new byte[128];
    private readonly Voice[] _voices = Enumerable.Range(0, 8).Select(index => new Voice(index)).ToArray();
    private readonly short[,] _echoHistory = new short[2, 8];
    private readonly short[] _gaussianTable = new short[512];
    private readonly object _sampleLock = new();
    private readonly float[] _samples = new float[65_536];
    private int _sampleReadIndex;
    private int _sampleWriteIndex;
    private int _sampleCount;
    private long _droppedSampleCount;
    private int _cycle;
    private int _counter = 30_720;
    private ushort _noiseLfsr = 0x4000;
    private byte _keyOnPending;
    private int _echoOffset;
    private int _echoHistoryOffset;

    public SnesDsp(byte[] ram)
    {
        _ram = ram;
        _registers[0x6C] = 0xE0;
        ConstructGaussianTable();
    }

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

    public int ActiveVoiceCount => _voices.Count(voice => voice.Active);

    public long DroppedSampleCount => Interlocked.Read(ref _droppedSampleCount);

    public byte ReadRegister(byte address) => _registers[address & 0x7F];

    public void WriteRegister(byte address, byte value)
    {
        address &= 0x7F;
        switch (address)
        {
            case 0x4C:
                _keyOnPending |= value;
                _registers[address] = value;
                break;
            case 0x7C:
                _registers[address] = 0;
                break;
            default:
                _registers[address] = value;
                break;
        }
    }

    public void ClockCycle()
    {
        if (++_cycle < 32)
        {
            return;
        }

        _cycle = 0;
        GenerateSample();
    }

    public int ReadSamples(Span<float> destination)
    {
        lock (_sampleLock)
        {
            var read = Math.Min(destination.Length & ~1, _sampleCount & ~1);
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

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(_registers);
        writer.Write(_cycle);
        writer.Write(_counter);
        writer.Write(_noiseLfsr);
        writer.Write(_keyOnPending);
        writer.Write(_echoOffset);
        writer.Write(_echoHistoryOffset);
        for (var channel = 0; channel < 2; channel++)
        {
            for (var index = 0; index < 8; index++)
            {
                writer.Write(_echoHistory[channel, index]);
            }
        }

        foreach (var voice in _voices)
        {
            voice.SaveState(writer);
        }
    }

    public void LoadState(BinaryReader reader)
    {
        reader.ReadExactly(_registers);
        _cycle = reader.ReadInt32();
        _counter = reader.ReadInt32();
        _noiseLfsr = reader.ReadUInt16();
        _keyOnPending = reader.ReadByte();
        _echoOffset = reader.ReadInt32();
        _echoHistoryOffset = reader.ReadInt32();
        for (var channel = 0; channel < 2; channel++)
        {
            for (var index = 0; index < 8; index++)
            {
                _echoHistory[channel, index] = reader.ReadInt16();
            }
        }

        foreach (var voice in _voices)
        {
            voice.LoadState(reader);
        }

        ClearSamples();
    }

    private void GenerateSample()
    {
        var flags = _registers[0x6C];
        var reset = (flags & 0x80) != 0;
        var keyOff = _registers[0x5C];
        var pitchModulation = _registers[0x2D];
        var noiseEnable = _registers[0x3D];
        var echoEnable = _registers[0x4D];
        var mainLeft = 0;
        var mainRight = 0;
        var echoSendLeft = 0;
        var echoSendRight = 0;
        var previousVoiceOutput = 0;

        for (var index = 0; index < _voices.Length; index++)
        {
            var bit = 1 << index;
            var voice = _voices[index];
            if ((keyOff & bit) != 0)
            {
                voice.KeyOff();
            }

            if ((_keyOnPending & bit) != 0)
            {
                StartVoice(index, voice);
            }

            if (reset)
            {
                voice.Silence();
            }

            var registerBase = index << 4;
            var pitch = _registers[registerBase + 2] | ((_registers[registerBase + 3] & 0x3F) << 8);
            if (index > 0 && (pitchModulation & bit) != 0)
            {
                pitch += ((previousVoiceOutput >> 5) * pitch) >> 10;
                pitch = Math.Clamp(pitch, 0, 0x3FFF);
            }

            var output = voice.Render(
                pitch,
                (_registers[registerBase + 5] & 0x80) != 0,
                _registers[registerBase + 5],
                _registers[registerBase + 6],
                _registers[registerBase + 7],
                (noiseEnable & bit) != 0 ? unchecked((short)(_noiseLfsr << 1)) : null,
                PollCounter,
                Interpolate,
                DecodeBlock);
            previousVoiceOutput = output;
            _registers[registerBase + 8] = (byte)(voice.Envelope >> 4);
            _registers[registerBase + 9] = (byte)(output >> 8);

            var left = Clamp16((output * unchecked((sbyte)_registers[registerBase])) >> 7);
            var right = Clamp16((output * unchecked((sbyte)_registers[registerBase + 1])) >> 7);
            mainLeft = Clamp16(mainLeft + left);
            mainRight = Clamp16(mainRight + right);
            if ((echoEnable & bit) != 0)
            {
                echoSendLeft = Clamp16(echoSendLeft + left);
                echoSendRight = Clamp16(echoSendRight + right);
            }
        }

        _keyOnPending = 0;
        _registers[0x4C] = 0;

        var (echoLeft, echoRight) = ProcessEcho(echoSendLeft, echoSendRight);
        var outputLeft = Clamp16(
            ((mainLeft * unchecked((sbyte)_registers[0x0C])) >> 7) +
            ((echoLeft * unchecked((sbyte)_registers[0x2C])) >> 7));
        var outputRight = Clamp16(
            ((mainRight * unchecked((sbyte)_registers[0x1C])) >> 7) +
            ((echoRight * unchecked((sbyte)_registers[0x3C])) >> 7));

        if ((flags & 0xC0) != 0)
        {
            outputLeft = 0;
            outputRight = 0;
        }

        EnqueueStereo(outputLeft / 32768f, outputRight / 32768f);
        TickCounter();
        TickNoise(flags & 0x1F);
    }

    private void StartVoice(int index, Voice voice)
    {
        var registerBase = index << 4;
        var directoryAddress = (ushort)((_registers[0x5D] << 8) + (_registers[registerBase + 4] << 2));
        var startAddress = ReadWord(directoryAddress);
        var loopAddress = ReadWord((ushort)(directoryAddress + 2));
        voice.KeyOn(startAddress, loopAddress, DecodeBlock);
        _registers[0x7C] &= (byte)~(1 << index);
    }

    private bool DecodeBlock(Voice voice)
    {
        var address = voice.BlockAddress;
        var header = _ram[address];
        var shift = header >> 4;
        var filter = (header >> 2) & 3;

        for (var sampleIndex = 0; sampleIndex < 16; sampleIndex++)
        {
            var encoded = _ram[(ushort)(address + 1 + (sampleIndex >> 1))];
            var nibble = (sampleIndex & 1) == 0 ? encoded >> 4 : encoded & 0x0F;
            var sample = nibble >= 8 ? nibble - 16 : nibble;
            if (shift <= 12)
            {
                sample = (sample << shift) >> 1;
            }
            else
            {
                sample = sample < 0 ? -2048 : 0;
            }

            var previousOne = voice.DecoderPreviousOne;
            var previousTwo = voice.DecoderPreviousTwo >> 1;
            sample += filter switch
            {
                1 => (previousOne >> 1) + ((-previousOne) >> 5),
                2 => previousOne - previousTwo + (previousTwo >> 4) + ((previousOne * -3) >> 6),
                3 => previousOne - previousTwo + ((previousOne * -13) >> 7) + ((previousTwo * 3) >> 4),
                _ => 0
            };

            sample = Clamp16(sample);
            var decoded = unchecked((short)(sample << 1));
            voice.Enqueue(decoded);
            voice.DecoderPreviousTwo = voice.DecoderPreviousOne;
            voice.DecoderPreviousOne = decoded;
        }

        voice.BlockAddress = (ushort)(address + 9);
        if ((header & 1) == 0)
        {
            return true;
        }

        _registers[0x7C] |= (byte)(1 << voice.Index);
        if ((header & 2) != 0)
        {
            voice.BlockAddress = voice.LoopAddress;
            return true;
        }

        return false;
    }

    private (int Left, int Right) ProcessEcho(int sendLeft, int sendRight)
    {
        var echoAddress = (ushort)((_registers[0x6D] << 8) + _echoOffset);
        var inputLeft = unchecked((short)(_ram[echoAddress] | (_ram[(ushort)(echoAddress + 1)] << 8)));
        var inputRight = unchecked((short)(_ram[(ushort)(echoAddress + 2)] | (_ram[(ushort)(echoAddress + 3)] << 8)));
        _echoHistoryOffset = (_echoHistoryOffset + 1) & 7;
        _echoHistory[0, _echoHistoryOffset] = (short)(inputLeft >> 1);
        _echoHistory[1, _echoHistoryOffset] = (short)(inputRight >> 1);

        var filteredLeft = 0;
        var filteredRight = 0;
        for (var tap = 0; tap < 8; tap++)
        {
            var historyIndex = (_echoHistoryOffset + tap + 1) & 7;
            var coefficient = unchecked((sbyte)_registers[(tap << 4) | 0x0F]);
            filteredLeft += (_echoHistory[0, historyIndex] * coefficient) >> 6;
            filteredRight += (_echoHistory[1, historyIndex] * coefficient) >> 6;
        }

        filteredLeft = Clamp16(filteredLeft) & ~1;
        filteredRight = Clamp16(filteredRight) & ~1;
        var feedback = unchecked((sbyte)_registers[0x0D]);
        var writeLeft = Clamp16(sendLeft + ((filteredLeft * feedback) >> 7)) & ~1;
        var writeRight = Clamp16(sendRight + ((filteredRight * feedback) >> 7)) & ~1;
        if ((_registers[0x6C] & 0x20) == 0)
        {
            _ram[echoAddress] = (byte)writeLeft;
            _ram[(ushort)(echoAddress + 1)] = (byte)(writeLeft >> 8);
            _ram[(ushort)(echoAddress + 2)] = (byte)writeRight;
            _ram[(ushort)(echoAddress + 3)] = (byte)(writeRight >> 8);
        }

        var echoLength = (_registers[0x7D] & 0x0F) << 11;
        _echoOffset += 4;
        if (echoLength == 0 || _echoOffset >= echoLength)
        {
            _echoOffset = 0;
        }

        return (filteredLeft, filteredRight);
    }

    private void TickCounter()
    {
        if (_counter == 0)
        {
            _counter = 30_720;
        }

        _counter--;
    }

    private bool PollCounter(int rate)
    {
        if (rate == 0)
        {
            return false;
        }

        return (_counter + CounterOffsets[rate]) % CounterRates[rate] == 0;
    }

    private void TickNoise(int rate)
    {
        if (!PollCounter(rate))
        {
            return;
        }

        var feedback = ((_noiseLfsr << 13) ^ (_noiseLfsr << 14)) & 0x4000;
        _noiseLfsr = (ushort)(feedback | (_noiseLfsr >> 1));
    }

    private ushort ReadWord(ushort address) =>
        (ushort)(_ram[address] | (_ram[(ushort)(address + 1)] << 8));

    private void EnqueueStereo(float left, float right)
    {
        lock (_sampleLock)
        {
            while (_sampleCount > _samples.Length - 2)
            {
                _sampleReadIndex = (_sampleReadIndex + 2) % _samples.Length;
                _sampleCount -= 2;
                Interlocked.Add(ref _droppedSampleCount, 2);
            }

            _samples[_sampleWriteIndex] = left;
            _sampleWriteIndex = (_sampleWriteIndex + 1) % _samples.Length;
            _samples[_sampleWriteIndex] = right;
            _sampleWriteIndex = (_sampleWriteIndex + 1) % _samples.Length;
            _sampleCount += 2;
        }
    }

    private int Interpolate(Voice voice)
    {
        var offset = (voice.Phase >> 4) & 0xFF;
        var sample = (_gaussianTable[255 - offset] * voice.PreviousSample) >> 11;
        sample += (_gaussianTable[511 - offset] * voice.Peek(0)) >> 11;
        sample += (_gaussianTable[256 + offset] * voice.Peek(1)) >> 11;
        sample = unchecked((short)sample);
        sample += (_gaussianTable[offset] * voice.Peek(2)) >> 11;
        return Clamp16(sample) & ~1;
    }

    private void ConstructGaussianTable()
    {
        var source = new double[512];
        for (var index = 0; index < source.Length; index++)
        {
            var k = 0.5 + index;
            var s = Math.Sin(Math.PI * k * 1.280 / 1024);
            var t = (Math.Cos(Math.PI * k * 2.000 / 1023) - 1) * 0.50;
            var u = (Math.Cos(Math.PI * k * 4.000 / 1023) - 1) * 0.08;
            source[511 - index] = s * (t + u + 1.0) / k;
        }

        for (var phase = 0; phase < 128; phase++)
        {
            var sum = source[phase] + source[phase + 256] + source[511 - phase] + source[255 - phase];
            var scale = 2048.0 / sum;
            _gaussianTable[phase] = (short)(source[phase] * scale + 0.5);
            _gaussianTable[phase + 256] = (short)(source[phase + 256] * scale + 0.5);
            _gaussianTable[511 - phase] = (short)(source[511 - phase] * scale + 0.5);
            _gaussianTable[255 - phase] = (short)(source[255 - phase] * scale + 0.5);
        }
    }

    private static int Clamp16(int value) => Math.Clamp(value, short.MinValue, short.MaxValue);

    private sealed class Voice
    {
        private readonly short[] _decoded = new short[64];
        private int _readIndex;
        private int _writeIndex;
        private int _decodedCount;
        private bool _hasMoreBlocks;
        private EnvelopeMode _envelopeMode;
        private int _hiddenEnvelope;

        public Voice(int index)
        {
            Index = index;
        }

        public int Index { get; }

        public bool Active { get; private set; }

        public int Envelope { get; private set; }

        public int Phase { get; private set; }

        public short PreviousSample { get; private set; }

        public short DecoderPreviousOne { get; set; }

        public short DecoderPreviousTwo { get; set; }

        public ushort BlockAddress { get; set; }

        public ushort LoopAddress { get; private set; }

        public int KeyOnDelay { get; private set; }

        public void KeyOn(ushort startAddress, ushort loopAddress, Func<Voice, bool> decodeBlock)
        {
            Active = true;
            Envelope = 0;
            _hiddenEnvelope = 0;
            _envelopeMode = EnvelopeMode.Attack;
            Phase = 0;
            PreviousSample = 0;
            DecoderPreviousOne = 0;
            DecoderPreviousTwo = 0;
            BlockAddress = startAddress;
            LoopAddress = loopAddress;
            KeyOnDelay = 5;
            _readIndex = 0;
            _writeIndex = 0;
            _decodedCount = 0;
            _hasMoreBlocks = true;
            EnsureDecoded(decodeBlock);
        }

        public void KeyOff()
        {
            if (Active)
            {
                _envelopeMode = EnvelopeMode.Release;
            }
        }

        public void Silence()
        {
            Envelope = 0;
            _hiddenEnvelope = 0;
            _envelopeMode = EnvelopeMode.Release;
        }

        public int Render(
            int pitch,
            bool useAdsr,
            byte adsrOne,
            byte adsrTwo,
            byte gain,
            short? noise,
            Func<int, bool> pollCounter,
            Func<Voice, int> interpolate,
            Func<Voice, bool> decodeBlock)
        {
            if (!Active)
            {
                return 0;
            }

            if (KeyOnDelay > 0)
            {
                KeyOnDelay--;
                return 0;
            }

            var source = noise ?? interpolate(this);
            var output = (source * Envelope) >> 11;
            output &= ~1;
            RunEnvelope(useAdsr, adsrOne, adsrTwo, gain, pollCounter);
            Advance(pitch, decodeBlock);
            return output;
        }

        public void Enqueue(short sample)
        {
            if (_decodedCount == _decoded.Length)
            {
                return;
            }

            _decoded[_writeIndex] = sample;
            _writeIndex = (_writeIndex + 1) % _decoded.Length;
            _decodedCount++;
        }

        public short Peek(int offset)
        {
            if (offset < 0 || offset >= _decodedCount)
            {
                return 0;
            }

            return _decoded[(_readIndex + offset) % _decoded.Length];
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(Active);
            writer.Write(Envelope);
            writer.Write(Phase);
            writer.Write(PreviousSample);
            writer.Write(DecoderPreviousOne);
            writer.Write(DecoderPreviousTwo);
            writer.Write(BlockAddress);
            writer.Write(LoopAddress);
            writer.Write(KeyOnDelay);
            writer.Write(_readIndex);
            writer.Write(_writeIndex);
            writer.Write(_decodedCount);
            writer.Write(_hasMoreBlocks);
            writer.Write((byte)_envelopeMode);
            writer.Write(_hiddenEnvelope);
            foreach (var sample in _decoded)
            {
                writer.Write(sample);
            }
        }

        public void LoadState(BinaryReader reader)
        {
            Active = reader.ReadBoolean();
            Envelope = reader.ReadInt32();
            Phase = reader.ReadInt32();
            PreviousSample = reader.ReadInt16();
            DecoderPreviousOne = reader.ReadInt16();
            DecoderPreviousTwo = reader.ReadInt16();
            BlockAddress = reader.ReadUInt16();
            LoopAddress = reader.ReadUInt16();
            KeyOnDelay = reader.ReadInt32();
            _readIndex = reader.ReadInt32();
            _writeIndex = reader.ReadInt32();
            _decodedCount = reader.ReadInt32();
            _hasMoreBlocks = reader.ReadBoolean();
            _envelopeMode = (EnvelopeMode)reader.ReadByte();
            _hiddenEnvelope = reader.ReadInt32();
            for (var index = 0; index < _decoded.Length; index++)
            {
                _decoded[index] = reader.ReadInt16();
            }
        }

        private void Advance(int pitch, Func<Voice, bool> decodeBlock)
        {
            Phase += pitch;
            while (Phase >= 0x1000 && Active)
            {
                Phase -= 0x1000;
                if (_decodedCount == 0)
                {
                    Active = false;
                    Envelope = 0;
                    break;
                }

                PreviousSample = _decoded[_readIndex];
                _readIndex = (_readIndex + 1) % _decoded.Length;
                _decodedCount--;
                EnsureDecoded(decodeBlock);
                if (_decodedCount == 0 && !_hasMoreBlocks)
                {
                    Active = false;
                    Envelope = 0;
                }
            }
        }

        private void EnsureDecoded(Func<Voice, bool> decodeBlock)
        {
            while (_decodedCount <= _decoded.Length - 16 && _decodedCount < 32 && _hasMoreBlocks)
            {
                _hasMoreBlocks = decodeBlock(this);
            }
        }

        private void RunEnvelope(
            bool useAdsr,
            byte adsrOne,
            byte adsrTwo,
            byte gain,
            Func<int, bool> pollCounter)
        {
            var envelope = Envelope;
            if (_envelopeMode == EnvelopeMode.Release)
            {
                Envelope = Math.Max(0, envelope - 8);
                if (Envelope == 0)
                {
                    Active = false;
                }

                return;
            }

            int rate;
            var envelopeData = adsrTwo;
            if (useAdsr)
            {
                if (_envelopeMode >= EnvelopeMode.Decay)
                {
                    envelope--;
                    envelope -= envelope >> 8;
                    rate = adsrTwo & 0x1F;
                    if (_envelopeMode == EnvelopeMode.Decay)
                    {
                        rate = (((adsrOne >> 4) & 7) * 2) + 16;
                    }
                }
                else
                {
                    rate = ((adsrOne & 0x0F) * 2) + 1;
                    envelope += rate < 31 ? 0x20 : 0x400;
                }
            }
            else
            {
                envelopeData = gain;
                var mode = gain >> 5;
                if (mode < 4)
                {
                    Envelope = (gain & 0x7F) << 4;
                    _hiddenEnvelope = Envelope;
                    return;
                }

                rate = gain & 0x1F;
                switch (mode)
                {
                    case 4:
                        envelope -= 0x20;
                        break;
                    case 5:
                        envelope--;
                        envelope -= envelope >> 8;
                        break;
                    case 6:
                        envelope += 0x20;
                        break;
                    case 7:
                        envelope += _hiddenEnvelope >= 0x600 ? 0x08 : 0x20;
                        break;
                }
            }

            if ((envelope >> 8) == (envelopeData >> 5) && _envelopeMode == EnvelopeMode.Decay)
            {
                _envelopeMode = EnvelopeMode.Sustain;
            }

            _hiddenEnvelope = envelope;
            envelope = Math.Clamp(envelope, 0, 0x7FF);
            if (envelope == 0x7FF && _envelopeMode == EnvelopeMode.Attack)
            {
                _envelopeMode = EnvelopeMode.Decay;
            }

            if (pollCounter(rate))
            {
                Envelope = envelope;
            }
        }

        private enum EnvelopeMode : byte
        {
            Release,
            Attack,
            Decay,
            Sustain
        }
    }

}
