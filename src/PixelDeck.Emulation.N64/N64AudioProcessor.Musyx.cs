namespace PixelDeck.Emulation.N64;

public sealed partial class N64AudioProcessor
{
    private const int MusyxSubframeSamples = 192;
    private const int MusyxMaximumVoices = 32;
    private const int MusyxSampleBufferSamples = 0x200;
    private const int MusyxSubframeHeaderSize = 0x10;
    private const int MusyxVoiceSize = 0x50;

    /// <summary>
    /// Executes the structured MusyX v1 task used by Factor 5 titles. Unlike
    /// libultra's ABI audio tasks, DataSize is a subframe count and DataPointer
    /// points at an array of SFD records.
    /// </summary>
    private void ExecuteMusyxV1Task(N64RspTask task)
    {
        var subframeAddress = task.DataPointer & 0x7FFFFF;
        var subframeCount = Math.Min(task.DataSize, 0x100u);
        if (subframeCount == 0)
        {
            return;
        }

        var stateAddress = ReadRdramUInt32(subframeAddress + 0x08);
        var state = new MusyxWorkingState();
        LoadMusyxBaseVolumes(state.BaseVolumes, stateAddress + 0x100);
        LoadMusyxSamples(state.Cc0, stateAddress + 0x110);
        LoadMusyxSamples(state.LastFour, stateAddress + 0x290);

        for (var subframe = 0u; subframe < subframeCount; subframe++)
        {
            CommandsProcessed++;
            var sfxIndex = ReadRdramUInt16(subframeAddress + 0x02);
            var voiceMask = ReadRdramUInt32(subframeAddress + 0x04);
            var sfxAddress = ReadRdramUInt32(subframeAddress + 0x0C);
            var voiceAddress = subframeAddress + MusyxSubframeHeaderSize;
            var lastSampleAddress = stateAddress;

            UpdateMusyxBaseVolumes(
                state.BaseVolumes,
                voiceMask,
                lastSampleAddress);
            InitializeMusyxV1Subframes(state);

            var outputAddress = ProcessMusyxVoices(
                state,
                voiceAddress,
                lastSampleAddress);
            ProcessMusyxV1Effects(state, sfxAddress, sfxIndex);
            WriteMusyxStereo(state, outputAddress);

            subframeAddress +=
                MusyxSubframeHeaderSize +
                (MusyxMaximumVoices * MusyxVoiceSize);
            if (subframe + 1 < subframeCount)
            {
                stateAddress = ReadRdramUInt32(subframeAddress + 0x08);
            }
        }

        StoreMusyxBaseVolumes(state.BaseVolumes, stateAddress + 0x100);
        StoreMusyxSamples(state.Cc0, stateAddress + 0x110);
        StoreMusyxSamples(state.LastFour, stateAddress + 0x290);
    }

    private void LoadMusyxBaseVolumes(int[] destination, uint address)
    {
        for (var channel = 0; channel < destination.Length; channel++)
        {
            var high = ReadRdramUInt16(address + (uint)(channel * 2));
            var low = ReadRdramUInt16(address + 8u + (uint)(channel * 2));
            destination[channel] = unchecked((int)(((uint)high << 16) | low));
        }
    }

    private void StoreMusyxBaseVolumes(int[] source, uint address)
    {
        for (var channel = 0; channel < source.Length; channel++)
        {
            WriteRdramUInt16(
                address + (uint)(channel * 2),
                unchecked((ushort)(source[channel] >> 16)));
            WriteRdramUInt16(
                address + 8u + (uint)(channel * 2),
                unchecked((ushort)source[channel]));
        }
    }

