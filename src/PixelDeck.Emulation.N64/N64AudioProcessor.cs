using System.Buffers.Binary;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// High-level emulation of the Nintendo 64 audio microcode command list
/// (audio ABI 1, "aspMain"), the version shipped with early libultra titles
/// including Super Mario 64. Commands operate on a 4 KiB DMEM-style scratch
/// buffer; A_LOADBUFF/A_SAVEBUFF move PCM between RDRAM and the scratch.
/// </summary>
public sealed class N64AudioProcessor
{
    private const int ScratchSize = 0x1000;
    private const byte FlagInit = 0x01;
    private const byte FlagLoop = 0x02;
    private const byte FlagVolume = 0x04;
    private const byte FlagLeft = 0x02;
    private const byte FlagAux = 0x08;
    private const int ResamplePhaseCount = 64;
    private const int ResampleTapCount = 4;
    private const int ResampleCoefficientScale = 1 << 15;
    private static readonly short[] ResampleCoefficients = CreateResampleCoefficients();

    private readonly N64Memory _memory;
    private readonly byte[] _scratch = new byte[ScratchSize];
    private readonly uint[] _segments = new uint[16];
    private readonly short[] _codebook = new short[128];
    private readonly short[] _adpcmState = new short[16];
    private readonly short[] _residuals = new short[16];
    private readonly short[] _resampleState = new short[4];
    private readonly int[] _volume = new int[2];
    private readonly int[] _volumeTarget = new int[2];
    private readonly uint[] _volumeRate = new uint[2];
    private int _inAddress;
    private int _outAddress;
    private int _count;
    private int _dryRightAddress;
    private int _wetLeftAddress;
    private int _wetRightAddress;
    private int _dryGain;
    private int _wetGain;
    private uint _loopAddress;

    public N64AudioProcessor(N64Memory memory)
    {
        _memory = memory;
    }

    public long CommandsProcessed { get; private set; }

    public long UnsupportedCommands { get; private set; }

    public SortedDictionary<uint, long> UnsupportedCommandCounts { get; } = [];

    public void Execute(N64RspTask task)
    {
        var pointer = task.DataPointer & 0x7FFFFF;
        var end = pointer + Math.Min(task.DataSize, 0x10000);
        while (pointer + 8 <= end)
        {
            var w0 = _memory.ReadUInt32(pointer);
            var w1 = _memory.ReadUInt32(pointer + 4);
            pointer += 8;
            CommandsProcessed++;
            var flags = (byte)(w0 >> 16);
            switch (w0 >> 24)
            {
                case 0x00:
                    break;
                case 0x01:
                    Adpcm(flags, ResolveAddress(w1));
                    break;
                case 0x02:
                    ClearBuffer((int)(w0 & 0xFFFF), (int)(w1 & 0xFFFF));
                    break;
                case 0x03:
                    EnvelopeMixer(flags, ResolveAddress(w1));
                    break;
                case 0x04:
                    LoadBuffer(ResolveAddress(w1));
                    break;
                case 0x05:
                    Resample(flags, (int)(w0 & 0xFFFF), ResolveAddress(w1));
                    break;
                case 0x06:
                    SaveBuffer(ResolveAddress(w1));
                    break;
                case 0x07:
                    _segments[(w1 >> 24) & 0xF] = w1 & 0x00FFFFFF;
                    break;
                case 0x08:
                    SetBuffer(flags, w0, w1);
                    break;
                case 0x09:
                    SetVolume(flags, w0, w1);
                    break;
                case 0x0A:
                    MoveScratch((int)(w0 & 0xFFFF), (int)(w1 >> 16), (int)(w1 & 0xFFFF));
                    break;
                case 0x0B:
                    LoadCodebook((int)(w0 & 0xFFFF), ResolveAddress(w1));
                    break;
                case 0x0C:
                    Mix(unchecked((short)w0), (int)(w1 >> 16), (int)(w1 & 0xFFFF));
                    break;
                case 0x0D:
                    Interleave((int)(w1 >> 16), (int)(w1 & 0xFFFF));
                    break;
                case 0x0F:
                    _loopAddress = ResolveAddress(w1);
                    break;
                default:
                    UnsupportedCommands++;
                    UnsupportedCommandCounts[w0 >> 24] =
                        UnsupportedCommandCounts.GetValueOrDefault(w0 >> 24) + 1;
                    break;
            }
        }
    }

