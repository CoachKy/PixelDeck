using System.Buffers.Binary;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// High-level emulation of the Nintendo 64 audio microcode command list
/// (audio ABI 1, "aspMain"), the version shipped with early libultra titles
/// including Super Mario 64. Commands operate on a 4 KiB DMEM-style scratch
/// buffer; A_LOADBUFF/A_SAVEBUFF move PCM between RDRAM and the scratch.
/// </summary>
public sealed partial class N64AudioProcessor : IN64AudioBackend
{
    private const int ScratchSize = 0x1000;
    private const byte FlagInit = 0x01;
    private const byte FlagLoop = 0x02;
    private const byte FlagVolume = 0x04;
    private const byte FlagTwoBit = 0x04;
    private const byte FlagLeft = 0x02;
    private const byte FlagAux = 0x08;
    private const int ResamplePhaseCount = 64;
    private const int ResampleTapCount = 4;
    private const int NAudioCount = 0x170;
    private const int NAudioMain = 0x4F0;
    private const int NAudioMain2 = 0x660;
    private const int NAudioDryLeft = 0x9D0;
    private const int NAudioDryRight = 0xB40;
    private const int NAudioWetLeft = 0xCB0;
    private const int NAudioWetRight = 0xE20;
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
    private readonly ushort[] _neadEnvelopeValues = new ushort[3];
    private readonly ushort[] _neadEnvelopeSteps = new ushort[3];
    private uint _neadFilterCount;
    private uint _neadFilterLutAddress;
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

    public string Name => "Pixel64 Audio HLE";

    public N64AudioMicrocode DetectedMicrocode { get; private set; }

    public string DetectedMicrocodeName => DetectedMicrocode.ToString();

    IReadOnlyDictionary<uint, long> IN64AudioBackend.UnsupportedCommandCounts =>
        UnsupportedCommandCounts;

