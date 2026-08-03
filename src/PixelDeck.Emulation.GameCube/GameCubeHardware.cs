using System.Buffers.Binary;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// The memory-mapped hardware register block at 0xCC00_0000.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not an emulation of the GameCube's hardware. It is the
/// smallest set of answers that lets a retail startup sequence get past the
/// point where it waits for hardware — nothing here draws, plays or reads
/// anything. Every register divides into three cases, and the difference
/// between them is kept visible:
/// </para>
/// <list type="bullet">
/// <item>Registers with modelled behaviour, traced on
/// <see cref="GameCubeTraceChannel.Registers"/>.</item>
/// <item>Registers that are merely stored and read back, which is right for
/// configuration a game writes and later re-reads.</item>
/// <item>Registers with no behaviour at all, reported on
/// <see cref="GameCubeTraceChannel.Unimplemented"/> and counted, so the work
/// list keeps naming what is missing.</item>
/// </list>
/// <para>
/// The one that mattered: Super Mario Sunshine writes the DSP control register
/// three times and then reads it 978,834 times, waiting for a reset handshake.
/// Clearing that bit on write is four lines and it is the difference between a
/// boot that proceeds and one that spins forever.
/// </para>
/// </remarks>
public sealed class GameCubeHardware
{
    /// <summary>The register window, from the command processor to the FIFO.</summary>
    public const uint Base = 0xCC00_0000;

    public const uint Size = 0x0001_0000;

    // Block bases, as offsets within the window.
    private const uint CommandProcessor = 0x0000;
    private const uint PixelEngine = 0x1000;
    private const uint VideoInterface = 0x2000;
    private const uint ProcessorInterface = 0x3000;
    private const uint MemoryInterface = 0x4000;
    private const uint DspInterface = 0x5000;
    private const uint DvdInterface = 0x6000;
    private const uint SerialInterface = 0x6400;
    private const uint ExternalInterface = 0x6800;
    private const uint AudioInterface = 0x6C00;

    /// <summary>
    /// The write-gather pipe: everything a game draws is written here, one
    /// burst at a time, and lands in the FIFO in main memory.
    /// </summary>
    private const uint WriteGatherPipe = 0x8000;

    // The command processor's FIFO, in pairs of sixteen-bit halves because
    // that is how the hardware exposes a 32-bit address.
    private const uint FifoBaseLow = CommandProcessor + 0x20;
    private const uint FifoBaseHigh = CommandProcessor + 0x22;
    private const uint FifoEndLow = CommandProcessor + 0x24;
    private const uint FifoEndHigh = CommandProcessor + 0x26;
    private const uint FifoDistanceLow = CommandProcessor + 0x30;
    private const uint FifoDistanceHigh = CommandProcessor + 0x32;
    private const uint FifoWritePointerLow = CommandProcessor + 0x34;
    private const uint FifoWritePointerHigh = CommandProcessor + 0x36;
    private const uint FifoReadPointerLow = CommandProcessor + 0x38;
    private const uint FifoReadPointerHigh = CommandProcessor + 0x3A;

    /// <summary>The CPU-to-DSP mailbox, written high word first.</summary>
    private const uint DspMailToDspHigh = DspInterface + 0x00;
    private const uint DspMailToDspLow = DspInterface + 0x02;

    /// <summary>The DSP-to-CPU mailbox, read high word first.</summary>
    private const uint DspMailToCpuHigh = DspInterface + 0x04;
    private const uint DspMailToCpuLow = DspInterface + 0x06;

    /// <summary>
    /// The word the DSP's boot ROM sends the CPU once it has come out of
    /// reset. Documented hardware behaviour, not a convenient invention: the
    /// bit pattern already carries the mailbox's own "mail waiting" flag in
    /// its top bit, which is what makes the CPU's poll succeed.
    /// </summary>
    private const uint DspRomReadyMail = 0x8071_FEED;

    /// <summary>
    /// Set in the DSP control register while the boot ROM is in charge.
    /// Clearing it is the CPU's instruction to start the microcode.
    /// </summary>
    private const ushort DspInitInProgress = 0x0800;

    /// <summary>
    /// What the init-audio-system microcode announces once the boot ROM has
    /// started it. Also documented hardware behaviour: clearing the init bit
    /// makes the ROM load that microcode out of ARAM and run it, and this is
    /// the first thing it says.
    /// </summary>
    private const uint DspInitUCodeReadyMail = 0x8054_4348;

    /// <summary>Top bit of a mailbox's high word: a message is waiting.</summary>
    private const ushort MailWaiting = 0x8000;

    // DSP control and status, at 0xCC00_500A. Bit names follow the hardware.
    private const uint DspControlStatus = DspInterface + 0x0A;
    private const ushort DspReset = 0x0001;
    private const ushort DspInterruptStatus = 0x0008 | 0x0020 | 0x0080;
    private const ushort DspDmaInProgress = 0x0200;

    /// <summary>The ARAM DMA registers: main address, ARAM address, and count.</summary>
    private const uint AramDmaMainAddress = DspInterface + 0x20;
    private const uint AramDmaAramAddress = DspInterface + 0x24;
    private const uint AramDmaControl = DspInterface + 0x28;

    /// <summary>
    /// The low half of the size register. Writing it is what starts the
    /// transfer — the other five halves are only setup.
    /// </summary>
    private const uint AramDmaSizeLow = DspInterface + 0x2A;

    /// <summary>Set in the count register when the transfer reads out of ARAM.</summary>
    private const uint AramDmaFromAram = 0x8000_0000;

    /// <summary>The ARAM completion bit in the DSP control register.</summary>
    private const ushort AramInterrupt = 0x0020;

    /// <summary>
    /// The three interrupt sources inside the DSP block, each a status bit with
    /// its enable in the bit immediately above it: audio DMA, ARAM DMA, and the
    /// DSP itself. Status is cleared by writing a one to it.
    /// </summary>
    private const ushort AudioDmaInterrupt = 0x0008;
    private const ushort AudioDmaInterruptMask = 0x0010;
    private const ushort AramInterruptMask = 0x0040;
    private const ushort DspMailInterrupt = 0x0080;
    private const ushort DspMailInterruptMask = 0x0100;