    private void UpdateMusyxBaseVolumes(
        int[] baseVolumes,
        uint voiceMask,
        uint lastSampleAddress)
    {
        if (voiceMask != 0)
        {
            for (var voice = 0; voice < MusyxMaximumVoices; voice++)
            {
                if ((voiceMask & (1u << voice)) == 0)
                {
                    continue;
                }

                for (var channel = 0; channel < baseVolumes.Length; channel++)
                {
                    baseVolumes[channel] = unchecked(
                        baseVolumes[channel] +
                        (short)ReadRdramUInt16(
                            lastSampleAddress +
                            (uint)(voice * 8) +
                            (uint)(channel * 2)));
                }
            }
        }

        for (var channel = 0; channel < baseVolumes.Length; channel++)
        {
            baseVolumes[channel] = unchecked(
                (int)(((long)baseVolumes[channel] * 0xF850) >> 16));
        }
    }

    private static void InitializeMusyxV1Subframes(MusyxWorkingState state)
    {
        var baseCc0 = ClampToInt16(state.BaseVolumes[2]);
        var baseEffect = ClampToInt16(state.BaseVolumes[3]);
        for (var sample = 0; sample < MusyxSubframeSamples; sample++)
        {
            state.Effect[sample] = baseEffect;
            state.Left[sample] = ClampToInt16(state.Cc0[sample] + baseCc0);
            state.Right[sample] = ClampToInt16(-state.Cc0[sample] - baseCc0);
            state.Cc0[sample] = 0;
        }
    }

    private uint ProcessMusyxVoices(
        MusyxWorkingState state,
        uint voiceAddress,
        uint lastSampleAddress)
    {
        if (ReadRdramUInt16(voiceAddress + 0x2C) == 0)
        {
            return ReadRdramUInt32(voiceAddress + 0x44);
        }

        for (var voice = 0; voice < MusyxMaximumVoices; voice++)
        {
            var samples = new short[MusyxSampleBufferSamples];
            int segmentBase;
            int sampleOffset;
            if (ReadRdramByte(voiceAddress + 0x3C) == 0)
            {
                LoadMusyxPcmVoice(
                    voiceAddress,
                    samples,
                    out segmentBase,
                    out sampleOffset);
            }
            else
            {
                LoadMusyxAdpcmVoice(
                    voiceAddress,
                    samples,
                    out segmentBase,
                    out sampleOffset);
            }

            MixMusyxVoice(
                state,
                voiceAddress,
                samples,
                segmentBase,
                sampleOffset,
                lastSampleAddress + (uint)(voice * 8));

            var outputAddress = ReadRdramUInt32(voiceAddress + 0x44);
            if (outputAddress != 0)
            {
                return outputAddress;
            }

            voiceAddress += MusyxVoiceSize;
        }

        RecordUnsupported(0x101);
        return 0;
    }

    private void LoadMusyxPcmVoice(
        uint voiceAddress,
        short[] samples,
        out int segmentBase,
        out int sampleOffset)
    {
        sampleOffset = ReadRdramByte(voiceAddress + 0x3E);
        var primarySampleCount = ReadRdramUInt16(voiceAddress + 0x40);
        var secondarySampleCount = ReadRdramUInt16(voiceAddress + 0x42);
        var reservedSamples = Math.Min(
            AlignUp(primarySampleCount + sampleOffset, 4),
            MusyxSampleBufferSamples);
        segmentBase = MusyxSampleBufferSamples - reservedSamples;

        LoadMusyxConcatenatedPcm(
            voiceAddress + 0x24,
            samples,
            segmentBase);
        if (secondarySampleCount != 0)
        {
            LoadMusyxConcatenatedPcm(
                voiceAddress + 0x30,
                samples,
                0);
        }
    }

