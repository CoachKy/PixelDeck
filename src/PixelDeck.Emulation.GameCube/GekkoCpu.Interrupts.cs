namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Exception delivery and the decrementer.
/// </summary>
/// <remarks>
/// <para>
/// Everything the GameCube's operating system does with time or with waiting
/// runs through here. Threads sleep until something wakes them, alarms fire off
/// the decrementer, and a game's frame loop blocks until the video interface
/// says a field has finished. Without any of it, a scheduler enqueues a thread
/// and then has nothing that could ever dequeue it — which is precisely what a
/// trace of fifty-seven million writes into one queue structure looks like.
/// </para>
/// <para>
/// The two asynchronous exceptions are delivered between instructions, never
/// in the middle of one, and only while the machine state register says
/// interrupts are enabled. An exception saves the address it interrupted and
/// the state it interrupted it in, and <c>rfi</c> puts both back.
/// </para>
/// </remarks>
public sealed partial class GekkoCpu
{
    /// <summary>External interrupts enabled, in the machine state register.</summary>
    public const uint MsrExternalInterruptEnable = 0x0000_8000;

    /// <summary>Where an external interrupt transfers control.</summary>
    private const uint ExternalInterruptVector = 0x8000_0500;

    /// <summary>Where a decrementer exception transfers control.</summary>
    private const uint DecrementerVector = 0x8000_0900;

    private const int SprDecrementer = 22;

    /// <summary>
    /// How many instructions pass per tick of the time base and decrementer.
    /// </summary>
    /// <remarks>
    /// Both count at a quarter of the 162 MHz bus, while the core runs at
    /// 486 MHz — twelve core cycles per tick. PixelCube retires roughly one
    /// instruction per cycle, so instructions stand in for cycles. Ticking
    /// once per instruction instead would make every timer in the operating
    /// system fire twelve times too early, which is the kind of wrong that
    /// looks like a game being subtly unstable rather than like a bug.
    /// </remarks>
    private const int InstructionsPerTimeBaseTick = 12;

    /// <summary>
    /// How often the rest of the machine is told how much time has passed.
    /// Far shorter than a scanline, so video timing is not visibly quantised,
    /// and long enough that the call is not part of the instruction cost.
    /// </summary>
    private const int InstructionsPerHardwareUpdate = 128;

    private int _instructionsUntilTick = InstructionsPerTimeBaseTick;
    private int _instructionsSinceHardwareUpdate;
    private bool _decrementerPending;

    /// <summary>The decrementer, which counts down and then interrupts.</summary>
    public uint Decrementer
    {
        get => _spr[SprDecrementer];
        set => _spr[SprDecrementer] = value;
    }

    /// <summary>Whether the machine state register allows interrupts through.</summary>
    public bool AreInterruptsEnabled => (Msr & MsrExternalInterruptEnable) != 0;

    /// <summary>
    /// Delivers a pending asynchronous exception, if one is due. Returns true
    /// when control was transferred, in which case no instruction runs this
    /// step.
    /// </summary>
    private bool TryDeliverInterrupt()
    {
        if (!AreInterruptsEnabled)
        {
            return false;
        }

        // External interrupts outrank the decrementer.
        if (_memory.Hardware.IsInterruptPending)
        {
            DeliverException(ExternalInterruptVector, "external");
            return true;
        }

        if (_decrementerPending)
        {
            _decrementerPending = false;
            DeliverException(DecrementerVector, "decrementer");
            return true;
        }

        return false;
    }

    private void DeliverException(uint vector, string description)
    {
        _spr[SprSrr0] = Pc;
        _spr[SprSrr1] = Msr;

        // The handler runs with interrupts off and in supervisor state. Only
        // the bits an exception is defined to clear are cleared; address
        // translation is left alone, because PixelCube's flat mapping means
        // turning it off here would change nothing except the addresses in the
        // trace.
        Msr &= ~(MsrExternalInterruptEnable | MsrProblemState | MsrFloatingPointAvailable |
                 MsrRecoverableInterrupt);
        Pc = vector;

        _trace.Write(
            GameCubeTraceChannel.Interrupts,
            GameCubeTraceLevel.Debug,
            $"{description} exception: return=0x{_spr[SprSrr0]:X8} saved msr=0x{_spr[SprSrr1]:X8}");
    }

    /// <summary>
    /// Advances the time base and the decrementer, and lets the rest of the
    /// machine catch up. Called once per instruction, so it stays cheap until
    /// there is something to do.
    /// </summary>
    private void AdvanceTime()
    {
        if (--_instructionsUntilTick <= 0)
        {
            _instructionsUntilTick = InstructionsPerTimeBaseTick;
            TimeBase++;

            // The decrementer interrupts when it goes negative, which is the
            // moment its top bit turns on rather than when it reaches zero.
            var previous = _spr[SprDecrementer];
            var next = previous - 1;
            _spr[SprDecrementer] = next;
            if ((previous & 0x8000_0000) == 0 && (next & 0x8000_0000) != 0)
            {
                _decrementerPending = true;
            }
        }

        if (++_instructionsSinceHardwareUpdate >= InstructionsPerHardwareUpdate)
        {
            _memory.Hardware.Advance(_instructionsSinceHardwareUpdate);
            _instructionsSinceHardwareUpdate = 0;
        }
    }

    /// <summary>Supervisor state: cleared when an exception is taken.</summary>
    private const uint MsrProblemState = 0x0000_4000;

    private const uint MsrRecoverableInterrupt = 0x0000_0002;
}