    /// <summary>The DSP's bit in the processor interface's cause register.</summary>
    private const uint DspInterruptCause = 0x0000_0040;

    /// <summary>The ARAM controller's mode register, at 0xCC00_5016.</summary>
    private const uint AramMode = DspInterface + 0x16;

    /// <summary>
    /// ARAM_NORM: raised by the ARAM controller once it has finished
    /// initialising, and polled by the boot sequence until it appears.
    /// </summary>
    /// <remarks>
    /// PixelCube's ARAM is an array with nothing to initialise, so it is ready
    /// the first time anyone looks. This bit was confirmed against the
    /// hardware documentation rather than assumed from the shape of the poll:
    /// it sits beside a mode field the CPU does write, and reading it back as
    /// configuration would have been an equally plausible guess and wrong.
    /// </remarks>
    private const ushort AramReady = 0x0001;

    // The optical drive. A command goes into the three command words, the
    // destination and size into the DMA pair, and writing the start bit runs it.
    private const uint DvdStatus = DvdInterface + 0x00;
    private const uint DvdCommand0 = DvdInterface + 0x08;
    private const uint DvdCommand1 = DvdInterface + 0x0C;
    private const uint DvdCommand2 = DvdInterface + 0x10;
    private const uint DvdDmaAddress = DvdInterface + 0x14;
    private const uint DvdDmaLength = DvdInterface + 0x18;
    private const uint DvdImmediate = DvdInterface + 0x20;
    private const uint DvdConfiguration = DvdInterface + 0x24;
    private const uint DvdControl = DvdInterface + 0x1C;

    /// <summary>Transfer complete, in the drive's status register.</summary>
    private const uint DvdTransferComplete = 0x0000_0004;

    /// <summary>Read data from the disc into main memory.</summary>
    private const uint DvdReadCommand = 0xA8;

    /// <summary>Read the disc identifier: the first 0x20 bytes of the header.</summary>
    private const uint DvdReadIdCommand = 0x12;

    /// <summary>Report the last error. Nothing here ever fails.</summary>
    private const uint DvdRequestErrorCommand = 0xE0;

    /// <summary>
    /// A ceiling on one transfer. Real reads are at most a few megabytes;
    /// anything past this is a misparsed command, and allocating on it is how
    /// a bad command becomes an out-of-memory crash.
    /// </summary>
    private const int MaximumDvdTransfer = 16 * 1024 * 1024;
    private const uint SerialCommunicationStatus = SerialInterface + 0x34;
    private const uint SerialStatus = SerialInterface + 0x38;

    /// <summary>
    /// Controller polling: bits 4 to 7 enable a port each, and the rate is set
    /// in video lines rather than in time.
    /// </summary>
    private const uint SerialPoll = SerialInterface + 0x30;
    private const uint SerialPollEnabledPorts = 0x0000_00F0;

    /// <summary>
    /// The two interrupts the serial interface raises, each a status bit with
    /// its enable below it. Status is cleared by writing a one.
    /// </summary>
    private const uint TransferCompleteInterrupt = 1u << 31;
    private const uint TransferCompleteInterruptMask = 1u << 30;
    private const uint ReadStatusInterrupt = 1u << 28;
    private const uint ReadStatusInterruptMask = 1u << 27;

    private const uint SerialInterruptStatus = TransferCompleteInterrupt | ReadStatusInterrupt;

    /// <summary>The serial interface's bit in the processor interface.</summary>
    private const uint SerialInterruptCause = 0x0000_0008;

    /// <summary>
    /// The control register of each of the three EXI channels. Channels are
    /// twenty bytes apart, and the control register is the fourth word of each.
    /// </summary>
    private static readonly uint[] ExternalControlRegisters =
    [
        ExternalInterface + 0x0C,
        ExternalInterface + 0x20,
        ExternalInterface + 0x34
    ];

    /// <summary>
    /// The three external interface channels' status registers. Each channel is
    /// 0x14 apart, and its control register is 0x0C beyond its status.
    /// </summary>
    private static readonly uint[] ExternalStatusRegisters =
    [
        ExternalInterface + 0x00,
        ExternalInterface + 0x14,
        ExternalInterface + 0x28
    ];

    /// <summary>
    /// The three interrupts a channel raises, each a status bit with its enable
    /// in the bit below it. Status is cleared by writing a one.
    /// </summary>
    private const uint ExternalInterruptMask = 1u << 0;
    private const uint ExternalInterrupt = 1u << 1;
    private const uint ExternalTransferCompleteMask = 1u << 2;
    private const uint ExternalTransferComplete = 1u << 3;
    private const uint ExternalInsertionMask = 1u << 10;
    private const uint ExternalInsertion = 1u << 11;

    private const uint ExternalInterruptStatus =
        ExternalInterrupt | ExternalTransferComplete | ExternalInsertion;

    /// <summary>The external interface's bit in the processor interface.</summary>
    private const uint ExternalInterruptCause = 0x0000_0010;

    /// <summary>Bit zero of a transfer control register: start, and busy.</summary>
    private const uint TransferStart = 1;

    // The processor interface gathers every device's interrupt into one line
    // to the CPU. A cause is asserted by the device and cleared by software.
    private const uint InterruptCause = ProcessorInterface + 0x00;
    private const uint InterruptMask = ProcessorInterface + 0x04;

    /// <summary>The video interface's bit in the interrupt cause register.</summary>
    private const uint VideoInterruptCause = 0x0000_0100;

    // The video interface's four programmable display interrupts, each of
    // which fires when the beam reaches a chosen line.
    private const uint DisplayInterrupt0 = VideoInterface + 0x30;
    private const int DisplayInterruptCount = 4;
    private const uint VerticalPosition = VideoInterface + 0x2C;

    /// <summary>Top and bottom field base addresses of the external framebuffer.</summary>
    private const uint TopFieldBase = VideoInterface + 0x1C;
    private const uint BottomFieldBase = VideoInterface + 0x24;

    /// <summary>
    /// Where the video interface is currently reading its picture from, or zero
    /// if a game has not pointed it anywhere yet.
    /// </summary>
    /// <remarks>
    /// The top field is preferred and the bottom is the fallback, because a
    /// game running progressively programs only one of them and which one is
    /// not fixed.
    /// </remarks>
    public uint ExternalFramebuffer
    {
        get
        {
            var top = GameCubeVideoOutput.DecodeFramebufferAddress(ReadRegister32(TopFieldBase));
            return top != 0
                ? top
                : GameCubeVideoOutput.DecodeFramebufferAddress(ReadRegister32(BottomFieldBase));
        }
    }