    private void LoadMusyxAdpcmVoice(
        uint voiceAddress,
        short[] samples,
        out int segmentBase,
        out int sampleOffset)
    {
        var primaryFrames = ReadRdramByte(voiceAddress + 0x3C);
        var secondaryFrames = ReadRdramByte(voiceAddress + 0x3D);
        var primarySkip = ReadRdramByte(voiceAddress + 0x3E);
        var secondarySkip = ReadRdramByte(voiceAddress + 0x3F);
        var codebookAddress = ReadRdramUInt32(voiceAddress + 0x40);
        var codebook = new short[128];
        for (var index = 0; index < codebook.Length; index++)
        {
            codebook[index] = unchecked((short)ReadRdramUInt16(
                codebookAddress + (uint)(index * 2)));
        }

        var primarySamples = Math.Min(
            primaryFrames * 32,
            MusyxSampleBufferSamples);
        segmentBase = MusyxSampleBufferSamples - primarySamples;
        sampleOffset = primarySkip & 0x1F;

        var compressed = new byte[320];
        LoadMusyxConcatenatedBytes(
            voiceAddress + 0x24,
            compressed);
        DecodeMusyxAdpcmFrames(
            samples,
            segmentBase,
            compressed,
            codebook,
            primaryFrames,
            primarySkip);

        if (secondaryFrames != 0)
        {
            Array.Clear(compressed);
            LoadMusyxConcatenatedBytes(
                voiceAddress + 0x30,
                compressed);
            DecodeMusyxAdpcmFrames(
                samples,
                0,
                compressed,
                codebook,
                secondaryFrames,
                secondarySkip);
        }
    }

    private void LoadMusyxConcatenatedPcm(
        uint descriptorAddress,
        short[] destination,
        int destinationIndex)
    {
        var firstAddress = ReadRdramUInt32(descriptorAddress);
        var secondAddress = ReadRdramUInt32(descriptorAddress + 4);
        var firstSamples = ReadRdramUInt16(descriptorAddress + 8) / 2;
        var secondSamples = ReadRdramUInt16(descriptorAddress + 10) / 2;

        destinationIndex = CopyMusyxPcm(
            firstAddress,
            firstSamples,
            destination,
            destinationIndex);
        CopyMusyxPcm(
            secondAddress,
            secondSamples,
            destination,
            destinationIndex);
    }

    private int CopyMusyxPcm(
        uint sourceAddress,
        int count,
        short[] destination,
        int destinationIndex)
    {
        count = Math.Min(count, destination.Length - destinationIndex);
        for (var index = 0; index < count; index++)
        {
            destination[destinationIndex++] = unchecked((short)ReadRdramUInt16(
                sourceAddress + (uint)(index * 2)));
        }

        return destinationIndex;
    }

    private void LoadMusyxConcatenatedBytes(
        uint descriptorAddress,
        byte[] destination)
    {
        var firstAddress = ReadRdramUInt32(descriptorAddress);
        var secondAddress = ReadRdramUInt32(descriptorAddress + 4);
        var firstCount = ReadRdramUInt16(descriptorAddress + 8);
        var secondCount = ReadRdramUInt16(descriptorAddress + 10);
        var destinationIndex = CopyMusyxBytes(
            firstAddress,
            firstCount,
            destination,
            0);
        CopyMusyxBytes(
            secondAddress,
            secondCount,
            destination,
            destinationIndex);
    }

    private int CopyMusyxBytes(
        uint sourceAddress,
        int count,
        byte[] destination,
        int destinationIndex)
    {
        count = Math.Min(count, destination.Length - destinationIndex);
        for (var index = 0; index < count; index++)
        {
            destination[destinationIndex++] = ReadRdramByte(
                sourceAddress + (uint)index);
        }

        return destinationIndex;
    }