    public void Execute(N64RspTask task)
    {
        DetectedMicrocode = DetectMicrocode(task);
        Array.Clear(_segments);
        if (DetectedMicrocode == N64AudioMicrocode.MusyxV1)
        {
            ExecuteMusyxV1Task(task);
            return;
        }

        if (DetectedMicrocode == N64AudioMicrocode.MusyxV2)
        {
            // MusyX task data is a count of structured subframes, not an ABI
            // command-list byte length. Do not accidentally parse it as ABI-1.
            RecordUnsupported(0x100);
            return;
        }

        var canDecodeAsAbi1 = DetectedMicrocode is
            N64AudioMicrocode.Unknown or
            N64AudioMicrocode.Abi1 or
            N64AudioMicrocode.Abi1GoldenEye or
            N64AudioMicrocode.Abi1BlastCorps;
        var canDecodeAsNAudio = DetectedMicrocode is
            N64AudioMicrocode.NAudio or
            N64AudioMicrocode.NAudioBanjoKazooie or
            N64AudioMicrocode.NAudioDonkeyKong;
        var canDecodeAsNead = DetectedMicrocode is
            N64AudioMicrocode.NeadMarioKart or
            N64AudioMicrocode.NeadZeldaOcarinaOfTime;

        var pointer = task.DataPointer & 0x7FFFFF;
        var end = pointer + Math.Min(task.DataSize, 0x10000);
        while (pointer + 8 <= end)
        {
            var w0 = _memory.ReadUInt32(pointer);
            var w1 = _memory.ReadUInt32(pointer + 4);
            pointer += 8;
            CommandsProcessed++;
            var flags = (byte)(w0 >> 16);
            var opcode = w0 >> 24;
            if (canDecodeAsNAudio)
            {
                ExecuteNAudioCommand(opcode, w0, w1);
                continue;
            }

            if (canDecodeAsNead)
            {
                ExecuteNeadCommand(opcode, w0, w1);
                continue;
            }

            if (!canDecodeAsAbi1)
            {
                RecordUnsupported(opcode);
                continue;
            }

            switch (opcode)
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
                    if (DetectedMicrocode is
                        N64AudioMicrocode.Abi1GoldenEye or
                        N64AudioMicrocode.Abi1BlastCorps)
                    {
                        EnvelopeMixerGoldenEye(flags, ResolveAddress(w1));
                    }
                    else
                    {
                        EnvelopeMixer(flags, ResolveAddress(w1));
                    }
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
                case 0x0E:
                    PoleFilter(flags, (ushort)w0, ResolveAddress(w1));
                    break;
                case 0x0F:
                    _loopAddress = ResolveAddress(w1);
                    break;
                default:
                    RecordUnsupported(opcode);
                    break;
            }
        }
    }

    private void ExecuteNeadCommand(uint opcode, uint w0, uint w1)
    {
        var marioKart = DetectedMicrocode == N64AudioMicrocode.NeadMarioKart;
        switch (opcode)
        {
            case 0x00: // SPNOOP
            case 0x03: // SPNOOP
            case 0x17: // SPNOOP
            case >= 0x18 and <= 0x1F: // SPNOOP
                break;
            case 0x01: // ADPCM
                NeadAdpcm(w0, w1);
                break;
            case 0x02: // CLEARBUFF
                ScratchSpan((ushort)w0, (int)(w1 & 0xFFF)).Clear();
                break;
            case 0x04: // ADDMIXER (SPNOOP in Mario Kart)
                if (!marioKart)
                {
                    AddBuffer(
                        (ushort)(w1 >> 16),
                        (ushort)w1,
                        (int)((w0 >> 12) & 0xFF0));
                }

                break;
            case 0x05: // RESAMPLE
                Resample((byte)(w0 >> 16), (ushort)w0, w1 & 0x00FFFFFF);
                break;
            case 0x06: // RESAMPLE_ZOH (SPNOOP in Mario Kart)
                if (!marioKart)
                {
                    NeadResampleZeroOrder(w0, w1);
                }

                break;
            case 0x07: // SEGMENT in Mario Kart, FILTER in later NEAD
                if (!marioKart)
                {
                    NeadFilter(w0, w1);
                }

                break;
            case 0x08: // SETBUFF
                _inAddress = (ushort)w0;
                _outAddress = (ushort)(w1 >> 16);
                _count = (ushort)w1;
                break;
            case 0x09: // SPNOOP in Mario Kart, DUPLICATE in later NEAD
                if (!marioKart)
                {
                    NeadDuplicate(w0, w1);
                }

                break;
            case 0x0A: // DMEMMOVE
                MoveScratchExact(
                    (ushort)w0,
                    (ushort)(w1 >> 16),
                    AlignUp((ushort)w1, 4));
                break;
            case 0x0B: // LOADADPCM
                LoadCodebook((ushort)w0, w1 & 0x00FFFFFF);
                break;
            case 0x0C: // MIXER
                MixBuffer(
                    unchecked((short)w0),
                    (ushort)(w1 >> 16),
                    (ushort)w1,
                    (int)((w0 >> 12) & 0xFF0));
                break;
            case 0x0D: // INTERLEAVE
                if (marioKart)
                {
                    InterleaveBuffer(
                        _outAddress,
                        (ushort)(w1 >> 16),
                        (ushort)w1,
                        _count);
                }
                else
                {
                    InterleaveBuffer(
                        (ushort)w0,
                        (ushort)(w1 >> 16),
                        (ushort)w1,
                        (int)((w0 >> 12) & 0xFF0));
                }

                break;
            case 0x0E: // POLEF in Mario Kart, HILOGAIN in later NEAD
                if (marioKart)
                {
                    PoleFilter((byte)(w0 >> 16), (ushort)w0, w1 & 0x00FFFFFF);
                }
                else
                {
                    NeadHighLowGain(w0, w1);
                }

                break;
            case 0x0F: // SETLOOP
                _loopAddress = w1 & 0x00FFFFFF;
                break;
            case 0x10: // NEAD_16
                NeadCopyBlocks(w0, w1);
                break;
            case 0x11: // INTERL
                NeadCopyEveryOtherSample(w0, w1);
                break;
            case 0x12: // ENVSETUP1
                _neadEnvelopeValues[2] = (ushort)((w0 >> 8) & 0xFF00);
                _neadEnvelopeSteps[2] = marioKart ? (ushort)0 : (ushort)w0;
                _neadEnvelopeSteps[0] = (ushort)(w1 >> 16);
                _neadEnvelopeSteps[1] = (ushort)w1;
                break;
            case 0x13: // ENVMIXER
                NeadEnvelopeMixer(w0, w1, marioKart);
                break;
            case 0x14: // LOADBUFF
                CopyRdramToScratch(
                    w1 & 0x00FFFFFF,
                    (int)(w0 & 0xFFF),
                    (int)((w0 >> 12) & 0xFFF));
                break;
            case 0x15: // SAVEBUFF
                CopyScratchToRdram(
                    (int)(w0 & 0xFFF),
                    w1 & 0x00FFFFFF,
                    (int)((w0 >> 12) & 0xFFF));
                break;
            case 0x16: // ENVSETUP2
                _neadEnvelopeValues[0] = (ushort)(w1 >> 16);
                _neadEnvelopeValues[1] = (ushort)w1;
                break;
            default:
                RecordUnsupported(opcode);
                break;
        }
    }

    private void NeadAdpcm(uint w0, uint w1)
    {
        // NEAD uses the same predictor/history layout as ABI-1, but keeps
        // its buffer addresses directly in the command state.
        Adpcm((byte)(w0 >> 16), w1 & 0x00FFFFFF);
    }

    private void NeadCopyEveryOtherSample(uint w0, uint w1)
    {
        var count = (ushort)w0;
        var source = (ushort)(w1 >> 16);
        var destination = (ushort)w1;
        for (var index = 0; index < count; index++)
        {
            WriteScratchInt16(
                destination + (index * 2),
                ReadScratchInt16(source + (index * 4)));
        }
    }

    private void NeadCopyBlocks(uint w0, uint w1)
    {
        var blocks = (byte)(w0 >> 16);
        var source = (int)(ushort)w0;
        var destination = (int)(ushort)(w1 >> 16);
        var blockSize = (ushort)w1;
        if (blocks == 0 || blockSize == 0)
        {
            return;
        }

        var bytesPerBlock = AlignUp(blockSize, 0x20);
        for (var block = 0; block < blocks; block++)
        {
            MoveScratchExact(source, destination, bytesPerBlock);
            source += bytesPerBlock;
            destination += bytesPerBlock;
        }
    }

    private void NeadEnvelopeMixer(uint w0, uint w1, bool marioKart)
    {
        var source = (int)((w0 >> 12) & 0xFF0);
        var sampleCount = AlignUp((int)((w0 >> 8) & 0xFF), 8);
        var dryLeft = (int)((w1 >> 20) & 0xFF0);
        var dryRight = (int)((w1 >> 12) & 0xFF0);
        var wetLeft = (int)((w1 >> 4) & 0xFF0);
        var wetRight = (int)((w1 << 4) & 0xFF0);
        if (!marioKart && (w0 & 0x10) != 0)
        {
            (wetLeft, wetRight) = (wetRight, wetLeft);
        }

        var invertLeft = (w0 & 0x2) != 0;
        var invertRight = (w0 & 0x1) != 0;
        var invertWetLeft = !marioKart && (w0 & 0x8) != 0;
        var invertWetRight = !marioKart && (w0 & 0x4) != 0;

        for (var sampleBase = 0; sampleBase < sampleCount; sampleBase += 8)
        {
            for (var index = 0; index < 8; index++)
            {
                var offset = (sampleBase + index) * 2;
                var input = ReadScratchInt16(source + offset);
                var left = unchecked(
                    (short)(((long)input * _neadEnvelopeValues[0]) >> 16));
                var right = unchecked(
                    (short)(((long)input * _neadEnvelopeValues[1]) >> 16));
                if (invertLeft)
                {
                    left = unchecked((short)~left);
                }

                if (invertRight)
                {
                    right = unchecked((short)~right);
                }

                var leftWet = unchecked(
                    (short)(((long)left * _neadEnvelopeValues[2]) >> 16));
                var rightWet = unchecked(
                    (short)(((long)right * _neadEnvelopeValues[2]) >> 16));
                if (invertWetLeft)
                {
                    leftWet = unchecked((short)~leftWet);
                }

                if (invertWetRight)
                {
                    rightWet = unchecked((short)~rightWet);
                }

                MixInto(dryLeft + offset, left);
                MixInto(dryRight + offset, right);
                MixInto(wetLeft + offset, leftWet);
                MixInto(wetRight + offset, rightWet);
            }

            for (var envelope = 0; envelope < _neadEnvelopeValues.Length; envelope++)
            {
                _neadEnvelopeValues[envelope] = unchecked(
                    (ushort)(_neadEnvelopeValues[envelope] + _neadEnvelopeSteps[envelope]));
            }
        }
    }

    private void AddBuffer(int source, int destination, int byteCount)
    {
        for (var offset = 0; offset < byteCount; offset += 2)
        {
            var value =
                ReadScratchInt16(destination + offset) +
                ReadScratchInt16(source + offset);
            WriteScratchInt16(destination + offset, ClampToInt16(value));
        }
    }

    private void NeadDuplicate(uint w0, uint w1)
    {
        var count = (byte)(w0 >> 16);
        var source = (ushort)w0;
        var destination = (ushort)(w1 >> 16);
        if (count == 0)
        {
            return;
        }

        Span<byte> block = stackalloc byte[128];
        ScratchSpan(source, block.Length).CopyTo(block);
        for (var copy = 0; copy < count; copy++)
        {
            block.CopyTo(ScratchSpan(destination + (copy * block.Length), block.Length));
        }
    }

    private void NeadHighLowGain(uint w0, uint w1)
    {
        var gain = unchecked((sbyte)(w0 >> 16));
        var byteCount = (int)(w0 & 0xFFF);
        var destination = (ushort)(w1 >> 16);
        for (var offset = 0; offset < byteCount; offset += 2)
        {
            WriteScratchInt16(
                destination + offset,
                ClampToInt16((ReadScratchInt16(destination + offset) * gain) >> 4));
        }
    }

    private void NeadResampleZeroOrder(uint w0, uint w1)
    {
        var pitch = (uint)(ushort)w0 << 1;
        uint phase = (ushort)w1;
        var sourceSample = _inAddress / 2;
        var destinationSample = _outAddress / 2;
        var samples = _count / 2;
        for (var index = 0; index < samples; index++)
        {
            WriteScratchInt16(
                (destinationSample + index) * 2,
                ReadScratchInt16(sourceSample * 2));
            phase += pitch;
            sourceSample += (int)(phase >> 16);
            phase &= 0xFFFF;
        }
    }

    private void NeadFilter(uint w0, uint w1)
    {
        var flags = (byte)(w0 >> 16);
        var address = w1 & 0x00FFFFFF;
        if (flags > 1)
        {
            _neadFilterCount = w0;
            _neadFilterLutAddress = address;
            return;
        }

        var byteCount = (int)(_neadFilterCount & 0xFFFF);
        var dmem = (ushort)w0;
        if (byteCount <= 0)
        {
            return;
        }

        Span<short> coefficients = stackalloc short[8];
        for (var index = 0; index < coefficients.Length; index++)
        {
            var first = unchecked((short)ReadRdramUInt16(
                _neadFilterLutAddress + (uint)(index * 2)));
            var secondAddress = address + 0x10u + (uint)(index * 2);
            var second = unchecked((short)ReadRdramUInt16(secondAddress));
            coefficients[index] = unchecked((short)((first + second) >> 1));
            WriteRdramUInt16(
                _neadFilterLutAddress + (uint)(index * 2),
                unchecked((ushort)coefficients[index]));
            WriteRdramUInt16(secondAddress, unchecked((ushort)coefficients[index]));
        }

        Span<short> history = stackalloc short[8];
        for (var index = 0; index < history.Length; index++)
        {
            history[index] = unchecked((short)ReadRdramUInt16(
                address + (uint)(index * 2)));
        }

        Span<short> input = stackalloc short[8];
        Span<short> output = stackalloc short[8];
        Span<int> value = stackalloc int[8];
        for (var byteOffset = 0; byteOffset < byteCount; byteOffset += 16)
        {
            for (var index = 0; index < input.Length; index++)
            {
                input[index] = ReadScratchInt16(dmem + byteOffset + (index * 2));
            }

            value[1] =
                (history[0] * coefficients[6]) + (history[3] * coefficients[7]) +
                (history[2] * coefficients[4]) + (history[5] * coefficients[5]) +
                (history[4] * coefficients[2]) + (history[7] * coefficients[3]) +
                (history[6] * coefficients[0]) + (input[1] * coefficients[1]);
            value[0] =
                (history[3] * coefficients[6]) + (history[2] * coefficients[7]) +
                (history[5] * coefficients[4]) + (history[4] * coefficients[5]) +
                (history[7] * coefficients[2]) + (history[6] * coefficients[3]) +
                (input[1] * coefficients[0]) + (input[0] * coefficients[1]);
            value[3] =
                (history[2] * coefficients[6]) + (history[5] * coefficients[7]) +
                (history[4] * coefficients[4]) + (history[7] * coefficients[5]) +
                (history[6] * coefficients[2]) + (input[1] * coefficients[3]) +
                (input[0] * coefficients[0]) + (input[3] * coefficients[1]);
            value[2] =
                (history[5] * coefficients[6]) + (history[4] * coefficients[7]) +
                (history[7] * coefficients[4]) + (history[6] * coefficients[5]) +
                (input[1] * coefficients[2]) + (input[0] * coefficients[3]) +
                (input[3] * coefficients[0]) + (input[2] * coefficients[1]);
            value[5] =
                (history[4] * coefficients[6]) + (history[7] * coefficients[7]) +
                (history[6] * coefficients[4]) + (input[1] * coefficients[5]) +
                (input[0] * coefficients[2]) + (input[3] * coefficients[3]) +
                (input[2] * coefficients[0]) + (input[5] * coefficients[1]);
            value[4] =
                (history[7] * coefficients[6]) + (history[6] * coefficients[7]) +
                (input[1] * coefficients[4]) + (input[0] * coefficients[5]) +
                (input[3] * coefficients[2]) + (input[2] * coefficients[3]) +
                (input[5] * coefficients[0]) + (input[4] * coefficients[1]);
            value[7] =
                (history[6] * coefficients[6]) + (input[1] * coefficients[7]) +
                (input[0] * coefficients[4]) + (input[3] * coefficients[5]) +
                (input[2] * coefficients[2]) + (input[5] * coefficients[3]) +
                (input[4] * coefficients[0]) + (input[7] * coefficients[1]);
            value[6] =
                (input[1] * coefficients[6]) + (input[0] * coefficients[7]) +
                (input[3] * coefficients[4]) + (input[2] * coefficients[5]) +
                (input[5] * coefficients[2]) + (input[4] * coefficients[3]) +
                (input[7] * coefficients[0]) + (input[6] * coefficients[1]);

            for (var index = 0; index < output.Length; index++)
            {
                output[index] = unchecked((short)((value[index] + 0x4000) >> 15));
                WriteScratchInt16(dmem + byteOffset + (index * 2), output[index]);
            }

            input.CopyTo(history);
        }

        for (var index = 0; index < history.Length; index++)
        {
            WriteRdramUInt16(
                address + (uint)(index * 2),
                unchecked((ushort)history[index]));
        }
    }

    private void RecordUnsupported(uint opcode)
    {
        UnsupportedCommands++;
        UnsupportedCommandCounts[opcode] =
            UnsupportedCommandCounts.GetValueOrDefault(opcode) + 1;
    }

    private void ExecuteNAudioCommand(uint opcode, uint w0, uint w1)
    {
        switch (opcode)
        {
            case 0x00:
                break;
            case 0x01:
                NAudioAdpcm(w0, w1);
                break;
            case 0x02:
                ClearBuffer(NAudioMain + (int)(w0 & 0xFFFF), (int)(w1 & 0xFFF));
                break;
            case 0x03:
                NAudioEnvelopeMixer((byte)(w0 >> 16), w0, w1 & 0x00FFFFFF);
                break;
            case 0x04:
                NAudioLoadBuffer(w0, w1);
                break;
            case 0x05:
                NAudioResample(w0, w1);
                break;
            case 0x06:
                NAudioSaveBuffer(w0, w1);
                break;
            case 0x07:
            case 0x08:
                if (DetectedMicrocode == N64AudioMicrocode.NAudioDonkeyKong)
                {
                    MixBuffer(
                        unchecked((short)w0),
                        NAudioMain + (int)(w1 >> 16),
                        NAudioMain + (int)(w1 & 0xFFFF),
                        NAudioCount);
                }
                else
                {
                    RecordUnsupported(opcode);
                }
                break;
            case 0x09:
                NAudioSetVolume((byte)(w0 >> 16), w0, w1);
                break;
            case 0x0A:
                MoveScratchExact(
                    NAudioMain + (int)(w0 & 0xFFFF),
                    NAudioMain + (int)(w1 >> 16),
                    AlignUp((int)(w1 & 0xFFFF), 4));
                break;
            case 0x0B:
                LoadCodebook((int)(w0 & 0xFFFF), w1 & 0x00FFFFFF);
                break;
            case 0x0C:
                MixBuffer(
                    unchecked((short)w0),
                    NAudioMain + (int)(w1 >> 16),
                    NAudioMain + (int)(w1 & 0xFFFF),
                    NAudioCount);
                break;
            case 0x0D:
                InterleaveBuffer(
                    NAudioMain,
                    NAudioDryLeft,
                    NAudioDryRight,
                    NAudioCount);
                break;
            case 0x0E:
                _volumeRate[1] = (_volumeRate[1] & 0xFFFF0000) | (w1 & 0xFFFF);
                break;
            case 0x0F:
                _loopAddress = w1 & 0x00FFFFFF;
                break;
            default:
                RecordUnsupported(opcode);
                break;
        }
    }

    private void NAudioSetVolume(byte flags, uint w0, uint w1)
    {
        if ((flags & FlagVolume) != 0)
        {
            if ((flags & FlagLeft) != 0)
            {
                _volume[0] = unchecked((short)w0);
                _dryGain = unchecked((short)(w1 >> 16));
                _wetGain = unchecked((short)w1);
            }
            else
            {
                _volumeTarget[1] = unchecked((short)w0);
                _volumeRate[1] = w1;
            }

            return;
        }

        _volumeTarget[0] = unchecked((short)w0);
        _volumeRate[0] = w1;
    }

    private void NAudioLoadBuffer(uint w0, uint w1)
    {
        var count = (int)((w0 >> 12) & 0xFFF);
        var destination = NAudioMain + (int)(w0 & 0xFFF);
        CopyRdramToScratch(w1 & 0x00FFFFFF, destination, count);
    }

    private void NAudioSaveBuffer(uint w0, uint w1)
    {
        var count = (int)((w0 >> 12) & 0xFFF);
        var source = NAudioMain + (int)(w0 & 0xFFF);
        CopyScratchToRdram(source, w1 & 0x00FFFFFF, count);
    }

    private void NAudioAdpcm(uint w0, uint w1)
    {
        var savedInput = _inAddress;
        var savedOutput = _outAddress;
        var savedCount = _count;
        try
        {
            _inAddress = NAudioMain + (int)((w1 >> 12) & 0xF);
            _outAddress = NAudioMain + (int)(w1 & 0xFFF);
            _count = (int)((w1 >> 16) & 0xFFF);
            Adpcm((byte)(w1 >> 28), w0 & 0x00FFFFFF);
        }
        finally
        {
            _inAddress = savedInput;
            _outAddress = savedOutput;
            _count = savedCount;
        }
    }

    private void NAudioResample(uint w0, uint w1)
    {
        var savedInput = _inAddress;
        var savedOutput = _outAddress;
        var savedCount = _count;
        try
        {
            _inAddress = NAudioMain + (int)((w1 >> 2) & 0xFFF);
            _outAddress = (w1 & 0x3) != 0 ? NAudioMain2 : NAudioMain;
            _count = NAudioCount;
            Resample((byte)(w1 >> 30), (int)((w1 >> 14) & 0xFFFF), w0 & 0x00FFFFFF);
        }
        finally
        {
            _inAddress = savedInput;
            _outAddress = savedOutput;
            _count = savedCount;
        }
    }

    private N64AudioMicrocode DetectMicrocode(N64RspTask task)
    {
        if (task.MicrocodeDataSize < 0x14)
        {
            return N64AudioMicrocode.Unknown;
        }

        var address = task.MicrocodeDataPointer & 0x7FFFFF;
        if (ReadRdramUInt32(address) == 1)
        {
            if (task.MicrocodeDataSize >= 0x34 &&
                ReadRdramUInt32(address + 0x30) == 0xF0000F00)
            {
                return ReadRdramUInt32(address + 0x28) switch
                {
                    0x1E24138C => N64AudioMicrocode.Abi1,
                    0x1DC8138C => N64AudioMicrocode.Abi1GoldenEye,
                    0x1E3C1390 => N64AudioMicrocode.Abi1BlastCorps,
                    _ => N64AudioMicrocode.Unknown
                };
            }

            return ReadRdramUInt32(address + 0x10) switch
            {
                0x11181350 => N64AudioMicrocode.NeadMarioKart,
                0x111812E0 => N64AudioMicrocode.NeadStarFoxJapan,
                0x110412AC => N64AudioMicrocode.NeadWaveRaceJapanRevB,
                0x110412CC => N64AudioMicrocode.NeadStarFox,
                0x1CD01250 => N64AudioMicrocode.NeadFZeroX,
                0x1F08122C => N64AudioMicrocode.NeadYoshisStory,
                0x1F38122C => N64AudioMicrocode.Nead1080Snowboarding,
                0x1F681230 => N64AudioMicrocode.NeadZeldaOcarinaOfTime,
                0x1F801250 => N64AudioMicrocode.NeadZeldaMajorasMask,
                0x109411F8 => N64AudioMicrocode.NeadZeldaMajorasMaskBeta,
                0x1EAC11B8 => N64AudioMicrocode.NeadAnimalCrossing,
                0x00010010 => N64AudioMicrocode.MusyxV2,
                0x1F701238 => N64AudioMicrocode.NeadMarioArtistTalentStudio,
                0x1F4C1230 => N64AudioMicrocode.NeadFZeroXExpansion,
                _ => N64AudioMicrocode.Unknown
            };
        }

        return ReadRdramUInt32(address + 0x10) switch
        {
            0x00000001 => N64AudioMicrocode.MusyxV1,
            0x0000127C => N64AudioMicrocode.NAudio,
            0x00001280 => N64AudioMicrocode.NAudioBanjoKazooie,
            0x1C58126C => N64AudioMicrocode.NAudioDonkeyKong,
            0x1AE8143C => N64AudioMicrocode.NAudioMp3,
            0x1AB0140C => N64AudioMicrocode.NAudioConker,
            _ => N64AudioMicrocode.Unknown
        };
    }

    public void SaveState(BinaryWriter writer)
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

    public void LoadState(BinaryReader reader)
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
        MoveScratchExact(source, destination, AlignUp(count, 16));
    }

    private void MoveScratchExact(int source, int destination, int count)
    {
        var from = ScratchSpan(source, count);
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
        MixBuffer(gain, source, destination, AlignUp(_count, 32));
    }

    private void MixBuffer(short gain, int source, int destination, int byteCount)
    {
        var samples = byteCount / 2;
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
        InterleaveBuffer(_outAddress, left, right, AlignUp(_count, 16));
    }

    private void InterleaveBuffer(int destination, int left, int right, int byteCount)
    {
        var samples = byteCount / 2;
        for (var index = 0; index < samples; index++)
        {
            WriteScratchInt16(destination + (index * 4), ReadScratchInt16(left + (index * 2)));
            WriteScratchInt16(destination + (index * 4) + 2, ReadScratchInt16(right + (index * 2)));
        }
    }

    private void PoleFilter(byte flags, ushort gain, uint stateAddress)
    {
        if (_count == 0)
        {
            return;
        }

        short previous1;
        short previous2;
        if ((flags & FlagInit) != 0)
        {
            previous1 = 0;
            previous2 = 0;
        }
        else
        {
            previous1 = unchecked((short)ReadRdramUInt16(stateAddress + 4));
            previous2 = unchecked((short)ReadRdramUInt16(stateAddress + 6));
        }

        // A_POLEF splits the loaded ADPCM book into two eight-tap responses.
        // The second response is gain-adjusted once per command. Each output
        // group then combines the current input, the prior group's two trailing
        // outputs, and the already-seen inputs from the current group.
        Span<short> previousResponse = stackalloc short[8];
        Span<short> scaledResponse = stackalloc short[8];
        for (var index = 0; index < 8; index++)
        {
            previousResponse[index] = _codebook[index + 8];
            scaledResponse[index] = unchecked(
                (short)(((long)_codebook[index + 8] * gain) >> 14));
            _codebook[index + 8] = scaledResponse[index];
        }

        Span<short> input = stackalloc short[8];
        Span<short> output = stackalloc short[8];
        var sampleCount = AlignUp(_count, 16) / 2;
        for (var sampleBase = 0; sampleBase < sampleCount; sampleBase += 8)
        {
            for (var index = 0; index < 8; index++)
            {
                input[index] = ReadScratchInt16(_inAddress + ((sampleBase + index) * 2));
            }

            for (var index = 0; index < 8; index++)
            {
                long accumulator = (long)input[index] * gain;
                accumulator += (long)_codebook[index] * previous1;
                accumulator += (long)previousResponse[index] * previous2;
                for (var tap = 0; tap < index; tap++)
                {
                    accumulator +=
                        (long)scaledResponse[tap] * input[index - 1 - tap];
                }

                output[index] = ClampToInt16(accumulator >> 14);
                WriteScratchInt16(
                    _outAddress + ((sampleBase + index) * 2),
                    output[index]);
            }

            previous1 = output[6];
            previous2 = output[7];
        }

        // The ABI continuation record contains the final four output samples;
        // the next command reads its last two entries as the recursive history.
        for (var index = 0; index < 4; index++)
        {
            WriteRdramUInt16(
                stateAddress + (uint)(index * 2),
                unchecked((ushort)output[index + 4]));
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
        var twoBit = (flags & FlagTwoBit) != 0;

        // The ABI exposes the 16-sample history window immediately before the
        // newly decoded frames. Resample commands deliberately address into
        // this combined window; omitting the prefix shifts their source by
        // 32 bytes and turns valid ADPCM into discontinuous noise.
        for (var index = 0; index < _adpcmState.Length; index++)
        {
            WriteScratchInt16(destinationOffset, _adpcmState[index]);
            destinationOffset += 2;
        }

        for (var frame = 0; frame < frames; frame++)
        {
            var header = ReadScratchByte(sourceOffset++);
            var scale = header >> 4;
            var predictor = Math.Min(header & 0xF, 7);
            if (twoBit)
            {
                var residualScale = Math.Min(scale, 14);
                for (var index = 0; index < 4; index++)
                {
                    var packed = ReadScratchByte(sourceOffset++);
                    _residuals[index * 4] =
                        (short)(SignExtendTwoBits(packed >> 6) << residualScale);
                    _residuals[(index * 4) + 1] =
                        (short)(SignExtendTwoBits((packed >> 4) & 3) << residualScale);
                    _residuals[(index * 4) + 2] =
                        (short)(SignExtendTwoBits((packed >> 2) & 3) << residualScale);
                    _residuals[(index * 4) + 3] =
                        (short)(SignExtendTwoBits(packed & 3) << residualScale);
                }
            }
            else
            {
                for (var index = 0; index < 8; index++)
                {
                    var packed = ReadScratchByte(sourceOffset++);
                    // ABI-1's four-bit predictor saturates its left shift at
                    // twelve. Header scales 12..15 therefore share the same
                    // residual magnitude instead of wrapping through int16.
                    var residualScale = Math.Min(scale, 12);
                    _residuals[index * 2] =
                        (short)(SignExtendNibble(packed >> 4) << residualScale);
                    _residuals[(index * 2) + 1] =
                        (short)(SignExtendNibble(packed & 0xF) << residualScale);
                }
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
                    (long)SampleForResample(whole + tap) *
                    ResampleCoefficients[coefficientOffset + tap];
            }

            WriteScratchInt16(destination, ClampToInt16(filtered >> 15));
            destination += 2;
            position += step;
        }

        var consumed = (int)(position >> 16);
        for (var index = 0; index < 4; index++)
        {
            _resampleState[index] = SampleForResample(consumed + index);
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
    /// Decodes the fixed 64-phase, four-tap coefficient ROM used by ABI-1.
    /// These Q15 values are part of the audio microcode's observable numeric
    /// behavior; a generic Lanczos approximation changes the passband and
    /// produces audible high-frequency residue after repeated resampling.
    /// </summary>
    private static short[] CreateResampleCoefficients()
    {
        const string coefficientBits =
            "0C3966AD0D46FFDF0B3966960E5FFFD80A4466690F83FFD0095A662610B4FFC8" +
            "087D65CD11F0FFBF07AB655E1338FFB606E464D9148CFFAC0628643F15EBFFA1" +
            "0577638F1756FF9604D162CB18CBFF8A043561F31A4CFF7E03A461061BD7FF71" +
            "031C60071D6CFF64029F5EF51F0BFF56022A5DD020B3FF4801BE5C9A2264FF3A" +
            "015B5B53241EFF2C010159FC25E0FF1E00AE589627A9FF1000635720297AFF02" +
            "001F559D2B50FEF4FFE2540D2D2CFEE8FFAC52702F0DFEDBFF7C50C730F3FED0" +
            "FF534F1432DCFEC6FF2E4D5734C8FEBDFF0F4B9136B6FEB6FEF549C238A5FEB0" +
            "FEDF47ED3A95FEACFECE46113C85FEABFEC044303E74FEACFEB6424A4060FEAF" +
            "FEAF4060424AFEB6FEAC3E744430FEC0FEAB3C854611FECEFEAC3A9547EDFEDF" +
            "FEB038A549C2FEF5FEB636B64B91FF0FFEBD34C84D57FF2EFEC632DC4F14FF53" +
            "FED030F350C7FF7CFEDB2F0D5270FFACFEE82D2C540DFFE2FEF42B50559D001F" +
            "FF02297A57200063FF1027A9589600AEFF1E25E059FC0101FF2C241E5B53015B" +
            "FF3A22645C9A01BEFF4820B35DD0022AFF561F0B5EF5029FFF641D6C6007031C" +
            "FF711BD7610603A4FF7E1A4C61F30435FF8A18CB62CB04D1FF961756638F0577" +
            "FFA115EB643F0628FFAC148C64D906E4FFB61338655E07ABFFBF11F065CD087D" +
            "FFC810B46626095AFFD00F8366690A44FFD80E5F66960B39FFDF0D4666AD0C39";

        var result = new short[ResamplePhaseCount * ResampleTapCount];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = unchecked(
                (short)Convert.ToUInt16(coefficientBits.Substring(index * 4, 4), 16));
        }

        return result;
    }

    private void EnvelopeMixer(byte flags, uint stateAddress)
    {
        Span<int> volumes = stackalloc int[2];
        Span<int> targets = stackalloc int[2];
        Span<int> rates = stackalloc int[2];
        Span<int> exponentialSequence = stackalloc int[2];
        Span<int> steps = stackalloc int[2];
        var wet = _wetGain;
        var dry = _dryGain;
        if ((flags & FlagInit) != 0)
        {
            for (var channel = 0; channel < 2; channel++)
            {
                volumes[channel] = _volume[channel] << 16;
                targets[channel] = _volumeTarget[channel] << 16;
                rates[channel] = unchecked((int)_volumeRate[channel]);
                exponentialSequence[channel] =
                    unchecked(_volume[channel] * rates[channel]);
            }
        }
        else
        {
            wet = unchecked((short)ReadRdramUInt16(stateAddress));
            dry = unchecked((short)ReadRdramUInt16(stateAddress + 4));
            targets[0] = unchecked((int)ReadRdramUInt32(stateAddress + 8));
            targets[1] = unchecked((int)ReadRdramUInt32(stateAddress + 12));
            rates[0] = unchecked((int)ReadRdramUInt32(stateAddress + 16));
            rates[1] = unchecked((int)ReadRdramUInt32(stateAddress + 20));
            exponentialSequence[0] = unchecked((int)ReadRdramUInt32(stateAddress + 24));
            exponentialSequence[1] = unchecked((int)ReadRdramUInt32(stateAddress + 28));
            volumes[0] = unchecked((int)ReadRdramUInt32(stateAddress + 32));
            volumes[1] = unchecked((int)ReadRdramUInt32(stateAddress + 36));
        }

        steps[0] = unchecked(targets[0] - volumes[0]);
        steps[1] = unchecked(targets[1] - volumes[1]);
        var useAuxiliary = (flags & FlagAux) != 0;
        var samples = _count / 2;
        var position = 0;
        while (position < samples)
        {
            var chunk = Math.Min(8, samples - position);
            for (var channel = 0; channel < 2; channel++)
            {
                if (steps[channel] == 0)
                {
                    continue;
                }

                exponentialSequence[channel] = unchecked(
                    (int)(((long)exponentialSequence[channel] * rates[channel]) >> 16));
                steps[channel] =
                    unchecked(exponentialSequence[channel] - volumes[channel]) >> 3;
            }

            for (var index = 0; index < chunk; index++)
            {
                var leftVolume = StepExponentialRamp(
                    ref volumes[0],
                    targets[0],
                    ref steps[0]);
                var rightVolume = StepExponentialRamp(
                    ref volumes[1],
                    targets[1],
                    ref steps[1]);
                var leftDry = ClampToInt16(((long)leftVolume * dry + 0x4000) >> 15);
                var rightDry = ClampToInt16(((long)rightVolume * dry + 0x4000) >> 15);
                var sampleOffset = (position + index) * 2;
                int input = ReadScratchInt16(_inAddress + sampleOffset);
                MixInto(
                    _outAddress + sampleOffset,
                    (int)(((long)input * leftDry) >> 15));
                MixInto(
                    _dryRightAddress + sampleOffset,
                    (int)(((long)input * rightDry) >> 15));

                if (useAuxiliary)
                {
                    var leftWet = ClampToInt16(((long)leftVolume * wet + 0x4000) >> 15);
                    var rightWet = ClampToInt16(((long)rightVolume * wet + 0x4000) >> 15);
                    MixInto(
                        _wetLeftAddress + sampleOffset,
                        (int)(((long)input * leftWet) >> 15));
                    MixInto(
                        _wetRightAddress + sampleOffset,
                        (int)(((long)input * rightWet) >> 15));
                }
            }

            position += chunk;
        }

        WriteRdramUInt16(stateAddress, unchecked((ushort)(short)wet));
        WriteRdramUInt16(stateAddress + 4, unchecked((ushort)(short)dry));
        WriteRdramUInt32(stateAddress + 8, unchecked((uint)targets[0]));
        WriteRdramUInt32(stateAddress + 12, unchecked((uint)targets[1]));
        WriteRdramUInt32(stateAddress + 16, unchecked((uint)rates[0]));
        WriteRdramUInt32(stateAddress + 20, unchecked((uint)rates[1]));
        WriteRdramUInt32(stateAddress + 24, unchecked((uint)exponentialSequence[0]));
        WriteRdramUInt32(stateAddress + 28, unchecked((uint)exponentialSequence[1]));
        WriteRdramUInt32(stateAddress + 32, unchecked((uint)volumes[0]));
        WriteRdramUInt32(stateAddress + 36, unchecked((uint)volumes[1]));
    }

    private static short StepExponentialRamp(ref int value, int target, ref int step)
    {
        value = unchecked(value + step);
        var reachedTarget = step <= 0 ? value <= target : value >= target;
        if (reachedTarget)
        {
            value = target;
            step = 0;
        }

        return unchecked((short)(value >> 16));
    }

    private void NAudioEnvelopeMixer(byte flags, uint w0, uint stateAddress)
    {
        _volume[1] = unchecked((short)w0);

        Span<int> values = stackalloc int[2];
        Span<int> targets = stackalloc int[2];
        Span<int> steps = stackalloc int[2];
        var wet = _wetGain;
        var dry = _dryGain;
        if ((flags & FlagInit) != 0)
        {
            for (var channel = 0; channel < 2; channel++)
            {
                values[channel] = _volume[channel] << 16;
                targets[channel] = _volumeTarget[channel] << 16;
                steps[channel] = unchecked((int)_volumeRate[channel]) / 8;
            }
        }
        else
        {
            wet = unchecked((short)ReadRdramUInt16(stateAddress));
            dry = unchecked((short)ReadRdramUInt16(stateAddress + 4));
            targets[0] = unchecked((short)ReadRdramUInt16(stateAddress + 8)) << 16;
            targets[1] = unchecked((short)ReadRdramUInt16(stateAddress + 12)) << 16;
            steps[0] = unchecked((int)ReadRdramUInt32(stateAddress + 16));
            steps[1] = unchecked((int)ReadRdramUInt32(stateAddress + 20));
            values[0] = unchecked((int)ReadRdramUInt32(stateAddress + 32));
            values[1] = unchecked((int)ReadRdramUInt32(stateAddress + 36));
        }

        var samples = NAudioCount / 2;
        for (var index = 0; index < samples; index++)
        {
            var leftVolume = StepExponentialRamp(
                ref values[0],
                targets[0],
                ref steps[0]);
            var rightVolume = StepExponentialRamp(
                ref values[1],
                targets[1],
                ref steps[1]);
            var leftDry = ClampToInt16(((long)leftVolume * dry + 0x4000) >> 15);
            var rightDry = ClampToInt16(((long)rightVolume * dry + 0x4000) >> 15);
            var leftWet = ClampToInt16(((long)leftVolume * wet + 0x4000) >> 15);
            var rightWet = ClampToInt16(((long)rightVolume * wet + 0x4000) >> 15);
            var sampleOffset = index * 2;
            var input = ReadScratchInt16(NAudioMain + sampleOffset);

            MixInto(
                NAudioDryLeft + sampleOffset,
                (int)(((long)input * leftDry) >> 15));
            MixInto(
                NAudioDryRight + sampleOffset,
                (int)(((long)input * rightDry) >> 15));
            MixInto(
                NAudioWetLeft + sampleOffset,
                (int)(((long)input * leftWet) >> 15));
            MixInto(
                NAudioWetRight + sampleOffset,
                (int)(((long)input * rightWet) >> 15));
        }

        WriteRdramUInt16(stateAddress, unchecked((ushort)(short)wet));
        WriteRdramUInt16(stateAddress + 4, unchecked((ushort)(short)dry));
        WriteRdramUInt16(stateAddress + 8, unchecked((ushort)(short)(targets[0] >> 16)));
        WriteRdramUInt16(stateAddress + 12, unchecked((ushort)(short)(targets[1] >> 16)));
        WriteRdramUInt32(stateAddress + 16, unchecked((uint)steps[0]));
        WriteRdramUInt32(stateAddress + 20, unchecked((uint)steps[1]));
        WriteRdramUInt32(stateAddress + 32, unchecked((uint)values[0]));
        WriteRdramUInt32(stateAddress + 36, unchecked((uint)values[1]));
    }

    private void EnvelopeMixerGoldenEye(byte flags, uint stateAddress)
    {
        Span<long> values = stackalloc long[2];
        Span<long> targets = stackalloc long[2];
        Span<long> steps = stackalloc long[2];
        var wet = _wetGain;
        var dry = _dryGain;
        if ((flags & FlagInit) != 0)
        {
            values[0] = (long)_volume[0] << 16;
            values[1] = (long)_volume[1] << 16;
            targets[0] = (long)_volumeTarget[0] << 16;
            targets[1] = (long)_volumeTarget[1] << 16;
            steps[0] = unchecked((int)_volumeRate[0]) / 8;
            steps[1] = unchecked((int)_volumeRate[1]) / 8;
        }
        else
        {
            wet = unchecked((short)ReadRdramUInt16(stateAddress));
            dry = unchecked((short)ReadRdramUInt16(stateAddress + 4));
            targets[0] = unchecked((int)ReadRdramUInt32(stateAddress + 8));
            targets[1] = unchecked((int)ReadRdramUInt32(stateAddress + 12));
            steps[0] = unchecked((int)ReadRdramUInt32(stateAddress + 16));
            steps[1] = unchecked((int)ReadRdramUInt32(stateAddress + 20));
            values[0] = unchecked((int)ReadRdramUInt32(stateAddress + 32));
            values[1] = unchecked((int)ReadRdramUInt32(stateAddress + 36));
        }

        var useAuxiliary = (flags & FlagAux) != 0;
        var samples = _count / 2;
        for (var index = 0; index < samples; index++)
        {
            var leftVolume = StepLinearRamp(ref values[0], targets[0], ref steps[0]);
            var rightVolume = StepLinearRamp(ref values[1], targets[1], ref steps[1]);
            var leftDry = ClampToInt16(((long)leftVolume * dry + 0x4000) >> 15);
            var rightDry = ClampToInt16(((long)rightVolume * dry + 0x4000) >> 15);
            var sampleOffset = index * 2;
            var input = ReadScratchInt16(_inAddress + sampleOffset);
            MixInto(
                _outAddress + sampleOffset,
                (int)(((long)input * leftDry) >> 15));
            MixInto(
                _dryRightAddress + sampleOffset,
                (int)(((long)input * rightDry) >> 15));

            if (useAuxiliary)
            {
                var leftWet = ClampToInt16(((long)leftVolume * wet + 0x4000) >> 15);
                var rightWet = ClampToInt16(((long)rightVolume * wet + 0x4000) >> 15);
                MixInto(
                    _wetLeftAddress + sampleOffset,
                    (int)(((long)input * leftWet) >> 15));
                MixInto(
                    _wetRightAddress + sampleOffset,
                    (int)(((long)input * rightWet) >> 15));
            }
        }

        WriteRdramUInt16(stateAddress, unchecked((ushort)(short)wet));
        WriteRdramUInt16(stateAddress + 4, unchecked((ushort)(short)dry));
        WriteRdramUInt32(stateAddress + 8, unchecked((uint)(int)targets[0]));
        WriteRdramUInt32(stateAddress + 12, unchecked((uint)(int)targets[1]));
        WriteRdramUInt32(stateAddress + 16, unchecked((uint)(int)steps[0]));
        WriteRdramUInt32(stateAddress + 20, unchecked((uint)(int)steps[1]));
        WriteRdramUInt32(stateAddress + 32, unchecked((uint)(int)values[0]));
        WriteRdramUInt32(stateAddress + 36, unchecked((uint)(int)values[1]));
    }

    private static short StepLinearRamp(ref long value, long target, ref long step)
    {
        value += step;
        var reachedTarget = step <= 0 ? value <= target : value >= target;
        if (reachedTarget)
        {
            value = target;
            step = 0;
        }

        return unchecked((short)(value >> 16));
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
        scratchOffset &= ~3;
        address &= ~7u;
        var destination = ScratchSpan(scratchOffset, AlignUp(count, 8));
        var source = RdramSpan(address, destination.Length);
        source.CopyTo(destination[..source.Length]);
    }

    private void CopyScratchToRdram(int scratchOffset, uint address, int count)
    {
        scratchOffset &= ~3;
        address &= ~7u;
        var source = ScratchSpan(scratchOffset, AlignUp(count, 8));
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

    private static int SignExtendTwoBits(int value) => (value << 30) >> 30;

    private static int AlignUp(int value, int alignment) =>
        (value + (alignment - 1)) & -alignment;

    private static short ClampToInt16(long value) =>
        (short)Math.Clamp(value, short.MinValue, short.MaxValue);
}
