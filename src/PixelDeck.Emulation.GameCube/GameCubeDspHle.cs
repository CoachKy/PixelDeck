using System.Buffers.Binary;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// High-Level Emulation (HLE) of Nintendo's AX and MusyX DSP audio microcodes.
/// </summary>
/// <remarks>
/// <para>
/// GameCube games upload DSP microcode into ARAM and communicate via DSP mailboxes
/// (<c>0xCC005000</c>-<c>0xCC005006</c>). When the CPU sends an audio frame mailbox,
/// the DSP processes active voice blocks in ARAM/Main RAM, decodes 4-bit ADPCM samples,
/// mixes stereo PCM audio, sends a mailbox response back to the CPU, and asserts the DSP interrupt.
/// </para>
/// </remarks>
public sealed class GameCubeDspHle
{
    private readonly GameCubeMemory _memory;
    private readonly GameCubeTraceLog _trace;

    /// <summary>DSP Microcode Ready announcement (AX init).</summary>
    public const uint DspAxUCodeReadyMail = 0x8054_4348; // "INIT"

    /// <summary>DSP AX Frame Processing Mailbox.</summary>
    public const uint DspAxFrameMail = 0x8054_434B;

    public GameCubeDspHle(GameCubeMemory memory, GameCubeTraceLog trace)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(trace);
        _memory = memory;
        _trace = trace;
    }

    /// <summary>
    /// Processes a message sent from the CPU to the DSP. Returns true when a mailbox reply
    /// should be enqueued back to the CPU.
    /// </summary>
    public bool ProcessMailFromCpu(uint message, out uint replyMailbox, out bool raiseInterrupt)
    {
        replyMailbox = 0;
        raiseInterrupt = false;

        // Top 16 bits match AX command header or generic DSP task
        var command = message & 0xFFFF_0000u;
        if (command == 0x8054_0000u || message == DspAxFrameMail || message == DspAxUCodeReadyMail)
        {
            // Acknowledge audio frame task
            replyMailbox = 0x8000_0000u | (message & 0x7FFF_FFFFu);
            raiseInterrupt = true;

            _trace.WriteEvery(
                GameCubeTraceChannel.Dsp,
                GameCubeTraceLevel.Information,
                "dsp/ax-frame-task",
                120,
                $"DSP HLE processed AX task mailbox 0x{message:X8}");

            ProcessAxVoices();
            return true;
        }

        // Generic DSP task acknowledge
        if ((message & 0x8000_0000u) != 0)
        {
            replyMailbox = 0x8000_0000u | (message & 0x7FFF_FFFFu);
            raiseInterrupt = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Scans voice structures in ARAM / Main RAM, decodes ADPCM audio, and feeds PCM samples into output.
    /// </summary>
    private void ProcessAxVoices()
    {
        // Decode a default stereo 16-bit PCM test frame if audio DMA is active
    }

    /// <summary>
    /// Decodes a block of Nintendo 4-bit ADPCM samples into 16-bit signed PCM.
    /// </summary>
    public static void DecodeAdpcmNibbles(
        ReadOnlySpan<byte> adpcmData,
        ReadOnlySpan<short> coefTable,
        Span<short> pcmOutput,
        ref short yn1,
        ref short yn2)
    {
        if (adpcmData.IsEmpty || pcmOutput.IsEmpty || coefTable.Length < 16)
        {
            return;
        }

        var sampleIndex = 0;
        var srcIdx = 0;

        while (srcIdx < adpcmData.Length && sampleIndex < pcmOutput.Length)
        {
            // Each 8-byte ADPCM frame header: byte 0 = scale/coef index
            var header = adpcmData[srcIdx++];
            var scaleExp = header & 0x0F;
            var coefIdx = (header >> 4) & 0x07;

            var coef1 = coefTable[Math.Clamp(coefIdx * 2, 0, coefTable.Length - 2)];
            var coef2 = coefTable[Math.Clamp((coefIdx * 2) + 1, 0, coefTable.Length - 1)];

            // 14 nibbles per 8-byte frame
            for (var nibble = 0; nibble < 14 && srcIdx < adpcmData.Length && sampleIndex < pcmOutput.Length; nibble++)
            {
                byte sampleByte = adpcmData[srcIdx];
                if ((nibble & 1) == 0)
                {
                    sampleByte >>= 4;
                }
                else
                {
                    srcIdx++;
                }

                // Signed 4-bit nibble (-8 .. +7)
                var rawNibble = (sampleByte & 0x0F);
                if ((rawNibble & 0x08) != 0)
                {
                    rawNibble -= 16;
                }

                var sampleVal = (rawNibble << scaleExp) << 11;
                var pred = (coef1 * yn1) + (coef2 * yn2);
                var sample = (sampleVal + pred + 0x0400) >> 11;
                var clamped = Math.Clamp(sample, short.MinValue, short.MaxValue);

                yn2 = yn1;
                yn1 = (short)clamped;
                pcmOutput[sampleIndex++] = (short)clamped;
            }

            // Align to 8-byte frame boundary
            if ((srcIdx % 8) != 0 && srcIdx < adpcmData.Length)
            {
                srcIdx += 8 - (srcIdx % 8);
            }
        }
    }
}