    private static void DecodeMusyxAdpcmFrames(
        short[] destination,
        int destinationIndex,
        byte[] source,
        short[] codebook,
        int frameCount,
        int skippedSamples)
    {
        var historyOffset = 0;
        var packedOffset = 8;
        var skipAlternateGap = false;
        var predicted = new short[32];
        if (skippedSamples >= 32)
        {
            skipAlternateGap = true;
            historyOffset += 4;
            packedOffset += 16;
        }

        for (var frameIndex = 0;
             frameIndex < frameCount && destinationIndex + 32 <= destination.Length;
             frameIndex++)
        {
            if (packedOffset + 16 > source.Length ||
                historyOffset + 4 > source.Length)
            {
                break;
            }

            var header = source[packedOffset];
            var bookOffset = header & 0xF0;
            var shift = header & 0x0F;
            predicted[0] = unchecked((short)(
                (source[historyOffset] << 8) |
                source[historyOffset + 1]));
            predicted[1] = unchecked((short)(
                (source[historyOffset + 2] << 8) |
                source[historyOffset + 3]));
            for (var packed = 1; packed < 16; packed++)
            {
                var value = source[packedOffset + packed];
                predicted[packed * 2] = DecodeMusyxAdpcmNibble(
                    value & 0xF0,
                    8,
                    shift);
                predicted[(packed * 2) + 1] = DecodeMusyxAdpcmNibble(
                    value & 0x0F,
                    12,
                    shift);
            }

            destination[destinationIndex] = predicted[0];
            destination[destinationIndex + 1] = predicted[1];
            ComputeMusyxAdpcmResiduals(
                destination,
                destinationIndex + 2,
                predicted,
                2,
                codebook,
                bookOffset,
                destinationIndex,
                6);
            ComputeMusyxAdpcmResiduals(
                destination,
                destinationIndex + 8,
                predicted,
                8,
                codebook,
                bookOffset,
                destinationIndex + 6,
                8);
            ComputeMusyxAdpcmResiduals(
                destination,
                destinationIndex + 16,
                predicted,
                16,
                codebook,
                bookOffset,
                destinationIndex + 14,
                8);
            ComputeMusyxAdpcmResiduals(
                destination,
                destinationIndex + 24,
                predicted,
                24,
                codebook,
                bookOffset,
                destinationIndex + 22,
                8);

            if (skipAlternateGap)
            {
                packedOffset += 8;
                historyOffset += 32;
            }

            skipAlternateGap = !skipAlternateGap;
            packedOffset += 16;
            historyOffset += 4;
            destinationIndex += 32;
        }
    }

    private static short DecodeMusyxAdpcmNibble(
        int value,
        int leftShift,
        int rightShift)
    {
        var signed = unchecked((short)(value << leftShift));
        return unchecked((short)(signed >> rightShift));
    }

    private static void ComputeMusyxAdpcmResiduals(
        short[] destination,
        int destinationIndex,
        ReadOnlySpan<short> predicted,
        int predictedIndex,
        short[] codebook,
        int bookOffset,
        int historyIndex,
        int count)
    {
        var firstHistory = destination[historyIndex];
        var secondHistory = destination[historyIndex + 1];
        for (var sample = 0; sample < count; sample++)
        {
            long accumulator = (long)predicted[predictedIndex + sample] << 11;
            accumulator += (long)codebook[bookOffset + sample] * firstHistory;
            accumulator += (long)codebook[bookOffset + 8 + sample] * secondHistory;
            for (var previous = 0; previous < sample; previous++)
            {
                accumulator +=
                    (long)codebook[bookOffset + 8 + previous] *
                    predicted[predictedIndex + sample - 1 - previous];
            }

            destination[destinationIndex + sample] =
                ClampToInt16(accumulator >> 11);
        }
    }