    /// <summary>
    /// Bit 31 of a display interrupt: it has fired and not been acknowledged.
    /// Software clears it by writing a one.
    /// </summary>
    /// <remarks>
    /// These two were the wrong way round, and the mistake was invisible from
    /// every direction. A game arms an interrupt by setting bit 28; PixelCube
    /// tested bit 31 for "armed", found it clear, and skipped the interrupt as
    /// disabled — forever. The video interface was configured, unmasked at the
    /// processor interface, and running against a correct video clock, and
    /// still never raised anything. YAGCD: "31 i INT - Interrupt Status
    /// (1=Active) (Write to clear)", "28 e ENB - Interrupt Enable Bit".
    /// </remarks>
    private const uint DisplayInterruptFired = 1u << 31;

    /// <summary>Bit 28: this display interrupt is armed.</summary>
    private const uint DisplayInterruptEnabled = 1u << 28;

    /// <summary>The line a display interrupt fires on, in bits 16 to 25.</summary>
    private const uint DisplayInterruptLineMask = 0x3FF;

    /// <summary>
    /// Lines in one NTSC field, and the core cycles each one takes.
    /// </summary>
    /// <remarks>
    /// 486 MHz over 59.94 fields of 263 lines is a shade under 30,830 cycles a
    /// line. PixelCube retires about one instruction per cycle, so the video
    /// clock advances against instructions retired. That keeps video time
    /// correct relative to the CPU, which is what a frame loop actually
    /// depends on — a game never observes wall-clock seconds, only how much of
    /// its own work fits between two fields.
    /// </remarks>
    private const int LinesPerField = 263;

    private const int CoreCyclesPerLine = 30_830;

    private long _videoCycles;
    private int _verticalLine = 1;

    /// <summary>
    /// How often a register that does have modelled behaviour still gets a
    /// line in the log. The tally counts every access regardless; this only
    /// decides how loud a hot register is.
    /// </summary>
    private const long HotRegisterInterval = 250_000;

    private readonly byte[] _registers = new byte[Size];
    private readonly GameCubeTraceLog _trace;
    private readonly GameCubeMemory _memory;

    /// <summary>
    /// The disc in the drive. Null until one is inserted, which is the state
    /// a synthetic test or a bare memory harness runs in.
    /// </summary>
    public GameCubeDisc? Disc { get; set; }

    /// <summary>The message the DSP has waiting for the CPU, if any.</summary>
    private uint _mailToCpu;
    private bool _mailToCpuWaiting;
    private ushort _mailToDspHigh;
    private bool _dspInitInProgress;

    /// <summary>How many gather pipe writes have been reported verbatim.</summary>
    private int _gatherPipeWrites;

