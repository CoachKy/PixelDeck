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

    /// <summary>
    /// The audio direct memory access: where sound is read from, how much of it
    /// there is, and how much is left.
    /// </summary>
    /// <remarks>
    /// This is the heartbeat of a running audio system. It reads thirty-two
    /// bytes at a time at four thousand blocks a second — the rate the sampler
    /// consumes stereo pairs — and when the last block of a buffer has gone it
    /// reloads from these registers and interrupts. That interrupt is what runs
    /// the callback that hands the game its next buffer, and a game waits for
    /// it before believing its audio system is alive.
    /// </remarks>
    private const uint AudioDmaStartHigh = DspInterface + 0x30;
    private const uint AudioDmaStartLow = DspInterface + 0x32;
    private const uint AudioDmaControlLength = DspInterface + 0x36;
    private const uint AudioDmaBlocksLeft = DspInterface + 0x3A;

    private const uint AudioDmaEnabled = 0x8000;
    private const int AudioDmaBlockBytes = 32;

    /// <summary>Core cycles between blocks: four thousand of them a second.</summary>
    private const long CoreCyclesPerAudioBlock = 486_000_000 / 4_000;

    private uint _audioDmaSource;
    private uint _audioDmaBlocks;
    private uint _audioDmaRemaining;
    private long _audioDmaCycles;

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
    /// <summary>
    /// The drive's three interrupts, each a status bit with its enable in the
    /// bit below it. Status is cleared by writing a one.
    /// </summary>
    /// <remarks>
    /// Transfer complete is bit 4. It was bit 2 here, which is the device error
    /// flag — so every successful read reported a drive fault instead of a
    /// completed transfer, and the one bit software actually waits on was never
    /// set at all.
    /// </remarks>
    private const uint DvdDeviceErrorMask = 1u << 1;
    private const uint DvdDeviceError = 1u << 2;
    private const uint DvdTransferCompleteMask = 1u << 3;
    private const uint DvdTransferComplete = 1u << 4;
    private const uint DvdBreakCompleteMask = 1u << 5;
    private const uint DvdBreakComplete = 1u << 6;

    private const uint DvdInterruptStatus =
        DvdDeviceError | DvdTransferComplete | DvdBreakComplete;

    /// <summary>The drive's bit in the processor interface.</summary>
    private const uint DvdInterruptCause = 1u << 2;

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

    /// <summary>
    /// The pixel engine's interrupt register, and the token a game reads back
    /// after one. Two independent interrupts share the word: a token, which a
    /// game plants in the command stream to find out when the graphics
    /// processor has reached that point, and finish, which means the whole
    /// drawing list is done.
    /// </summary>
    private const uint PixelEngineInterrupt = PixelEngine + 0x0A;
    private const uint PixelEngineToken = PixelEngine + 0x0E;

    private const ushort PixelEngineTokenEnable = 1 << 0;
    private const ushort PixelEngineFinishEnable = 1 << 1;
    private const ushort PixelEngineTokenStatus = 1 << 2;
    private const ushort PixelEngineFinishStatus = 1 << 3;

    private const ushort PixelEngineStatus = PixelEngineTokenStatus | PixelEngineFinishStatus;

    /// <summary>The pixel engine's two bits in the processor interface.</summary>
    private const uint PixelEngineTokenCause = 1u << 9;
    private const uint PixelEngineFinishCause = 1u << 10;

    /// <summary>
    /// The audio interface's control register, its free-running sample counter,
    /// and the count an interrupt is wanted at.
    /// </summary>
    private const uint AudioControl = AudioInterface + 0x00;
    private const uint AudioSampleCounter = AudioInterface + 0x08;
    private const uint AudioInterruptTiming = AudioInterface + 0x0C;

    private const uint AudioPlaying = 1u << 0;

    /// <summary>
    /// The streaming rate select. Set means 48 kHz and clear means 32 kHz,
    /// which is the opposite of what YAGCD states.
    /// </summary>
    /// <remarks>
    /// The documentation has this bit's sense backwards, and it is the kind of
    /// error only an implementation that has been run against real games would
    /// record. Reading it the documented way makes the streaming clock run at
    /// two-thirds speed exactly when a game asks for full speed, and the only
    /// caller that notices is the routine which times the clock deliberately.
    /// </remarks>
    private const uint AudioRate48kHz = 1u << 1;
    private const uint AudioInterruptMask = 1u << 2;
    private const uint AudioInterruptStatus = 1u << 3;
    private const uint AudioInterruptHeld = 1u << 4;
    private const uint AudioCounterReset = 1u << 5;

    /// <summary>The audio interface's bit in the processor interface.</summary>
    private const uint AudioInterruptCause = 1u << 5;

    /// <summary>
    /// Core cycles between stereo samples, at the two rates the streaming
    /// clock actually runs at.
    /// </summary>
    /// <remarks>
    /// Expressed as the divisors the hardware actually uses — 2250 and 3375
    /// against a 108 MHz reference — rather than as a frequency, because that
    /// is what the period is derived from and it divides exactly. The often
    /// quoted figure of 48,043 Hz describes what a real console puts out of its
    /// analogue stage; it is not the divisor, and using it as one moves the
    /// period by ten cycles in the wrong direction.
    /// </remarks>
    private const int AudioReferenceClock = 108_000_000;
    private const int AudioDivisor48kHz = 2250;
    private const int AudioDivisor32kHz = 3375;

    private const int CoreCyclesPerSample48kHz =
        (int)(486_000_000L * AudioDivisor48kHz / AudioReferenceClock);

    private const int CoreCyclesPerSample32kHz =
        (int)(486_000_000L * AudioDivisor32kHz / AudioReferenceClock);

    private long _audioCycles;

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

    /// <summary>Top and bottom field base addresses of the external framebuffer.</summary>
    private const uint VerticalPosition = VideoInterface + 0x2C;
    private const uint VerticalPosition2 = VideoInterface + 0x02;
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

    /// <summary>Bit 28 or Bit 31: this display interrupt is armed.</summary>
    private const uint DisplayInterruptEnabled = (1u << 28) | (1u << 31);

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
    /// <summary>
    /// Messages the DSP has sent that the CPU has not taken yet.
    /// </summary>
    /// <remarks>
    /// A queue, not a slot. Three messages are sent within a few microseconds
    /// of each other while a game is starting its audio system, and with one
    /// slot each overwrote the last before anything read it — the machine
    /// announced its boot ROM, announced its microcode, and announced its boot
    /// ROM again, and a game listening carefully heard only the third. Two
    /// thirds of the conversation was being destroyed by the act of continuing
    /// it.
    /// </remarks>
    private readonly Queue<uint> _mailToCpuQueue = new();

    /// <summary>Whether each queued message interrupts when it reaches the front.</summary>
    private readonly Queue<bool> _mailInterrupts = new();

    /// <summary>
    /// The message last taken. It stays readable after its "waiting" flag is
    /// dropped, because software reads the two halves separately and the value
    /// must survive between them.
    /// </summary>
    private uint _lastMailToCpu;
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
        if (PdGxNative.IsAvailable)
        {
            unsafe
            {
                _ = PdGxNative.pdgx_init((void*)memory.MainMemoryPointer, 24 * 1024 * 1024);
            }
        }
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
    /// Names the devices currently asserting an unmasked interrupt, so a
    /// delivery can be attributed rather than merely counted.
    /// </summary>
    public string PendingInterruptName
    {
        get
        {
            var asserted = ReadRegister32(InterruptCause) & ReadRegister32(InterruptMask);
            var names = new List<string>();
            for (var bit = 0; bit < InterruptNames.Length; bit++)
            {
                if ((asserted & (1u << bit)) != 0)
                {
                    names.Add(InterruptNames[bit]);
                }
            }

            return names.Count == 0 ? "none" : string.Join("+", names);
        }
    }

    /// <summary>The processor interface's interrupt sources, in bit order.</summary>
    private static readonly string[] InterruptNames =
    [
        "error", "reset", "dvd", "serial", "external-interface", "audio",
        "dsp", "memory", "video", "pe-token", "pe-finish", "command-fifo",
        "debug", "high-speed-port"
    ];

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

        AdvanceAudio(coreCycles);
        AdvanceMail(coreCycles);
        AdvanceDvd(coreCycles);
        AdvanceDspInit(coreCycles);
        AdvanceAudioDma(coreCycles);

        _videoCycles += coreCycles;
        while (_videoCycles >= CoreCyclesPerLine)
        {
            _videoCycles -= CoreCyclesPerLine;
            _verticalLine = _verticalLine >= LinesPerField ? 1 : _verticalLine + 1;
            WriteRegister16(VerticalPosition, (ushort)_verticalLine);
            WriteRegister16(VerticalPosition2, (ushort)_verticalLine);
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
            if (line != 0 && line != (uint)_verticalLine)
            {
                continue;
            }
            if (line == 0 && _verticalLine != 1)
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

    /// <summary>
    /// Drains any pending FIFO commands between the current read and write pointers.
    /// </summary>
    public void DrainFifo()
    {
        var start = ReadPointer(FifoBaseLow, FifoBaseHigh);
        var end = ReadPointer(FifoEndLow, FifoEndHigh);
        if (end <= start)
        {
            return;
        }

        var write = ReadPointer(FifoWritePointerLow, FifoWritePointerHigh);
        if (PdGxNative.IsAvailable)
        {
            var read = ReadPointer(FifoReadPointerLow, FifoReadPointerHigh);
            var physRead = read & 0x00FF_FFFF;
            var physWrite = write & 0x00FF_FFFF;
            if (physRead < physWrite)
            {
                PdGxNative.pdgx_process_fifo(physRead, physWrite);
            }
        }
        DrainFifo(start, end, write);
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
    /// <summary>The four GameCube controller ports.</summary>
    public GameCubeController[] Controllers { get; } = [new(), new(), new(), new()];

    /// <summary>DSP voice synthesizer for 8 active ADPCM channels.</summary>
    public DspVoiceSynthesizer AudioSynthesizer { get; } = new();

    /// <summary>Ring buffer output for stereo 16-bit PCM audio samples.</summary>
    public GameCubeAudioOutput AudioOutput { get; } = new();

    public void PollControllersForTest() => PollControllers();

    private void PollControllers()
    {
        var enabledMask = ReadRegister32(SerialPoll) & SerialPollEnabledPorts;
        if (enabledMask == 0)
        {
            return;
        }

        for (var i = 0; i < 4; i++)
        {
            var portBit = 0x80u >> i;
            if ((enabledMask & portBit) != 0)
            {
                var report = Controllers[i].GetSiReport();
                var baseOffset = SerialInterface + (uint)(i * 12);
                WriteRegister32(baseOffset, (uint)(report >> 32));
                WriteRegister32(baseOffset + 4, (uint)report);
            }
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
            $"controllers polled; ports enabled=0x{enabledMask:X2}");
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

    /// <summary>
    /// Reports that the graphics processor has finished the drawing list it
    /// was given, which is what <c>GXDrawDone</c> waits for.
    /// </summary>
    /// <remarks>
    /// This is the interrupt Super Mario Sunshine's main thread sleeps on. It
    /// pushes an end-of-list marker through the command stream and calls
    /// <c>OSSleepThread</c>; the pixel engine's finish interrupt is the only
    /// thing that ever wakes it. Without it the game is not stuck and not
    /// crashed — it is waiting, correctly, for hardware that never answers, and
    /// the operating system idles because that thread is the one with work to do.
    /// </remarks>
    public void SignalPixelEngineFinish()
    {
        WriteRegister16(
            PixelEngineInterrupt,
            (ushort)(ReadRegister16(PixelEngineInterrupt) | PixelEngineFinishStatus));
        RefreshPixelEngineInterrupt();
        _trace.WriteEvery(
            GameCubeTraceChannel.Graphics,
            GameCubeTraceLevel.Debug,
            "pe/finish",
            120,
            "drawing list finished");
    }

    /// <summary>
    /// Reports that the graphics processor has passed a token a game planted
    /// in the command stream, and records which one.
    /// </summary>
    public void SignalPixelEngineToken(ushort token, bool raisesInterrupt)
    {
        WriteRegister16(PixelEngineToken, token);
        if (!raisesInterrupt)
        {
            return;
        }

        WriteRegister16(
            PixelEngineInterrupt,
            (ushort)(ReadRegister16(PixelEngineInterrupt) | PixelEngineTokenStatus));
        RefreshPixelEngineInterrupt();
        _trace.WriteEvery(
            GameCubeTraceChannel.Graphics,
            GameCubeTraceLevel.Debug,
            "pe/token",
            120,
            $"token 0x{token:X4} passed");
    }

    /// <summary>
    /// Asserts or drops the pixel engine's two interrupts, each a status bit
    /// with its own enable.
    /// </summary>
    private void RefreshPixelEngineInterrupt()
    {
        var status = ReadRegister16(PixelEngineInterrupt);
        var cause = ReadRegister32(InterruptCause);

        cause = (status & PixelEngineTokenStatus) != 0 && (status & PixelEngineTokenEnable) != 0
            ? cause | PixelEngineTokenCause
            : cause & ~PixelEngineTokenCause;

        cause = (status & PixelEngineFinishStatus) != 0 && (status & PixelEngineFinishEnable) != 0
            ? cause | PixelEngineFinishCause
            : cause & ~PixelEngineFinishCause;

        WriteRegister32(InterruptCause, cause);
    }

    /// <summary>
    /// Advances the audio sample counter and raises the audio interrupt when it
    /// reaches the count software asked to be told about.
    /// </summary>
    /// <remarks>
    /// This counter is the clock the whole audio system is built on. It counts
    /// stereo samples actually output, so it moves whether or not anything is
    /// listening, and a game reads it to know where playback has reached. Left
    /// at zero it says the machine has produced no sound since it was switched
    /// on, which is a claim a game will wait on indefinitely rather than
    /// disbelieve — thirty-nine million reads of it in one run, and nothing
    /// else happening at all.
    /// </remarks>
    private void AdvanceAudio(long coreCycles)
    {
        var control = ReadRegister32(AudioControl);

        // Free-running, and deliberately not gated on the playing bit. That bit
        // decides whether samples reach the speakers; the counter is driven by
        // the audio oscillator and keeps time regardless. Gating it produced a
        // game reading the same value twenty-eight million times — it uses this
        // as a clock, and a stopped clock is not a quiet one, it is a broken one.
        var period = (control & AudioRate48kHz) != 0
            ? CoreCyclesPerSample48kHz
            : CoreCyclesPerSample32kHz;

        _audioCycles += coreCycles;
        if (_audioCycles < period)
        {
            return;
        }

        var samples = (uint)(_audioCycles / period);
        _audioCycles -= (long)samples * period;

        var before = ReadRegister32(AudioSampleCounter);
        var after = before + samples;
        WriteRegister32(AudioSampleCounter, after);

        Span<short> pcmBuffer = stackalloc short[32];
        AudioSynthesizer.Synthesize(_memory, pcmBuffer);
        AudioOutput.WriteSamples(pcmBuffer);

        _trace.WriteEvery(
            GameCubeTraceChannel.Audio,
            GameCubeTraceLevel.Information,
            "ai/sample-counter",
            2000,
            $"sample counter {before} -> {after} (control 0x{control:X8}, " +
            $"{period} cycles per sample)");

        // The interrupt is raised when the counter reaches the requested value.
        // Compared as a range rather than for equality: many samples can pass
        // between two looks at the clock, and an equality test would step over
        // the one that mattered and never fire again.
        var wanted = ReadRegister32(AudioInterruptTiming);
        if (wanted == 0 || (control & AudioInterruptHeld) != 0 || before >= wanted || after < wanted)
        {
            return;
        }

        WriteRegister32(AudioControl, control | AudioInterruptStatus);
        RefreshAudioInterrupt();
    }

    /// <summary>
    /// Asserts or drops the audio interface's interrupt from its status bit and
    /// enable.
    /// </summary>
    private void RefreshAudioInterrupt()
    {
        var control = ReadRegister32(AudioControl);
        if ((control & AudioInterruptStatus) != 0 && (control & AudioInterruptMask) != 0)
        {
            RaiseInterrupt(AudioInterruptCause);
            return;
        }

        WriteRegister32(InterruptCause, ReadRegister32(InterruptCause) & ~AudioInterruptCause);
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
        _mailToCpuQueue.Clear();
        _lastMailToCpu = 0;
        _mailTakenPending = false;

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

        // Anything a read consumed takes effect now that its value has been
        // taken, not before it.
        CompleteMailTake();

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

        // The audio control register holds its interrupt status alongside the
        // enables, the rate and a counter reset that is a command rather than a
        // stored bit.
        if (size == 4 && offset == AudioControl)
        {
            var kept = ReadRegister32(offset) & AudioInterruptStatus & ~value;
            WriteRegister32(offset, ((value & ~AudioInterruptStatus) | kept) & ~AudioCounterReset);
            if ((value & AudioCounterReset) != 0)
            {
                WriteRegister32(AudioSampleCounter, 0);
                _audioCycles = 0;
            }

            RefreshAudioInterrupt();
            Report(offset, "write", size, value);
            return;
        }

        // The drive's three status bits are acknowledged by writing ones, in the
        // same word as their enables and the break request.
        if (size == 4 && offset == DvdStatus)
        {
            var kept = ReadRegister32(offset) & DvdInterruptStatus & ~value;
            WriteRegister32(offset, (value & ~DvdInterruptStatus) | kept);
            RefreshDvdInterrupt();
            Report(offset, "write", size, value);
            return;
        }

        // The pixel engine's two status bits are acknowledged the same way, and
        // share their word with the two enables.
        if (size == 2 && offset == PixelEngineInterrupt)
        {
            var current = ReadRegister16(PixelEngineInterrupt);
            var kept = (ushort)(current & PixelEngineStatus & ~value);
            WriteRegister16(PixelEngineInterrupt, (ushort)((value & ~PixelEngineStatus) | kept));
            RefreshPixelEngineInterrupt();
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
                WriteRegister32(DspMailToCpuHigh, FrontMailToCpu());
                if (size == 4)
                {
                    TakeMailToCpu();
                }

                break;
            }

            case DspMailToCpuLow:
                WriteRegister16(
                    DspMailToCpuLow,
                    (ushort)FrontMailToCpu());
                TakeMailToCpu();
                break;

            case DspMailToDspHigh:
                // The top bit means "the DSP has not taken this yet", and it is
                // what software polls after sending. Nothing here takes time, so
                // the message is already consumed by the time anyone looks and
                // the bit must read back clear — handing back the value that was
                // written returns the caller's own "unread" flag to it, which is
                // a poll that can never end.
                WriteRegister16(
                    DspMailToDspHigh,
                    (ushort)(_mailToDspHigh & ~MailWaiting));
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
        if (offset == AudioDmaControlLength)
        {
            StartAudioDma(value);
            return;
        }

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
                // A thirty-two bit store covers both halves and is a complete
                // message. Treating it as the high half alone keeps the low
                // sixteen bits of the value, throws the rest away, and never
                // sends anything — so every command written that way vanishes
                // and the sender waits for a reply to a message the machine
                // never received.
                if (size == 4)
                {
                    _mailToDspHigh = (ushort)(value >> 16);
                    HandleMailToDsp(value);
                    return;
                }

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
            // Clearing the initialise bit starts the audio system's own
            // microcode, and it announces itself. This is a separate event from
            // a microcode uploaded through the boot ROM, and both happen: a
            // game brings its audio system up first and loads its mixing code
            // afterwards, so both announcements are real and both are expected.
            //
            // Setting the initialise bit starts the audio system's microcode,
            // and the *hardware* clears the bit again once it is up — software
            // asks and then waits to be told, it does not clear the bit itself.
            // Waiting for software to clear it means waiting for something that
            // never happens, and a game polling for the machine to answer polls
            // forever.
            var initNow = (value & DspInitInProgress) != 0;
            if (initNow && !_dspInitInProgress)
            {
                _dspInitCycles = DspInitDelayCycles;
            }

            if (_dspInitInProgress && !initNow)
            {
                PostMailToCpuPolled(DspInitUCodeReadyMail, "init-audio-system microcode ready");
                _dspInitCycles = 0;
            }

            _dspInitInProgress = initNow;

            // Resetting puts the boot ROM back in charge. Without this the
            // machine stays in whatever microcode was last started, so a game
            // that resets the processor and uploads a second microcode has its
            // parameters answered as though they were commands to the first
            // one — and never boots anything again.
            if ((value & DspReset) != 0)
            {
                _microcodeRunning = false;
                _expectedBootParameter = 0;
                _expectingCommandListAddress = false;
            }

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

        // The data has moved, but the drive has not finished. Reporting
        // completion from inside the store that started the transfer is not
        // merely optimistic, it is impossible: the interrupt arrives before the
        // routine that asked for the read has returned, so it lands in code
        // that has not yet installed the handler meant to receive it. The
        // operating system finds an interrupt nobody owns and halts the
        // machine. A real drive takes milliseconds; the only thing that has to
        // be true here is that it takes longer than a function call.
        _dvdCompletionCycles = DvdCompletionDelayCycles;
    }

    /// <summary>
    /// How long a disc transfer takes to report itself finished.
    /// </summary>
    /// <remarks>
    /// Six hundred microseconds, which is what a read costs on a real drive
    /// before any data moves — the command has to be accepted and the head has
    /// to get there. The drive never answers sooner than three hundred even for
    /// a command that does nothing. These are the figures Dolphin uses, and the
    /// point of matching them is not fidelity for its own sake: an interrupt
    /// that arrives before the routine which requested it has returned lands in
    /// code that has not yet installed the handler for it.
    /// </remarks>
    private const long CoreCyclesPerMicrosecond = 486;
    private const long DvdCompletionDelayCycles = 600 * CoreCyclesPerMicrosecond;

    private long _dvdCompletionCycles;

    /// <summary>
    /// Finishes a disc transfer once its time is up: the transfer registers
    /// report what moved, and the completion interrupt is raised.
    /// </summary>
    private void AdvanceDvd(long coreCycles)
    {
        if (_dvdCompletionCycles <= 0)
        {
            return;
        }

        _dvdCompletionCycles -= coreCycles;
        if (_dvdCompletionCycles > 0)
        {
            return;
        }

        _dvdCompletionCycles = 0;

        // Length counts down as data moves and reads zero when the transfer is
        // finished, and the address advances past what was written. Software
        // checks both to decide whether a read succeeded, so a drive that
        // reports completion while its own registers say nothing moved is read
        // as a failure and retried forever.
        var moved = ReadRegister32(DvdDmaLength);
        WriteRegister32(DvdDmaAddress, ReadRegister32(DvdDmaAddress) + moved);
        WriteRegister32(DvdDmaLength, 0);

        WriteRegister32(DvdControl, ReadRegister32(DvdControl) & ~TransferStart);
        WriteRegister32(DvdStatus, ReadRegister32(DvdStatus) | DvdTransferComplete);
        RefreshDvdInterrupt();
    }

    /// <summary>
    /// Asserts or drops the drive's interrupt from its three status-and-enable
    /// pairs.
    /// </summary>
    /// <remarks>
    /// Reading from a disc is asynchronous: software starts a transfer,
    /// registers a callback and sleeps, and the completion interrupt is what
    /// runs that callback and wakes it. Finishing the transfer without saying
    /// so leaves the callback unrun — the same half of an interrupt that was
    /// missing on ARAM, and with the same symptom of a game that is waiting
    /// rather than broken.
    /// </remarks>
    private void RefreshDvdInterrupt()
    {
        var status = ReadRegister32(DvdStatus);
        var asserted =
            ((status & DvdTransferComplete) != 0 && (status & DvdTransferCompleteMask) != 0) ||
            ((status & DvdDeviceError) != 0 && (status & DvdDeviceErrorMask) != 0) ||
            ((status & DvdBreakComplete) != 0 && (status & DvdBreakCompleteMask) != 0);

        if (asserted)
        {
            RaiseInterrupt(DvdInterruptCause);
            return;
        }

        WriteRegister32(InterruptCause, ReadRegister32(InterruptCause) & ~DvdInterruptCause);
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
        PostMailToCpuPolled(DspRomReadyMail, "boot ROM ready");
    }

    /// <summary>Makes a message available to the CPU.</summary>
    /// <summary>
    /// How long a microcode takes to answer. Dolphin uses this same delay and
    /// says why: replying instantly breaks games.
    /// </summary>
    /// <remarks>
    /// A reply posted from inside the store instruction that sent the request
    /// arrives before the sending code has finished running. The interrupt then
    /// lands in the middle of a routine that has not yet published the state its
    /// own handler is about to read. Real hardware cannot do this, because a
    /// real DSP takes time.
    /// </remarks>
    private const long MicrocodeReplyCycles = 2500;

    private readonly record struct DelayedMail(uint Mail, string Description, long RemainingCycles);
    private readonly Queue<DelayedMail> _pendingMailQueue = [];

    /// <summary>
    /// Delivers messages that were posted with a delay, once their delay is up.
    /// </summary>
    private void AdvanceMail(long coreCycles)
    {
        if (_pendingMailQueue.Count == 0)
        {
            return;
        }

        var front = _pendingMailQueue.Dequeue();
        var remaining = front.RemainingCycles - coreCycles;

        if (remaining > 0)
        {
            // Put updated remaining cycles back at front of queue
            var updated = front with { RemainingCycles = remaining };
            _pendingMailQueue.Enqueue(updated);
            // Re-order queue so front stays at head
            for (var i = 0; i < _pendingMailQueue.Count - 1; i++)
            {
                _pendingMailQueue.Enqueue(_pendingMailQueue.Dequeue());
            }

            return;
        }

        DeliverMailToCpu(front.Mail, front.Description);
    }

    private void PostMailToCpu(uint mail, string description) =>
        DeliverMailToCpu(mail, description);

    /// <summary>
    /// Leaves a message for software that is already reading the mailbox, and
    /// does not interrupt the processor.
    /// </summary>
    /// <remarks>
    /// The boot ROM's greeting, the audio system's greeting and a refusal are
    /// all answers to something software just did and is waiting on, so it is
    /// looking at the box already. Ringing the bell as well raises a device
    /// interrupt during startup — before the operating system has installed a
    /// handler for that device — and it halts the machine rather than ignore
    /// one it cannot explain.
    /// </remarks>
    private void PostMailToCpuPolled(uint mail, string description) =>
        DeliverMailToCpu(mail, description, raisesInterrupt: false);

    /// <summary>
    /// Queues a reply for delivery after the time a microcode would have taken.
    /// </summary>
    private void PostMailToCpuLater(uint mail, string description)
    {
        _pendingMailQueue.Enqueue(new DelayedMail(mail, description, MicrocodeReplyCycles));
    }

    /// <summary>
    /// The message at the front of the queue, with the bit that says one is
    /// waiting; or the last one taken, with that bit gone.
    /// </summary>
    private uint FrontMailToCpu() =>
        _mailToCpuQueue.Count > 0
            ? _mailToCpuQueue.Peek() | 0x8000_0000u
            : _lastMailToCpu & ~0x8000_0000u;

    private void DeliverMailToCpu(uint mail, string description) =>
        DeliverMailToCpu(mail, description, raisesInterrupt: true);

    /// <summary>
    /// Puts a message in the mailbox, optionally interrupting the processor.
    /// </summary>
    /// <remarks>
    /// Whether a message interrupts is a property of the message, not of the
    /// mailbox. The boot ROM's greeting is not announced with one — software
    /// resets the processor and then reads the box, so it is already looking.
    /// Interrupting anyway raises a device interrupt during startup, before the
    /// operating system has a handler for that device, and it stops the machine
    /// rather than ignore it.
    /// </remarks>
    private void DeliverMailToCpu(uint mail, string description, bool raisesInterrupt)
    {
        var wasEmpty = _mailToCpuQueue.Count == 0;
        _mailToCpuQueue.Enqueue(mail);
        _mailInterrupts.Enqueue(raisesInterrupt);

        // Sending mail interrupts the processor, but only when the message has
        // reached the front of the queue. Software does not sit reading the
        // mailbox waiting for a reply — it registers a handler and gets on with
        // something else, and that handler is what runs the callback a game's
        // audio system is actually waiting on. A message queued behind another
        // rings its bell when its turn comes, not before.
        if (wasEmpty && raisesInterrupt)
        {
            RaiseMailInterrupt();
        }

        // Information, not Debug. There are a few dozen of these in a whole
        // session and they are the entire conversation a game's audio system
        // waits on — hiding them below the level a normal run records has meant
        // repeatedly inferring both halves of an exchange that was being
        // written down and thrown away.
        _trace.Write(
            GameCubeTraceChannel.Dsp,
            GameCubeTraceLevel.Information,
            $"DSP -> CPU mail 0x{mail:X8} ({description})");
    }

    /// <summary>
    /// How long the audio system's microcode takes to come up before the
    /// machine clears the initialise bit and announces it.
    /// </summary>
    private const long DspInitDelayCycles = 130 * 12;

    private long _dspInitCycles;

    /// <summary>
    /// Clears the initialise bit once the microcode it asked for is up, and
    /// announces it — both of which the machine does for itself.
    /// </summary>
    private void AdvanceDspInit(long coreCycles)
    {
        if (_dspInitCycles <= 0)
        {
            return;
        }

        _dspInitCycles -= coreCycles;
        if (_dspInitCycles > 0)
        {
            return;
        }

        _dspInitCycles = 0;
        _dspInitInProgress = false;
        WriteRegister16(
            DspControlStatus,
            (ushort)(ReadRegister16(DspControlStatus) & ~DspInitInProgress));
        PostMailToCpuPolled(DspInitUCodeReadyMail, "init-audio-system microcode ready");
    }

    /// <summary>
    /// Starts the audio transfer when software enables it, loading the source
    /// and length it configured.
    /// </summary>
    private void StartAudioDma(uint value)
    {
        if ((value & AudioDmaEnabled) == 0)
        {
            _audioDmaRemaining = 0;
            WriteRegister16(AudioDmaBlocksLeft, 0);
            return;
        }

        _audioDmaSource =
            (((uint)ReadRegister16(AudioDmaStartHigh) & 0x03FF) << 16) |
            (ReadRegister16(AudioDmaStartLow) & 0xFFE0u);
        _audioDmaBlocks = value & 0x7FFF;
        _audioDmaRemaining = _audioDmaBlocks;
        _audioDmaCycles = 0;
        WriteRegister16(AudioDmaBlocksLeft, (ushort)_audioDmaRemaining);

        _trace.WriteEvery(
            GameCubeTraceChannel.Audio,
            GameCubeTraceLevel.Information,
            "dsp/audio-dma",
            240,
            $"audio transfer from 0x{_audioDmaSource:X8}, {_audioDmaBlocks} blocks " +
            $"of {AudioDmaBlockBytes} bytes");
    }

    /// <summary>
    /// Moves the audio transfer along, and interrupts when a buffer is spent.
    /// </summary>
    /// <remarks>
    /// The interrupt comes when the last block goes, not on every block, and
    /// the transfer then reloads itself from the same registers and carries on.
    /// That is what makes it a heartbeat rather than a one-off: a game hands
    /// over one buffer and is told each time another is wanted.
    /// </remarks>
    private void AdvanceAudioDma(long coreCycles)
    {
        if (_audioDmaRemaining == 0)
        {
            return;
        }

        _audioDmaCycles += coreCycles;
        while (_audioDmaCycles >= CoreCyclesPerAudioBlock && _audioDmaRemaining > 0)
        {
            _audioDmaCycles -= CoreCyclesPerAudioBlock;

            var blockOffset = (_audioDmaBlocks - _audioDmaRemaining) * (uint)AudioDmaBlockBytes;
            var address = _audioDmaSource + blockOffset;
            if (GameCubeMemory.TryTranslate(address, out var ramOffset) &&
                ramOffset + AudioDmaBlockBytes <= GameCubeMemory.MainMemorySize)
            {
                Span<short> pcmBuffer = stackalloc short[16];
                for (var i = 0; i < 16; i++)
                {
                    pcmBuffer[i] = BinaryPrimitives.ReadInt16BigEndian(
                        _memory.MainMemory.Slice((int)ramOffset + (i * 2), 2));
                }
                AudioOutput.WriteSamples(pcmBuffer);
            }

            _audioDmaRemaining--;
            WriteRegister16(AudioDmaBlocksLeft, (ushort)_audioDmaRemaining);

            if (_audioDmaRemaining != 0)
            {
                continue;
            }

            // Spent. Reload from what software configured and tell it.
            _audioDmaRemaining = _audioDmaBlocks;
            WriteRegister16(AudioDmaBlocksLeft, (ushort)_audioDmaRemaining);
            WriteRegister16(
                DspControlStatus,
                (ushort)(ReadRegister16(DspControlStatus) | AudioDmaInterrupt));
            RefreshDspInterrupt();
        }
    }

    /// <summary>Rings the bell for whatever is now at the front of the queue.</summary>
    private void RaiseMailInterrupt()
    {
        WriteRegister16(
            DspControlStatus,
            (ushort)(ReadRegister16(DspControlStatus) | DspMailInterrupt));
        RefreshDspInterrupt();
    }

    private void TakeMailToCpu()
    {
        if (_mailToCpuQueue.Count == 0)
        {
            return;
        }

        // The message stays readable; only the flag saying one is waiting goes
        // away, and it goes away for the *next* look rather than this one.
        // Clearing it here would hand the reader a message with the bit that
        // announces it already removed — which is the one bit software tests to
        // decide whether it received anything at all.
        _lastMailToCpu = _mailToCpuQueue.Dequeue();
        if (_mailInterrupts.Count > 0)
        {
            _mailInterrupts.Dequeue();
        }

        _mailTakenPending = true;
    }

    /// <summary>
    /// Drops the mail-waiting flag once the read that consumed it has returned,
    /// and rings again for whatever was queued behind it.
    /// </summary>
    private void CompleteMailTake()
    {
        if (!_mailTakenPending)
        {
            return;
        }

        _mailTakenPending = false;
        WriteRegister32(DspMailToCpuHigh, FrontMailToCpu());
        if (_mailToCpuQueue.Count > 0 && _mailInterrupts.Count > 0 && _mailInterrupts.Peek())
        {
            RaiseMailInterrupt();
        }
    }

    private bool _mailTakenPending;

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
    /// <summary>
    /// The boot ROM's protocol for loading a microcode: a parameter is named by
    /// one message and given by the next, five pairs in all, and the last pair
    /// starts the uploaded code.
    /// </summary>
    private const uint BootParameterPrefix = 0x80F3_0000;
    private const uint BootSourceAddress = 0x80F3_A001;
    private const uint BootCodeLength = 0x80F3_A002;
    private const uint BootDataLength = 0x80F3_B002;
    private const uint BootCodeDestination = 0x80F3_C002;
    private const uint BootStartAddress = 0x80F3_D001;

    /// <summary>
    /// What a running microcode says when it comes up. Every message from a
    /// microcode carries 0xDCD1 in its top half; this is the first of them.
    /// </summary>
    private const uint MicrocodeInitialised = 0xDCD1_0000;

    /// <summary>
    /// A command list is announced with this in its top half, its size in the
    /// bottom, and its address in the message after.
    /// </summary>
    private const uint CommandListPrefix = 0xBABE_0000;
    private const uint MailPrefixMask = 0xFFFF_0000;

    /// <summary>
    /// Task control from the CPU. The two directions are deliberately mirrored:
    /// the processor sends 0xCDD1 and the microcode answers 0xDCD1.
    /// </summary>
    private const uint TaskControlPrefix = 0xCDD1_0000;

    private const uint MicrocodeResumed = 0xDCD1_0001;
    private const uint MicrocodeYielded = 0xDCD1_0002;

    /// <summary>The parameter the next message will supply, or zero.</summary>
    private uint _expectedBootParameter;

    /// <summary>Whether an uploaded microcode has been started.</summary>
    private bool _microcodeRunning;

    /// <summary>Whether the next message carries a command list's address.</summary>
    private bool _expectingCommandListAddress;

    private uint _microcodeSource;
    private uint _microcodeLength;
    private uint _microcodeDestination;
    private uint _microcodeStart;

    /// <summary>
    /// Answers the CPU as the DSP's boot ROM does while a microcode is being
    /// uploaded, and announces the microcode once it has been started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is high-level emulation: no DSP instruction is executed, and the
    /// microcode's own work is not performed. What is emulated is the
    /// conversation, because the conversation is what a game waits on. It
    /// uploads its audio microcode into memory, tells the boot ROM where to
    /// find it and where to put it, and then blocks until the code it uploaded
    /// reports that it is running.
    /// </para>
    /// <para>
    /// Messages arrive in pairs — one naming a parameter, the next carrying its
    /// value — and anything that does not begin with the expected prefix is
    /// rejected the way the ROM rejects it, by echoing the offending half back
    /// under 0xFEEE. Getting that wrong matters: software checks the refusal.
    /// </para>
    /// </remarks>
    private void HandleMailToDsp(uint mail)
    {
        // Once a microcode is running the boot ROM is gone, and with it the
        // rule that anything unrecognised gets refused. Staying in ROM mode
        // means answering every command a game sends its own audio code with a
        // rejection, which is worse than not answering at all.
        if (_microcodeRunning)
        {
            HandleMicrocodeMail(mail);
            return;
        }

        if (_expectedBootParameter == 0)
        {
            if ((mail & 0xFFFF_0000) != BootParameterPrefix)
            {
                PostMailToCpuPolled(0xFEEE_0000 | (mail & 0xFFFF), $"refusing mail 0x{mail:X8}");
                return;
            }

            _expectedBootParameter = mail;
            return;
        }

        var parameter = _expectedBootParameter;
        _expectedBootParameter = 0;

        switch (parameter)
        {
            case BootSourceAddress:
                _microcodeSource = mail;
                return;

            case BootCodeLength:
                _microcodeLength = mail & 0xFFFF;
                return;

            case BootDataLength:
                return;

            case BootCodeDestination:
                _microcodeDestination = mail & 0xFFFF;
                return;

            case BootStartAddress:
                _microcodeStart = mail & 0xFFFF;
                StartMicrocode();
                return;

            default:
                _trace.WriteOnce(
                    GameCubeTraceChannel.Dsp,
                    GameCubeTraceLevel.Warning,
                    MailKey(parameter),
                    $"unknown boot parameter 0x{parameter:X8}; its value 0x{mail:X8} is ignored");
                return;
        }
    }

    /// <summary>
    /// Identifies an uploaded microcode by hashing it, the way every emulator
    /// that has ever run one does.
    /// </summary>
    /// <remarks>
    /// Exclusive-or each byte into a running value and rotate that value left
    /// by three. It is not a good hash and was never meant to be; it is simply
    /// the one everybody agreed on, so the published values for each known
    /// microcode are values this produces.
    /// </remarks>
    private static uint HashMicrocode(ReadOnlySpan<byte> code)
    {
        var hash = 0u;
        foreach (var value in code)
        {
            hash ^= value;
            hash = (hash << 3) | (hash >> 29);
        }

        return hash;
    }

    /// <summary>The published hashes of the microcodes worth telling apart.</summary>
    private static string DescribeMicrocode(uint hash) => hash switch
    {
        0x65D6_CC6F => "memory card",
        0xDD7E_72D5 => "game boy advance",
        0x3AD3_B7AC or 0x4E8A_8B21 or 0x07F8_8145 or 0xE213_6399 or 0x3389_A79E => "AX audio",
        0x8684_0740 or 0x6CA3_3A6D => "Zelda audio",
        _ => "unrecognised"
    };

    /// <summary>
    /// Reports that the uploaded microcode is now running.
    /// </summary>
    private void StartMicrocode()
    {
        // Identify what was uploaded before announcing anything, because the
        // announcement depends on it. Which microcode is running has been
        // guessed at three times and changed three times; a hash of the bytes
        // that actually arrived settles it.
        var hash = 0u;
        var source = _microcodeSource & 0x03FF_FFFF;
        if (_microcodeLength > 0 && source + _microcodeLength <= (uint)_memory.MainMemory.Length)
        {
            hash = HashMicrocode(_memory.MainMemory.Slice((int)source, (int)_microcodeLength));
        }

        _trace.Write(
            GameCubeTraceChannel.Dsp,
            GameCubeTraceLevel.Information,
            $"microcode uploaded from 0x{_microcodeSource:X8}, {_microcodeLength} bytes, " +
            $"to instruction memory 0x{_microcodeDestination:X4}, starting at 0x{_microcodeStart:X4}; " +
            $"hash 0x{hash:X8} ({DescribeMicrocode(hash)})");

        _microcodeRunning = true;
        _expectingCommandListAddress = false;

        // What a microcode says on starting depends on which one it is. The
        // mixing microcodes announce themselves with the task family's opening
        // word; the audio system's own microcode has a word of its own. The
        // hash is what tells them apart, and this game's does not match any
        // published one — so the choice rests on what the game does next, which
        // is to wait to be told its audio system is up and to send nothing at
        // all until it hears that. A microcode that only ever replies cannot
        // satisfy a caller that never asks.
        // Anything not recognised as one of the special microcodes is treated
        // as a mixing microcode, because that is what a game uploads through
        // the boot ROM. The audio system's own announcement belongs to the
        // initialise bit and is sent there; repeating it here says the same
        // thing twice and the operating system's task manager halts.
        var announcement = DescribeMicrocode(hash) is "memory card" or "game boy advance"
            ? DspInitUCodeReadyMail
            : MicrocodeInitialised;

        PostMailToCpuLater(announcement, $"microcode running (0x{hash:X8})");
    }

    /// <summary>
    /// Answers the CPU on behalf of a running microcode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A game drives its audio microcode by handing it a list of commands: one
    /// message announces the list and its size, the next gives its address, and
    /// the microcode reports when it has worked through it. Nothing here reads
    /// that list — no samples are mixed and no sound is produced — but the
    /// exchange is completed, because a game does not merely send the list, it
    /// waits to be told the list is done before preparing the next one.
    /// </para>
    /// <para>
    /// Sound will need the commands to actually be carried out. Progress does
    /// not, and the two are worth separating: a machine that answers honestly
    /// and produces silence is a different thing from one that never answers.
    /// </para>
    /// </remarks>
    private void HandleMicrocodeMail(uint mail)
    {
        // Every message, not just the unrecognised ones. A conversation that
        // stalls is diagnosed by reading both halves of it, and until now only
        // the replies were on the record.
        _trace.WriteEvery(
            GameCubeTraceChannel.Dsp,
            GameCubeTraceLevel.Information,
            "dsp/cpu-mail",
            64,
            $"CPU -> microcode 0x{mail:X8}");

        if (_expectingCommandListAddress)
        {
            _expectingCommandListAddress = false;
            _trace.WriteEvery(
                GameCubeTraceChannel.Dsp,
                GameCubeTraceLevel.Debug,
                "dsp/command-list",
                240,
                $"command list at 0x{mail:X8} accepted and reported finished");
            PostMailToCpuLater(MicrocodeYielded, "command list finished");
            return;
        }

        if ((mail & MailPrefixMask) == CommandListPrefix)
        {
            _expectingCommandListAddress = true;
            return;
        }

        if ((mail & MailPrefixMask) == TaskControlPrefix)
        {
            switch (mail & 0xFFFF)
            {
                case 0x0001:
                    // Load another microcode. A running microcode can be told
                    // to hand over, and what follows is the boot ROM's own
                    // parameter sequence again — so the machine has to go back
                    // to listening for it, or the parameters are answered as
                    // though they were commands and nothing ever loads.
                    _microcodeRunning = false;
                    _expectedBootParameter = 0;
                    _expectingCommandListAddress = false;
                    return;

                case 0x0002:
                    // Give up and go back to the boot ROM, which announces
                    // itself again on arrival.
                    _microcodeRunning = false;
                    _expectedBootParameter = 0;
                    _expectingCommandListAddress = false;
                    PostMailToCpuLater(DspRomReadyMail, "returned to the boot ROM");
                    return;

                case 0x0003:
                    // Carry on with the next command list, without an
                    // acknowledgement.
                    return;

                default:
                    PostMailToCpuLater(MicrocodeResumed, $"task control 0x{mail:X8} acknowledged");
                    return;
            }
        }

        _trace.WriteOnce(
            GameCubeTraceChannel.Dsp,
            GameCubeTraceLevel.Warning,
            MailKey(mail),
            $"microcode mail 0x{mail:X8} is not understood and went unanswered");
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
        InterruptCause or InterruptMask or VerticalPosition or VerticalPosition2 => true,
        VideoInterface or TopFieldBase or TopFieldBase + 2 or BottomFieldBase or BottomFieldBase + 2 => true,
        >= DisplayInterrupt0 and < DisplayInterrupt0 + (DisplayInterruptCount * 4) => true,
        SerialCommunicationStatus or SerialStatus => true,

        // The FIFO's bounds and pointers, and the write-gather pipe they are
        // fed through. These drive the command processor rather than merely
        // reading back, so calling them unimplemented would be the wrong way
        // round — but they stay counted, because "modelled" has been wrong
        // before and a count is the only thing that tells the two apart.
        >= FifoBaseLow and <= FifoReadPointerHigh => true,
        >= WriteGatherPipe => true,

        // Everything below drives real behaviour now. Leaving them on the
        // unimplemented list is not harmless: that list is what the next piece
        // of work gets chosen from, and a dozen entries describing things that
        // already work is what a genuine gap hides behind.
        AudioControl or AudioSampleCounter or AudioInterruptTiming => true,
        AudioInterface + 0x04 => true,
        >= DvdInterface and < SerialInterface => true,
        >= ExternalInterface and < AudioInterface => true,
        PixelEngineInterrupt or PixelEngineToken => true,
        SerialPoll => true,

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