    private void MixMusyxVoice(
        MusyxWorkingState state,
        uint voiceAddress,
        short[] samples,
        int segmentBase,
        int sampleOffset,
        uint lastSampleAddress)
    {
        var pitchAccumulator = (uint)ReadRdramUInt16(voiceAddress + 0x20);
        var pitchStep = (uint)ReadRdramUInt16(voiceAddress + 0x22) << 4;
        var endPoint = ReadRdramUInt16(voiceAddress + 0x48);
        var restartPoint = ReadRdramUInt16(voiceAddress + 0x4A);
        var additionalOffset = ReadRdramUInt16(voiceAddress + 0x4E);

        var sampleIndex = segmentBase + sampleOffset + additionalOffset;
        var sampleEnd = segmentBase + endPoint;
        var sampleRestart =
            (restartPoint & 0x7FFF) +
            ((restartPoint & 0x8000) != 0 ? 0 : segmentBase);

        var envelopes = new int[4];
        var envelopeSteps = new int[4];
        for (var channel = 0; channel < 4; channel++)
        {
            envelopes[channel] = unchecked((int)ReadRdramUInt32(
                voiceAddress + (uint)(channel * 4)));
            envelopeSteps[channel] = unchecked((int)ReadRdramUInt32(
                voiceAddress + 0x10u + (uint)(channel * 4)));
        }

        var destinations = new[]
        {
            state.Left,
            state.Right,
            state.Cc0,
            state.Effect
        };
        Span<short> lastValues = stackalloc short[4];
        for (var outputSample = 0;
             outputSample < MusyxSubframeSamples;
             outputSample++)
        {
            sampleIndex += (int)(pitchAccumulator >> 16);
            pitchAccumulator =
                (pitchAccumulator & 0xFFFF) +
                pitchStep;

            if (sampleIndex >= sampleEnd)
            {
                sampleIndex = sampleRestart + (sampleIndex - sampleEnd);
            }

            sampleIndex = Math.Clamp(sampleIndex, 0, samples.Length - 4);
            var coefficientOffset = (int)((pitchAccumulator & 0xFC00) >> 8);
            var sampleValue = DotMusyxResampler(
                samples,
                sampleIndex,
                coefficientOffset);

            for (var channel = 0; channel < destinations.Length; channel++)
            {
                var contribution = ClampToInt16(
                    ((long)sampleValue * (envelopes[channel] >> 16)) >> 15);
                lastValues[channel] = contribution;
                destinations[channel][outputSample] = ClampToInt16(
                    destinations[channel][outputSample] +
                    contribution);
                envelopes[channel] = unchecked(
                    envelopes[channel] +
                    envelopeSteps[channel]);
            }
        }

        for (var channel = 0; channel < lastValues.Length; channel++)
        {
            WriteRdramUInt16(
                lastSampleAddress + (uint)(channel * 2),
                unchecked((ushort)lastValues[channel]));
        }
    }

    private static short DotMusyxResampler(
        short[] samples,
        int sampleIndex,
        int coefficientOffset)
    {
        long accumulator = 0;
        for (var tap = 0; tap < ResampleTapCount; tap++)
        {
            accumulator = ClampToInt16(
                accumulator +
                ((long)samples[sampleIndex + tap] *
                 ResampleCoefficients[coefficientOffset + tap] >> 15));
        }

        return (short)accumulator;
    }