    internal void SaveState(BinaryWriter writer)
    {
        foreach (var value in _segments) writer.Write(value);
        foreach (var value in _codebook) writer.Write(value);
        foreach (var value in _volume) writer.Write(value);
        foreach (var value in _volumeTarget) writer.Write(value);
        foreach (var value in _volumeRate) writer.Write(value);
        writer.Write(_inAddress);
        writer.Write(_outAddress);
        writer.Write(_count);
        writer.Write(_dryRightAddress);
        writer.Write(_wetLeftAddress);
        writer.Write(_wetRightAddress);
        writer.Write(_dryGain);
        writer.Write(_wetGain);
        writer.Write(_loopAddress);
        writer.Write(_scratch);
    }

    internal void LoadState(BinaryReader reader)
    {
        for (var index = 0; index < _segments.Length; index++) _segments[index] = reader.ReadUInt32();
        for (var index = 0; index < _codebook.Length; index++) _codebook[index] = reader.ReadInt16();
        for (var index = 0; index < _volume.Length; index++) _volume[index] = reader.ReadInt32();
        for (var index = 0; index < _volumeTarget.Length; index++) _volumeTarget[index] = reader.ReadInt32();
        for (var index = 0; index < _volumeRate.Length; index++) _volumeRate[index] = reader.ReadUInt32();
        _inAddress = reader.ReadInt32();
        _outAddress = reader.ReadInt32();
        _count = reader.ReadInt32();
        _dryRightAddress = reader.ReadInt32();
        _wetLeftAddress = reader.ReadInt32();
        _wetRightAddress = reader.ReadInt32();
        _dryGain = reader.ReadInt32();
        _wetGain = reader.ReadInt32();
        _loopAddress = reader.ReadUInt32();
        reader.ReadExactly(_scratch);
    }

    private uint ResolveAddress(uint address) =>
        (_segments[(address >> 24) & 0xF] + (address & 0x00FFFFFF)) & (N64Memory.RdramSize - 1);

    private void SetBuffer(byte flags, uint w0, uint w1)
    {
        if ((flags & FlagAux) != 0)
        {
            _dryRightAddress = (int)(w0 & 0xFFFF);
            _wetLeftAddress = (int)(w1 >> 16);
            _wetRightAddress = (int)(w1 & 0xFFFF);
            return;
        }

        _inAddress = (int)(w0 & 0xFFFF);
        _outAddress = (int)(w1 >> 16);
        _count = (int)(w1 & 0xFFFF);
    }

    private void SetVolume(byte flags, uint w0, uint w1)
    {
        if ((flags & FlagAux) != 0)
        {
            _dryGain = unchecked((short)w0);
            _wetGain = unchecked((short)w1);
            return;
        }

        var channel = (flags & FlagLeft) != 0 ? 0 : 1;
        if ((flags & FlagVolume) != 0)
        {
            _volume[channel] = unchecked((short)w0);
            return;
        }

        _volumeTarget[channel] = unchecked((short)w0);
        _volumeRate[channel] = w1;
    }

    private void ClearBuffer(int offset, int count)
    {
        var span = ScratchSpan(offset, AlignUp(count, 16));
        span.Clear();
    }

    private void LoadBuffer(uint address)
    {
        CopyRdramToScratch(address, _inAddress, _count);
    }

    private void SaveBuffer(uint address)
    {
        CopyScratchToRdram(_outAddress, address, _count);
    }

    private void MoveScratch(int source, int destination, int count)
    {
        var from = ScratchSpan(source, AlignUp(count, 16));
        var to = ScratchSpan(destination, from.Length);
        from[..to.Length].CopyTo(to);
    }

    private void LoadCodebook(int byteCount, uint address)
    {
        var entries = Math.Min(byteCount / 2, _codebook.Length);
        for (var index = 0; index < entries; index++)
        {
            _codebook[index] = unchecked((short)ReadRdramUInt16(address + (uint)(index * 2)));
        }
    }

    private void Mix(short gain, int source, int destination)
    {
        var samples = AlignUp(_count, 32) / 2;
        for (var index = 0; index < samples; index++)
        {
            var input = ReadScratchInt16(source + (index * 2));
            var mixed = ReadScratchInt16(destination + (index * 2)) + ((input * gain) >> 15);
            WriteScratchInt16(destination + (index * 2), ClampToInt16(mixed));
        }
    }