    public GameCubeHardware(GameCubeTraceLog trace, GameCubeMemory memory)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(memory);
        _trace = trace;
        _memory = memory;
        Graphics = new GameCubeCommandProcessor(memory, trace);
        Reset();
    }

    /// <summary>The command processor, which decodes what the FIFO carries.</summary>
    public GameCubeCommandProcessor Graphics { get; }

    /// <summary>
    /// Whether any device is asserting an interrupt the CPU has not masked
    /// off. The single line the processor interface presents to the Gekko.
    /// </summary>
    public bool IsInterruptPending =>
        (ReadRegister32(InterruptCause) & ReadRegister32(InterruptMask)) != 0;

    /// <summary>
    /// Advances the video clock by the core cycles that have passed, firing
    /// any display interrupt whose line the beam has reached.
    /// </summary>
    public void Advance(long coreCycles)
    {
        if (coreCycles <= 0)
        {
            return;
        }

        _videoCycles += coreCycles;
        while (_videoCycles >= CoreCyclesPerLine)
        {
            _videoCycles -= CoreCyclesPerLine;
            _verticalLine = _verticalLine >= LinesPerField ? 1 : _verticalLine + 1;
            WriteRegister16(VerticalPosition, (ushort)_verticalLine);
            FireDisplayInterrupts();
            if (_verticalLine == 1)
            {
                PollControllers();
            }
        }
    }

    /// <summary>
    /// Raises any armed display interrupt whose chosen line is the one the
    /// beam has just reached.
    /// </summary>
    /// <remarks>
    /// This is where a frame comes from as far as the operating system is
    /// concerned: <c>VIWaitForRetrace</c> arms one of these at the top of the
    /// blanking interval and sleeps, and the interrupt is the only thing that
    /// ever wakes it.
    /// </remarks>
    private void FireDisplayInterrupts()
    {
        for (var index = 0; index < DisplayInterruptCount; index++)
        {
            var offset = DisplayInterrupt0 + ((uint)index * 4);
            var display = ReadRegister32(offset);
            if ((display & DisplayInterruptEnabled) == 0 ||
                (display & DisplayInterruptFired) != 0)
            {
                continue;
            }

            // The chosen line is stored one-based in bits 16 to 25.
            var line = (display >> 16) & DisplayInterruptLineMask;
            if (line != (uint)_verticalLine)
            {
                continue;
            }

            WriteRegister32(offset, display | DisplayInterruptFired);
            RaiseInterrupt(VideoInterruptCause);
            _trace.WriteEvery(
                GameCubeTraceChannel.Video,
                GameCubeTraceLevel.Debug,
                "vi/display-interrupt",
                60,
                $"display interrupt {index} at line {line}");
        }
    }

    // ------------------------------------------------------------- graphics

    /// <summary>
    /// Takes a write to the write-gather pipe and puts it in the FIFO, then
    /// lets the command processor read as much of the FIFO as is complete.
    /// </summary>
    /// <remarks>
    /// On hardware these are two independent parties: the CPU bursts thirty-two
    /// bytes at a time into a ring in main memory, and the graphics processor
    /// consumes it at its own pace, with the gap between the two pointers being
    /// what a game watches to know whether it may send more. PixelCube consumes
    /// immediately, so the gap is always zero and a game is never made to wait —
    /// which is the right way round to be wrong. A FIFO that reported itself
    /// full would stall a game against hardware that is not actually busy.
    /// </remarks>
    private void WriteToGatherPipe(int size, uint value)
    {
        var start = ReadPointer(FifoBaseLow, FifoBaseHigh);
        var end = ReadPointer(FifoEndLow, FifoEndHigh);
        if (end <= start)
        {
            _trace.WriteOnce(
                GameCubeTraceChannel.Graphics,
                GameCubeTraceLevel.Warning,
                "gx/fifo-unconfigured",
                $"write-gather pipe used before the FIFO was set up " +
                $"(base=0x{start:X8} end=0x{end:X8}); the write is dropped");
            return;
        }

        var write = ReadPointer(FifoWritePointerLow, FifoWritePointerHigh);
        var reset = write < start || write >= end;
        if (reset)
        {
            write = start;
        }

        // Every write for the first stretch, verbatim. The command stream is
        // built a byte or a word at a time, and the only way to tell a decoder
        // bug from a plumbing bug is to see what was actually pushed and where
        // it landed. Bounded, so this cannot become the session.
        if (_gatherPipeWrites < 64)
        {
            _gatherPipeWrites++;
            _trace.Write(
                GameCubeTraceChannel.Graphics,
                GameCubeTraceLevel.Information,
                $"gather pipe #{_gatherPipeWrites}: {size}-byte 0x{value:X8} -> 0x{write:X8}" +
                (reset ? " (write pointer was out of bounds and was reset to base)" : string.Empty));
        }

        for (var shift = (size - 1) * 8; shift >= 0; shift -= 8)
        {
            _memory.WriteByte(write, (byte)(value >> shift));
            if (++write >= end)
            {
                write = start;
            }
        }

        WritePointer(FifoWritePointerLow, FifoWritePointerHigh, write);
        Graphics.NoteFifoConfiguration(
            start,
            end,
            ReadPointer(FifoReadPointerLow, FifoReadPointerHigh),
            write);
        DrainFifo(start, end, write);
    }

    /// <summary>
    /// Decodes everything between the read and write pointers, in two runs when
    /// the ring has wrapped between them.
    /// </summary>
    private void DrainFifo(uint start, uint end, uint write)
    {
        var read = ReadPointer(FifoReadPointerLow, FifoReadPointerHigh);
        if (read < start || read >= end)
        {
            read = start;
        }

        if (read > write)
        {
            // A command that straddles the wrap is left alone until the
            // pointers are the simple way round again; decoding the tail of the
            // ring as though the head followed it would produce exactly the
            // resynchronisation this decoder refuses to do.
            read = Graphics.Decode(read, end);
            if (read >= end)
            {
                read = start;
            }
        }

        if (read <= write)
        {
            read = Graphics.Decode(read, write);
        }

        WritePointer(FifoReadPointerLow, FifoReadPointerHigh, read);
        WritePointer(
            FifoDistanceLow,
            FifoDistanceHigh,
            write >= read ? write - read : end - read + (write - start));
    }

    /// <summary>Reads one of the FIFO's split 32-bit pointers.</summary>
    private uint ReadPointer(uint low, uint high) =>
        ((uint)ReadRegister16(high) << 16) | ReadRegister16(low);

    private void WritePointer(uint low, uint high, uint value)
    {
        WriteRegister16(low, (ushort)value);
        WriteRegister16(high, (ushort)(value >> 16));
    }

    /// <summary>
    /// Polls the controllers once a field and says so, which is the one
    /// interrupt on a GameCube that keeps arriving whether or not a game asks
    /// for anything.
    /// </summary>
    /// <remarks>
    /// The serial interface reads every enabled port automatically at a rate
    /// set in video lines, and raises its read-status interrupt each time the
    /// data is refreshed. Without it the pad library's thread sleeps waiting
    /// for input that never arrives — and because a blocked thread is invisible
    /// from the outside, the symptom is an operating system with nothing
    /// runnable, idling forever, rather than anything that looks like input.
    /// </remarks>
    private void PollControllers()
    {
        if ((ReadRegister32(SerialPoll) & SerialPollEnabledPorts) == 0)
        {
            return;
        }

        WriteRegister32(
            SerialCommunicationStatus,
            ReadRegister32(SerialCommunicationStatus) | ReadStatusInterrupt);
        RefreshSerialInterrupt();
        _trace.WriteEvery(
            GameCubeTraceChannel.Input,
            GameCubeTraceLevel.Debug,
            "si/poll",
            60,
            $"controllers polled; ports enabled=0x{ReadRegister32(SerialPoll) & SerialPollEnabledPorts:X2}");
    }

    /// <summary>
    /// Asserts or drops the serial interface's interrupt from its two
    /// status-and-enable pairs.
    /// </summary>
    private void RefreshSerialInterrupt()
    {
        var status = ReadRegister32(SerialCommunicationStatus);
        var asserted =
            ((status & TransferCompleteInterrupt) != 0 &&
             (status & TransferCompleteInterruptMask) != 0) ||
            ((status & ReadStatusInterrupt) != 0 &&
             (status & ReadStatusInterruptMask) != 0);

        if (asserted)
        {
            RaiseInterrupt(SerialInterruptCause);
            return;
        }

        WriteRegister32(InterruptCause, ReadRegister32(InterruptCause) & ~SerialInterruptCause);
    }

    /// <summary>Asserts a device's interrupt cause.</summary>
    private void RaiseInterrupt(uint cause) =>
        WriteRegister32(InterruptCause, ReadRegister32(InterruptCause) | cause);

    /// <summary>
    /// Drops the video interrupt once software has acknowledged every display
    /// interrupt that raised it. The cause is shared by all four, so it stays
    /// asserted until none of them is still flagged.
    /// </summary>
    private void RefreshVideoInterrupt()
    {
        for (var index = 0; index < DisplayInterruptCount; index++)
        {
            if ((ReadRegister32(DisplayInterrupt0 + ((uint)index * 4)) &
                 DisplayInterruptFired) != 0)
            {
                return;
            }
        }

        WriteRegister32(InterruptCause, ReadRegister32(InterruptCause) & ~VideoInterruptCause);
    }

    /// <summary>Whether an address falls inside the register window.</summary>
    public static bool Contains(uint address)
    {
        var physical = address & 0x0FFF_FFFF;
        return physical >= (Base & 0x0FFF_FFFF) &&
               physical < ((Base & 0x0FFF_FFFF) + Size);
    }

    public void Reset()
    {
        Array.Clear(_registers);
        Graphics.Reset();
        _gatherPipeWrites = 0;

        // The DVD drive reports itself present and configured; nothing else
        // starts life with a value a game would notice.
        WriteRegister32(DvdConfiguration, 1);
    }

    public uint Read(uint address, int size)
    {
        var offset = address & (Size - 1);
        ApplyReadSideEffects(offset, size);

        var value = size switch
        {
            1 => _registers[offset],
            2 => BinaryPrimitives.ReadUInt16BigEndian(_registers.AsSpan((int)offset, 2)),
            _ => BinaryPrimitives.ReadUInt32BigEndian(_registers.AsSpan((int)offset, 4))
        };

        Report(offset, "read", size, value);
        return value;
    }

    public void Write(uint address, int size, uint value)
    {
        var offset = address & (Size - 1);

        // The write-gather pipe is not a register. It is a hole that everything
        // written to it falls through into the FIFO, so it must be handled
        // before the register store — writing it into the register file would
        // both lose the command and leave a byte of graphics data pretending to
        // be hardware state.
        if (offset >= WriteGatherPipe)
        {
            WriteToGatherPipe(size, value);
            Report(offset, "write", size, value);
            return;
        }

        // An external interface channel's three status bits are acknowledged by
        // writing ones, in the same word as their enables and the device select.
        if (size == 4 && Array.IndexOf(ExternalStatusRegisters, offset) >= 0)
        {
            var kept = ReadRegister32(offset) & ExternalInterruptStatus & ~value;
            WriteRegister32(offset, (value & ~ExternalInterruptStatus) | kept);
            RefreshExternalInterrupt();
            Report(offset, "write", size, value);
            return;
        }

        // The serial interface's two status bits are acknowledged the same way,
        // and sit in the same word as the enables and the transfer start bit.
        if (size == 4 && offset == SerialCommunicationStatus)
        {
            var kept = ReadRegister32(offset) & SerialInterruptStatus & ~value;
            WriteRegister32(offset, (value & ~SerialInterruptStatus) | kept);

            // A transfer finishes before the write that started it returns, so
            // its completion interrupt is already waiting when software looks.
            if ((value & 1) != 0)
            {
                WriteRegister32(
                    offset,
                    (ReadRegister32(offset) & ~1u) | TransferCompleteInterrupt);
            }

            RefreshSerialInterrupt();
            Report(offset, "write", size, value);
            return;
        }

        // A display interrupt's status bit is acknowledged by writing a one to
        // it, while every other bit in the same word is ordinary configuration
        // the game rewrites each time it re-arms. Storing the value wholesale
        // would set the very bit the write was clearing, so the interrupt would
        // re-assert the instant its handler returned.
        if (size == 4 &&
            offset >= DisplayInterrupt0 &&
            offset < DisplayInterrupt0 + (DisplayInterruptCount * 4) &&
            (offset - DisplayInterrupt0) % 4 == 0)
        {
            var kept = ReadRegister32(offset) & DisplayInterruptFired & ~value;
            WriteRegister32(offset, (value & ~DisplayInterruptFired) | kept);
            RefreshVideoInterrupt();
            Report(offset, "write", size, value);
            return;
        }

        // Acknowledgement, not assignment — and handled before the store,
        // because storing first would destroy the very causes being
        // acknowledged and leave the register reading as whatever software
        // happened to write.
        if (offset == InterruptCause && size == 4)
        {
            WriteRegister32(InterruptCause, ReadRegister32(InterruptCause) & ~value);
            Report(offset, "write", size, value);
            return;
        }

        switch (size)
        {
            case 1:
                _registers[offset] = (byte)value;
                break;
            case 2:
                BinaryPrimitives.WriteUInt16BigEndian(_registers.AsSpan((int)offset, 2), (ushort)value);
                break;
            default:
                BinaryPrimitives.WriteUInt32BigEndian(_registers.AsSpan((int)offset, 4), value);
                break;
        }

        ApplyWriteSideEffects(offset, size, value);
        Report(offset, "write", size, value);
    }

    /// <summary>
    /// Adjusts registers whose value depends on what the hardware would have
    /// done since the last look, rather than on what was last written.
    /// </summary>
    private void ApplyReadSideEffects(uint offset, int size)
    {
        switch (offset)
        {
            case DspMailToCpuHigh:
            {
                // A 32-bit read takes the whole message at once; a 16-bit read
                // takes the high word and leaves the message waiting until the
                // low word is read.
                WriteRegister32(DspMailToCpuHigh, _mailToCpuWaiting ? _mailToCpu : 0);
                if (size == 4)
                {
                    TakeMailToCpu();
                }

                break;
            }

            case DspMailToCpuLow:
                WriteRegister16(
                    DspMailToCpuLow,
                    _mailToCpuWaiting ? (ushort)_mailToCpu : (ushort)0);
                TakeMailToCpu();
                break;

            case DspMailToDspHigh:
                // The DSP consumes anything sent to it immediately, so the
                // "still unread" flag is never set when the CPU looks.
                WriteRegister16(DspMailToDspHigh, _mailToDspHigh);
                break;

            case DspControlStatus or DspControlStatus - 1:
            {
                // The reset bit is set by software and cleared by hardware
                // when the DSP comes back. Nothing here takes time, so it is
                // already clear by the time anyone looks — which is exactly
                // the handshake DSPInit spins on.
                var control = ReadRegister16(DspControlStatus);
                WriteRegister16(DspControlStatus, (ushort)(control & ~(DspReset | DspDmaInProgress)));
                break;
            }

            case SerialCommunicationStatus:
            case SerialStatus:
                // Controller transfers complete instantly: clear the transfer
                // start bit so a poll for completion ends.
                WriteRegister32(
                    SerialCommunicationStatus,
                    ReadRegister32(SerialCommunicationStatus) & ~1u);
                break;

            case DvdControl:
                // The same for a DVD command: no transfer is outstanding.
                WriteRegister32(DvdControl, ReadRegister32(DvdControl) & ~TransferStart);
                break;

            case AramMode:
                // Preserved rather than replaced: the CPU writes a mode into
                // the upper bits of this same register.
                WriteRegister16(AramMode, (ushort)(ReadRegister16(AramMode) | AramReady));
                break;

            default:
                // Every EXI channel behaves the same way. The memory card and
                // the real-time clock both hang off this bus, and OSInit reads
                // the console's SRAM through it before anything else — so a
                // transfer that never reports completion stops the boot just
                // as surely as the DSP reset did.
                if (Array.IndexOf(ExternalControlRegisters, offset) >= 0)
                {
                    WriteRegister32(offset, ReadRegister32(offset) & ~TransferStart);
                }

                break;
        }
    }

    /// <summary>
    /// Finishes an external interface transfer and says so, which is the half
    /// that was missing: the transfer already completed instantly, silently.
    /// </summary>
    /// <remarks>
    /// The memory card, the real-time clock and the console's own SRAM all hang
    /// off this bus, and the card library is asynchronous — it starts a
    /// transfer, registers a callback and sleeps. Completing without raising
    /// the interrupt leaves that callback unrun and the thread asleep for good.
    /// </remarks>
    private void CompleteExternalTransfer(uint controlOffset)
    {
        WriteRegister32(controlOffset, ReadRegister32(controlOffset) & ~TransferStart);

        // The status register sits 0x0C below its channel's control register.
        var status = controlOffset - 0x0C;
        WriteRegister32(status, ReadRegister32(status) | ExternalTransferComplete);
        RefreshExternalInterrupt();
    }

    /// <summary>
    /// Asserts or drops the external interface's interrupt from every
    /// channel's three status-and-enable pairs.
    /// </summary>
    private void RefreshExternalInterrupt()
    {
        foreach (var channel in ExternalStatusRegisters)
        {
            var status = ReadRegister32(channel);
            if (((status & ExternalInterrupt) != 0 && (status & ExternalInterruptMask) != 0) ||
                ((status & ExternalTransferComplete) != 0 &&
                 (status & ExternalTransferCompleteMask) != 0) ||
                ((status & ExternalInsertion) != 0 && (status & ExternalInsertionMask) != 0))
            {
                RaiseInterrupt(ExternalInterruptCause);
                return;
            }
        }

        WriteRegister32(InterruptCause, ReadRegister32(InterruptCause) & ~ExternalInterruptCause);
    }

    private void ApplyWriteSideEffects(uint offset, int size, uint value)
    {
        // "ARAM DMA is setup by writing to the various DSP_AR_DMA_* registers,
        // and initiated by writing to DSP_AR_DMA_SIZE_L." Those six halves are
        // written individually, so triggering only on a 32-bit store to the
        // size register meant every transfer a game set up half-word at a time
        // did nothing at all — silently, because the setup writes all landed.
        if (offset == AramDmaSizeLow || (offset == AramDmaControl && size == 4))
        {
            PerformAramTransfer();
            return;
        }

        if (offset == DvdControl && (value & TransferStart) != 0)
        {
            ExecuteDvdCommand();
            return;
        }

        if ((value & TransferStart) != 0 &&
            Array.IndexOf(ExternalControlRegisters, offset) >= 0)
        {
            CompleteExternalTransfer(offset);
            return;
        }

        if (offset >= DisplayInterrupt0 &&
            offset < DisplayInterrupt0 + (DisplayInterruptCount * 4))
        {
            RefreshVideoInterrupt();
            return;
        }

        switch (offset)
        {
            case DspMailToDspHigh:
                _mailToDspHigh = (ushort)value;
                return;

            case DspMailToDspLow:
                // Writing the low word is what sends the message.
                HandleMailToDsp(((uint)_mailToDspHigh << 16) | (ushort)value);
                return;
        }

        if (offset is DspControlStatus or DspControlStatus - 1 || (size == 4 && offset == DspInterface + 0x08))
        {
            if ((value & DspReset) != 0)
            {
                ResetDsp();
            }

            // Clearing the init bit hands control from the boot ROM to the
            // microcode it loads out of ARAM, which announces itself. This is
            // the second half of the handshake: without it the CPU takes the
            // ROM's greeting and then waits forever for the microcode's.
            var initNow = (value & DspInitInProgress) != 0;
            if (_dspInitInProgress && !initNow)
            {
                PostMailToCpu(DspInitUCodeReadyMail, "init-audio-system microcode ready");
            }

            _dspInitInProgress = initNow;

            var control = ReadRegister16(DspControlStatus);

            // Reset completes immediately, no DMA is ever outstanding, and the
            // interrupt status bits are cleared by writing one to them.
            control &= unchecked((ushort)~(DspReset | DspDmaInProgress));
            control &= unchecked((ushort)~(value & DspInterruptStatus));
            WriteRegister16(DspControlStatus, control);

            // Acknowledging a status bit, or changing an enable, can be what
            // drops the line the processor interface is holding.
            RefreshDspInterrupt();
        }
    }

    /// <summary>
    /// Asserts or drops the DSP's interrupt at the processor interface, from
    /// the three status-and-enable pairs inside the DSP block.
    /// </summary>
    /// <remarks>
    /// Setting a status bit inside the DSP is only half of an interrupt, and
    /// PixelCube did only that half. ARAM transfers completed, the ARAM status
    /// bit went up, and nothing ever reached the CPU — so the operating
    /// system's handler never ran, the callback never fired, and Super Mario
    /// Sunshine sat on a two-instruction loop waiting for a global that only
    /// that callback sets. A device that finishes without saying so is
    /// indistinguishable from one that never finishes.
    /// </remarks>
    private void RefreshDspInterrupt()
    {
        var control = ReadRegister16(DspControlStatus);
        var asserted =
            ((control & AudioDmaInterrupt) != 0 && (control & AudioDmaInterruptMask) != 0) ||
            ((control & AramInterrupt) != 0 && (control & AramInterruptMask) != 0) ||
            ((control & DspMailInterrupt) != 0 && (control & DspMailInterruptMask) != 0);

        if (asserted)
        {
            RaiseInterrupt(DspInterruptCause);
            return;
        }

        WriteRegister32(InterruptCause, ReadRegister32(InterruptCause) & ~DspInterruptCause);
    }

    /// <summary>
    /// Runs the command sitting in the drive's command registers.
    /// </summary>
    /// <remarks>
    /// Nothing here takes time: the transfer is finished before the write that
    /// started it returns, and the completion flags are already raised when the
    /// game first looks. That is the same shortcut the other interfaces take,
    /// and it is safe for the same reason — a game cannot tell a very fast
    /// drive from an instantaneous one, only a silent one from a working one.
    /// </remarks>
    private void ExecuteDvdCommand()
    {
        var command = ReadRegister32(DvdCommand0) >> 24;
        switch (command)
        {
            case DvdReadCommand:
                // The disc offset is stored in units of four bytes, which is
                // what lets a 32-bit field address a 1.4 GB disc.
                PerformDvdRead(
                    (long)ReadRegister32(DvdCommand1) << 2,
                    (int)ReadRegister32(DvdDmaLength),
                    ReadRegister32(DvdDmaAddress));
                break;

            case DvdReadIdCommand:
                PerformDvdRead(0, (int)ReadRegister32(DvdDmaLength), ReadRegister32(DvdDmaAddress));
                break;

            case DvdRequestErrorCommand:
                WriteRegister32(DvdImmediate, 0);
                break;

            default:
                _trace.WriteOnce(
                    GameCubeTraceChannel.Unimplemented,
                    GameCubeTraceLevel.Warning,
                    DvdCommandKey(command),
                    $"DVD command 0x{command:X2} is not implemented; the transfer is reported " +
                    "complete without anything being moved");
                break;
        }

        WriteRegister32(DvdControl, ReadRegister32(DvdControl) & ~TransferStart);
        WriteRegister32(DvdStatus, ReadRegister32(DvdStatus) | DvdTransferComplete);
    }

    private void PerformDvdRead(long discOffset, int length, uint destination)
    {
        if (Disc is null)
        {
            _trace.WriteOnce(
                GameCubeTraceChannel.Unimplemented,
                GameCubeTraceLevel.Warning,
                "dvd/no-disc",
                "a DVD read was issued with no disc attached to the drive");
            return;
        }

        if (length is <= 0 or > MaximumDvdTransfer || discOffset < 0)
        {
            _trace.WriteOnce(
                GameCubeTraceChannel.Unimplemented,
                GameCubeTraceLevel.Warning,
                "dvd/bad-transfer",
                $"a DVD read of {length} bytes from 0x{discOffset:X} was refused as implausible");
            return;
        }

        _memory.Write(destination, Disc.Read(discOffset, length));
        _trace.WriteEvery(
            GameCubeTraceChannel.Disc,
            GameCubeTraceLevel.Debug,
            "dvd/read",
            64,
            $"DVD read: disc=0x{discOffset:X8} length=0x{length:X} -> 0x{destination:X8}");
    }

    private string DvdCommandKey(uint command)
    {
        if (_dvdCommandKeys.TryGetValue(command, out var key))
        {
            return key;
        }

        key = $"dvd/command/0x{command:X2}";
        _dvdCommandKeys[command] = key;
        return key;
    }

    private readonly Dictionary<uint, string> _dvdCommandKeys = [];

    /// <summary>
    /// Brings the DSP out of reset, which on real hardware means its boot ROM
    /// starts and announces itself to the CPU.
    /// </summary>
    private void ResetDsp()
    {
        _mailToDspHigh = 0;
        PostMailToCpu(DspRomReadyMail, "boot ROM ready");
    }

    /// <summary>Makes a message available to the CPU.</summary>
    private void PostMailToCpu(uint mail, string description)
    {
        _mailToCpu = mail;
        _mailToCpuWaiting = true;
        _trace.Write(
            GameCubeTraceChannel.Dsp,
            GameCubeTraceLevel.Debug,
            $"DSP -> CPU mail 0x{mail:X8} ({description})");
    }

    private void TakeMailToCpu()
    {
        if (!_mailToCpuWaiting)
        {
            return;
        }

        _mailToCpuWaiting = false;
        WriteRegister32(DspMailToCpuHigh, _mailToCpu & ~0x8000_0000u);
    }

    /// <summary>
    /// Receives a message the CPU sent the DSP.
    /// </summary>
    /// <remarks>
    /// Nothing answers yet beyond the boot ROM's own announcement. What this
    /// does is name every distinct message, so the conversation a game expects
    /// shows up in the tally as a list of values rather than as a hang — the
    /// next step is reading that list, not guessing at it. Replying with
    /// invented words would be worse than silence: the game would carry on
    /// believing its audio system had started.
    /// </remarks>
    private void HandleMailToDsp(uint mail)
    {
        _trace.WriteOnce(
            GameCubeTraceChannel.Unimplemented,
            GameCubeTraceLevel.Information,
            MailKey(mail),
            $"CPU -> DSP mail 0x{mail:X8} has no reply; PixelCube emulates the DSP's boot ROM " +
            "announcement only");
    }

    private string MailKey(uint mail)
    {
        if (_mailKeys.TryGetValue(mail, out var key))
        {
            return key;
        }

        key = $"dsp/mail/0x{mail:X8}";
        _mailKeys[mail] = key;
        return key;
    }

    private readonly Dictionary<uint, string> _mailKeys = [];

    /// <summary>
    /// Copies between main memory and ARAM, then raises the completion bit the
    /// caller is about to wait on.
    /// </summary>
    /// <remarks>
    /// Real hardware takes time over this and interrupts when it finishes.
    /// Nothing here models time, so the transfer is done before the write
    /// returns and the interrupt is already pending — which is indistinguishable
    /// from a very fast machine, and is what lets ARAM sizing complete.
    /// </remarks>
    private void PerformAramTransfer()
    {
        // Read back rather than taken from the store that triggered it: the
        // trigger is a sixteen-bit write of the size's low half, which carries
        // neither the direction bit nor the upper size bits.
        var control = ReadRegister32(AramDmaControl);
        var length = (int)(control & 0x03FF_FFFF);

        // The low five bits of both addresses are hardwired to zero on
        // hardware, which forces every transfer onto a 32-byte boundary.
        var mainAddress = ReadRegister32(AramDmaMainAddress) & 0x03FF_FFE0;
        var aramAddress = ReadRegister32(AramDmaAramAddress) & 0x03FF_FFE0;
        var fromAram = (control & AramDmaFromAram) != 0;

        var main = _memory.MainMemory;
        var auxiliary = _memory.AuxiliaryMemory;
        if (length > 0 && mainAddress + length <= (uint)main.Length)
        {
            // ARAM addresses wrap. A retail console has 16 MB across two banks
            // addressed contiguously, and the DMA's address register is far
            // wider than that, so an address past the end comes back round to
            // the start. That is not a detail — it is how the operating system
            // measures ARAM: it writes a pattern at 16 MB exactly and checks
            // whether the bytes at zero changed, and an expansion board is
            // reported when they did not. Skipping the transfer, which is what
            // this did, leaves the probe unable to tell the two apart, so a
            // retail console reports memory it does not have and every audio
            // allocation afterwards points past the end of it.
            var moved = 0;
            while (moved < length)
            {
                var aramOffset = (int)((aramAddress + (uint)moved) % (uint)auxiliary.Length);
                var chunk = Math.Min(length - moved, auxiliary.Length - aramOffset);
                var fromMain = main.Slice((int)mainAddress + moved, chunk);
                var inAram = auxiliary.Slice(aramOffset, chunk);
                if (fromAram)
                {
                    inAram.CopyTo(fromMain);
                }
                else
                {
                    fromMain.CopyTo(inAram);
                }

                moved += chunk;
            }
        }
        else if (length > 0)
        {
            // Only main memory can genuinely be out of range now.
            _trace.WriteOnce(
                GameCubeTraceChannel.Unimplemented,
                GameCubeTraceLevel.Warning,
                "aram/out-of-range",
                $"ARAM transfer of {length} bytes at main memory 0x{mainAddress:X8} " +
                "falls outside memory and was skipped");
        }

        WriteRegister16(
            DspControlStatus,
            (ushort)(ReadRegister16(DspControlStatus) | AramInterrupt));
        RefreshDspInterrupt();
        _trace.Write(
            GameCubeTraceChannel.Dsp,
            GameCubeTraceLevel.Debug,
            $"ARAM DMA {(fromAram ? "ARAM->MEM" : "MEM->ARAM")} " +
            $"main=0x{mainAddress:X8} aram=0x{aramAddress:X8} length={length}");
    }

    private ushort ReadRegister16(uint offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(_registers.AsSpan((int)offset, 2));

    private void WriteRegister16(uint offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(_registers.AsSpan((int)offset, 2), value);

    private uint ReadRegister32(uint offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(_registers.AsSpan((int)offset, 4));

    private void WriteRegister32(uint offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(_registers.AsSpan((int)offset, 4), value);

    /// <summary>
    /// Records the access, and separately records the ones that only appear to
    /// work because the value happens to be stored.
    /// </summary>
    private void Report(uint offset, string operation, int size, uint value)
    {
        _trace.Write(
            GameCubeTraceChannel.Registers,
            GameCubeTraceLevel.Debug,
            $"{DescribeBlock(offset)}+0x{offset & 0x3FF:X3} {operation}{size * 8} = 0x{value:X}");

        var key = RegisterKey(offset, operation);
        if (!IsModelled(offset))
        {
            // Still on the work list. A register that reads back what was
            // written is not emulated, it is merely quiet, and the difference
            // only shows up much later if nothing keeps count.
            _trace.WriteOnce(
                GameCubeTraceChannel.Unimplemented,
                GameCubeTraceLevel.Information,
                key,
                $"{DescribeBlock(offset)}+0x{offset & 0x3FF:X3} {operation} has no modelled " +
                "behaviour; the value is stored and read back");
            return;
        }

        // Modelled registers are counted too. "Modelled" is not "correct": a
        // handshake that answers the wrong thing produces exactly the spin
        // loop an unmodelled one does, and the moment a register is declared
        // handled it drops off the unimplemented list — so without this, the
        // second spin loop is invisible in a way the first one never was.
        _trace.WriteEvery(
            GameCubeTraceChannel.Registers,
            GameCubeTraceLevel.Information,
            key,
            HotRegisterInterval,
            $"{DescribeBlock(offset)}+0x{offset & 0x3FF:X3} {operation} is modelled and busy");
    }

    private static bool IsModelled(uint offset) => offset switch
    {
        DspControlStatus or DspControlStatus - 1 => true,
        DspMailToDspHigh or DspMailToDspLow => true,
        DspMailToCpuHigh or DspMailToCpuLow => true,
        DvdConfiguration or DvdControl or DvdStatus => true,
        DvdCommand0 or DvdCommand1 or DvdCommand2 => true,
        DvdDmaAddress or DvdDmaLength or DvdImmediate => true,
        AramMode => true,
        InterruptCause or InterruptMask or VerticalPosition => true,
        >= DisplayInterrupt0 and < DisplayInterrupt0 + (DisplayInterruptCount * 4) => true,
        SerialCommunicationStatus or SerialStatus => true,

        // The FIFO's bounds and pointers, and the write-gather pipe they are
        // fed through. These drive the command processor rather than merely
        // reading back, so calling them unimplemented would be the wrong way
        // round — but they stay counted, because "modelled" has been wrong
        // before and a count is the only thing that tells the two apart.
        >= FifoBaseLow and <= FifoReadPointerHigh => true,
        >= WriteGatherPipe => true,

        _ => Array.IndexOf(ExternalControlRegisters, offset) >= 0
    };

    private readonly Dictionary<(uint Offset, string Operation), string> _keys = [];

    private string RegisterKey(uint offset, string operation)
    {
        if (_keys.TryGetValue((offset, operation), out var key))
        {
            return key;
        }

        key = $"register/{operation}/{DescribeBlock(offset)}+0x{offset & 0x3FF:X3}";
        _keys[(offset, operation)] = key;
        return key;
    }

    /// <summary>Names the block an offset in the window belongs to.</summary>
    public static string DescribeBlock(uint offset) => offset switch
    {
        < PixelEngine => "CP",
        < VideoInterface => "PE",
        < ProcessorInterface => "VI",
        < MemoryInterface => "PI",
        < DspInterface => "MI",
        < DvdInterface => "DSP",
        < SerialInterface => "DI",
        < ExternalInterface => "SI",
        < AudioInterface => "EXI",
        < 0x7000 => "AI",
        _ => "GX"
    };
}