    private void ProcessMusyxV1Effects(
        MusyxWorkingState state,
        uint sfxAddress,
        ushort sfxIndex)
    {
        if (sfxAddress == 0)
        {
            return;
        }

        var circularBufferAddress = ReadRdramUInt32(sfxAddress);
        var circularBufferLength = ReadRdramUInt32(sfxAddress + 4);
        var tapCount = Math.Min(ReadRdramUInt16(sfxAddress + 8), (ushort)8);
        if (circularBufferLength == 0)
        {
            return;
        }

        var effect = new short[MusyxSubframeSamples];
        var position = (long)sfxIndex * MusyxSubframeSamples;
        for (var tap = 0; tap < tapCount; tap++)
        {
            var delay = ReadRdramUInt32(sfxAddress + 0x0Cu + (uint)(tap * 4));
            var gain = unchecked((short)ReadRdramUInt16(
                sfxAddress + 0x2Cu + (uint)(tap * 2)));
            var delayedPosition = (position - delay) % circularBufferLength;
            if (delayedPosition <= 0)
            {
                delayedPosition += circularBufferLength;
            }

            for (var sample = 0; sample < effect.Length; sample++)
            {
                var sourceIndex =
                    (delayedPosition + sample) % circularBufferLength;
                var delayed = unchecked((short)ReadRdramUInt16(
                    circularBufferAddress + (uint)(sourceIndex * 2)));
                effect[sample] = ClampToInt16(
                    effect[sample] +
                    (((long)delayed * gain + 0x4000) >> 15));
            }
        }

        for (var sample = 0; sample < effect.Length; sample++)
        {
            state.Left[sample] = ClampToInt16(
                state.Left[sample] + effect[sample]);
            state.Right[sample] = ClampToInt16(
                state.Right[sample] + effect[sample]);
        }

        Span<short> filterInput = stackalloc short[MusyxSubframeSamples + 4];
        state.LastFour.CopyTo(filterInput);
        effect.CopyTo(filterInput[4..]);
        effect.AsSpan(effect.Length - 4).CopyTo(state.LastFour);
        var filterGain = unchecked((short)ReadRdramUInt16(sfxAddress + 0x0A));
        Span<int> filterCoefficients = stackalloc int[4];
        for (var tap = 0; tap < filterCoefficients.Length; tap++)
        {
            var coefficient = unchecked((short)ReadRdramUInt16(
                sfxAddress + 0x40u + (uint)(tap * 2)));
            filterCoefficients[tap] =
                (filterGain * coefficient) >> 15;
        }

        for (var sample = 0; sample < MusyxSubframeSamples; sample++)
        {
            long filtered = 0;
            for (var tap = 0; tap < filterCoefficients.Length; tap++)
            {
                filtered +=
                    (long)filterCoefficients[tap] *
                    filterInput[sample + tap + 1];
            }

            state.Effect[sample] = ClampToInt16(
                state.Effect[sample] +
                (filtered >> 15));
            WriteRdramUInt16(
                circularBufferAddress +
                (uint)((position + sample) * 2),
                unchecked((ushort)state.Effect[sample]));
        }
    }

    private void WriteMusyxStereo(
        MusyxWorkingState state,
        uint outputAddress)
    {
        if (outputAddress == 0)
        {
            RecordUnsupported(0x102);
            return;
        }

        var baseLeft = ClampToInt16(state.BaseVolumes[0]);
        var baseRight = ClampToInt16(state.BaseVolumes[1]);
        for (var sample = 0; sample < MusyxSubframeSamples; sample++)
        {
            var left = ClampToInt16(state.Left[sample] + baseLeft);
            var right = ClampToInt16(state.Right[sample] + baseRight);
            WriteRdramUInt32(
                outputAddress + (uint)(sample * 4),
                ((uint)(ushort)left << 16) | (ushort)right);
        }
    }

    private void LoadMusyxSamples(short[] destination, uint address)
    {
        for (var sample = 0; sample < destination.Length; sample++)
        {
            destination[sample] = unchecked((short)ReadRdramUInt16(
                address + (uint)(sample * 2)));
        }
    }

    private void StoreMusyxSamples(short[] source, uint address)
    {
        for (var sample = 0; sample < source.Length; sample++)
        {
            WriteRdramUInt16(
                address + (uint)(sample * 2),
                unchecked((ushort)source[sample]));
        }
    }

    private byte ReadRdramByte(uint address)
    {
        var source = RdramSpan(address, 1);
        return source.IsEmpty ? (byte)0 : source[0];
    }

    private sealed class MusyxWorkingState
    {
        public short[] Left { get; } = new short[MusyxSubframeSamples];

        public short[] Right { get; } = new short[MusyxSubframeSamples];

        public short[] Cc0 { get; } = new short[MusyxSubframeSamples];

        public short[] Effect { get; } = new short[MusyxSubframeSamples];

        public int[] BaseVolumes { get; } = new int[4];

        public short[] LastFour { get; } = new short[4];
    }
}