    private void Interleave(int left, int right)
    {
        // count describes one channel; the interleaved output is twice as long.
        var samples = AlignUp(_count, 16) / 2;
        for (var index = 0; index < samples; index++)
        {
            WriteScratchInt16(_outAddress + (index * 4), ReadScratchInt16(left + (index * 2)));
            WriteScratchInt16(_outAddress + (index * 4) + 2, ReadScratchInt16(right + (index * 2)));
        }
    }

    private void Adpcm(byte flags, uint stateAddress)
    {
        if ((flags & FlagInit) != 0)
        {
            Array.Clear(_adpcmState);
        }
        else
        {
            var source = (flags & FlagLoop) != 0 ? _loopAddress : stateAddress;
            for (var index = 0; index < 16; index++)
            {
                _adpcmState[index] = unchecked((short)ReadRdramUInt16(source + (uint)(index * 2)));
            }
        }

        var frames = AlignUp(_count, 32) / 32;
        var sourceOffset = _inAddress;
        var destinationOffset = _outAddress;
        var previous1 = _adpcmState[15];
        var previous2 = _adpcmState[14];
        for (var frame = 0; frame < frames; frame++)
        {
            var header = ReadScratchByte(sourceOffset++);
            var scale = header >> 4;
            var predictor = Math.Min(header & 0xF, 7);
            for (var index = 0; index < 8; index++)
            {
                var packed = ReadScratchByte(sourceOffset++);
                _residuals[index * 2] = (short)(SignExtendNibble(packed >> 4) << scale);
                _residuals[(index * 2) + 1] = (short)(SignExtendNibble(packed & 0xF) << scale);
            }

            for (var group = 0; group < 2; group++)
            {
                var residuals = _residuals.AsSpan(group * 8, 8);
                DecodeAdpcmGroup(residuals, predictor, ref previous1, ref previous2, group);
                for (var index = 0; index < 8; index++)
                {
                    WriteScratchInt16(destinationOffset, _adpcmState[(group * 8) + index]);
                    destinationOffset += 2;
                }
            }
        }

        for (var index = 0; index < 16; index++)
        {
            WriteRdramUInt16(stateAddress + (uint)(index * 2), unchecked((ushort)_adpcmState[index]));
        }
    }

    private void DecodeAdpcmGroup(
        ReadOnlySpan<short> residuals,
        int predictor,
        ref short previous1,
        ref short previous2,
        int group)
    {
        // Codebook layout per predictor: 8 coefficients applied to sample[-2]
        // followed by 8 applied to sample[-1]. In-group contributions reuse the
        // sample[-1] response at the matching lag, which is exactly how the RSP
        // vectorizes the order-2 recursion.
        var bookBase = predictor * 16;
        for (var index = 0; index < 8; index++)
        {
            long accumulator = (long)residuals[index] << 11;
            accumulator += (long)_codebook[bookBase + index] * previous2;
            accumulator += (long)_codebook[bookBase + 8 + index] * previous1;
            for (var lag = 0; lag < index; lag++)
            {
                accumulator += (long)_codebook[bookBase + 8 + (index - 1 - lag)] * residuals[lag];
            }

            _adpcmState[(group * 8) + index] = ClampToInt16(accumulator >> 11);
        }

        previous2 = _adpcmState[(group * 8) + 6];
        previous1 = _adpcmState[(group * 8) + 7];
    }

    private void Resample(byte flags, int pitch, uint stateAddress)
    {
        var step = (uint)pitch << 1;
        uint position = 0;
        if ((flags & FlagInit) != 0)
        {
            Array.Clear(_resampleState);
        }
        else
        {
            for (var index = 0; index < 4; index++)
            {
                _resampleState[index] = unchecked((short)ReadRdramUInt16(stateAddress + (uint)(index * 2)));
            }

            position = ReadRdramUInt16(stateAddress + 8);
        }

        var outputSamples = AlignUp(_count, 16) / 2;
        var destination = _outAddress;
        for (var index = 0; index < outputSamples; index++)
        {
            var whole = (int)(position >> 16);
            var phase = (int)((position & 0xFFFF) >> 10);
            var coefficientOffset = phase * ResampleTapCount;
            long filtered = 0;
            for (var tap = 0; tap < ResampleTapCount; tap++)
            {
                filtered +=
                    (long)SampleForResample(whole - 1 + tap) *
                    ResampleCoefficients[coefficientOffset + tap];
            }

            WriteScratchInt16(destination, ClampToInt16(filtered >> 15));
            destination += 2;
            position += step;
        }

        var consumed = (int)(position >> 16);
        for (var index = 0; index < 4; index++)
        {
            _resampleState[index] = SampleForResample(consumed - 4 + index);
        }

        for (var index = 0; index < 4; index++)
        {
            WriteRdramUInt16(stateAddress + (uint)(index * 2), unchecked((ushort)_resampleState[index]));
        }

        WriteRdramUInt16(stateAddress + 8, (ushort)(position & 0xFFFF));
    }

    private short SampleForResample(int index) =>
        index < 4
            ? _resampleState[Math.Max(index, 0)]
            : ReadScratchInt16(_inAddress + ((index - 4) * 2));

    /// <summary>
    /// Builds a four-tap, 64-phase Lanczos polyphase filter. The N64 audio
    /// microcode also evaluates four neighboring samples from one of 64
    /// fractional phases. Generating the coefficients here keeps the filter
    /// independent while avoiding the aliasing and stair-step distortion of
    /// the previous two-sample linear interpolation.
    /// </summary>
    private static short[] CreateResampleCoefficients()
    {
        var result = new short[ResamplePhaseCount * ResampleTapCount];
        Span<double> weights = stackalloc double[ResampleTapCount];
        for (var phase = 0; phase < ResamplePhaseCount; phase++)
        {
            var fraction = phase / (double)ResamplePhaseCount;
            var sum = 0.0;
            for (var tap = 0; tap < ResampleTapCount; tap++)
            {
                // Samples are [x-1, x, x+1, x+2], so the integer position
                // corresponds to the second tap.
                var distance = (tap - 1) - fraction;
                var weight = Lanczos2(distance);
                weights[tap] = weight;
                sum += weight;
            }

            var quantizedSum = 0;
            var phaseOffset = phase * ResampleTapCount;
            for (var tap = 0; tap < ResampleTapCount; tap++)
            {
                var coefficient = (int)Math.Round(
                    (weights[tap] / sum) * ResampleCoefficientScale,
                    MidpointRounding.AwayFromZero);
                coefficient = Math.Clamp(coefficient, short.MinValue, short.MaxValue);
                result[phaseOffset + tap] = (short)coefficient;
                quantizedSum += coefficient;
            }

            // Preserve unity DC gain after Q15 quantization.
            var correction = ResampleCoefficientScale - quantizedSum;
            result[phaseOffset + 1] = (short)Math.Clamp(
                result[phaseOffset + 1] + correction,
                short.MinValue,
                short.MaxValue);
        }

        return result;
    }

    private static double Lanczos2(double value)
    {
        var magnitude = Math.Abs(value);
        if (magnitude < 1e-12)
        {
            return 1.0;
        }

        if (magnitude >= 2.0)
        {
            return 0.0;
        }

        var piValue = Math.PI * value;
        return (Math.Sin(piValue) / piValue) *
               (Math.Sin(piValue / 2.0) / (piValue / 2.0));
    }

    private void EnvelopeMixer(byte flags, uint stateAddress)
    {
        Span<uint> volumes = stackalloc uint[2];
        Span<int> targets = stackalloc int[2];
        Span<uint> rates = stackalloc uint[2];
        if ((flags & FlagInit) != 0)
        {
            volumes[0] = (uint)Math.Max(_volume[0], 0) << 16;
            volumes[1] = (uint)Math.Max(_volume[1], 0) << 16;
            targets[0] = _volumeTarget[0];
            targets[1] = _volumeTarget[1];
            rates[0] = _volumeRate[0];
            rates[1] = _volumeRate[1];
        }
        else
        {
            volumes[0] = ReadRdramUInt32(stateAddress);
            volumes[1] = ReadRdramUInt32(stateAddress + 4);
            targets[0] = unchecked((int)ReadRdramUInt32(stateAddress + 8));
            targets[1] = unchecked((int)ReadRdramUInt32(stateAddress + 12));
            rates[0] = ReadRdramUInt32(stateAddress + 16);
            rates[1] = ReadRdramUInt32(stateAddress + 20);
        }

        var samples = _count / 2;
        var position = 0;
        while (position < samples)
        {
            var chunk = Math.Min(8, samples - position);
            var gainLeft = (int)(volumes[0] >> 16);
            var gainRight = (int)(volumes[1] >> 16);
            for (var index = 0; index < chunk; index++)
            {
                var sampleOffset = (position + index) * 2;
                int input = ReadScratchInt16(_inAddress + sampleOffset);
                var left = (input * gainLeft) >> 15;
                var right = (input * gainRight) >> 15;
                MixInto(_outAddress + sampleOffset, (left * _dryGain) >> 15);
                MixInto(_dryRightAddress + sampleOffset, (right * _dryGain) >> 15);
                MixInto(_wetLeftAddress + sampleOffset, (left * _wetGain) >> 15);
                MixInto(_wetRightAddress + sampleOffset, (right * _wetGain) >> 15);
            }

            for (var channel = 0; channel < 2; channel++)
            {
                var ramped = (uint)(((ulong)volumes[channel] * rates[channel]) >> 16);
                var target = (uint)Math.Max(targets[channel], 0) << 16;
                volumes[channel] = rates[channel] >= 0x10000
                    ? Math.Min(ramped, target)
                    : Math.Max(ramped, target);
            }

            position += chunk;
        }

        WriteRdramUInt32(stateAddress, volumes[0]);
        WriteRdramUInt32(stateAddress + 4, volumes[1]);
        WriteRdramUInt32(stateAddress + 8, unchecked((uint)targets[0]));
        WriteRdramUInt32(stateAddress + 12, unchecked((uint)targets[1]));
        WriteRdramUInt32(stateAddress + 16, rates[0]);
        WriteRdramUInt32(stateAddress + 20, rates[1]);
    }

    private void MixInto(int scratchOffset, int value)
    {
        var mixed = ReadScratchInt16(scratchOffset) + value;
        WriteScratchInt16(scratchOffset, ClampToInt16(mixed));
    }

    private Span<byte> ScratchSpan(int offset, int count)
    {
        offset &= ScratchSize - 1;
        return _scratch.AsSpan(offset, Math.Clamp(count, 0, ScratchSize - offset));
    }

    private byte ReadScratchByte(int offset) => _scratch[offset & (ScratchSize - 1)];

    private short ReadScratchInt16(int offset)
    {
        offset &= ScratchSize - 1;
        return offset <= ScratchSize - 2
            ? BinaryPrimitives.ReadInt16BigEndian(_scratch.AsSpan(offset, 2))
            : (short)0;
    }

    private void WriteScratchInt16(int offset, short value)
    {
        offset &= ScratchSize - 1;
        if (offset <= ScratchSize - 2)
        {
            BinaryPrimitives.WriteInt16BigEndian(_scratch.AsSpan(offset, 2), value);
        }
    }

    private void CopyRdramToScratch(uint address, int scratchOffset, int count)
    {
        var destination = ScratchSpan(scratchOffset, count);
        var source = RdramSpan(address, destination.Length);
        source.CopyTo(destination[..source.Length]);
    }

    private void CopyScratchToRdram(int scratchOffset, uint address, int count)
    {
        var source = ScratchSpan(scratchOffset, count);
        var destination = RdramSpan(address, source.Length);
        source[..destination.Length].CopyTo(destination);
    }

    private Span<byte> RdramSpan(uint address, int count)
    {
        var offset = (int)(address & (N64Memory.RdramSize - 1));
        return _memory.Rdram.AsSpan(offset, Math.Clamp(count, 0, N64Memory.RdramSize - offset));
    }

    private ushort ReadRdramUInt16(uint address)
    {
        var span = RdramSpan(address, 2);
        return span.Length == 2 ? BinaryPrimitives.ReadUInt16BigEndian(span) : (ushort)0;
    }

    private void WriteRdramUInt16(uint address, ushort value)
    {
        var span = RdramSpan(address, 2);
        if (span.Length == 2)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span, value);
        }
    }

    private uint ReadRdramUInt32(uint address)
    {
        var span = RdramSpan(address, 4);
        return span.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(span) : 0u;
    }

    private void WriteRdramUInt32(uint address, uint value)
    {
        var span = RdramSpan(address, 4);
        if (span.Length == 4)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span, value);
        }
    }

    private static int SignExtendNibble(int value) => (value << 28) >> 28;

    private static int AlignUp(int value, int alignment) =>
        (value + (alignment - 1)) & -alignment;

    private static short ClampToInt16(long value) =>
        (short)Math.Clamp(value, short.MinValue, short.MaxValue);
}
