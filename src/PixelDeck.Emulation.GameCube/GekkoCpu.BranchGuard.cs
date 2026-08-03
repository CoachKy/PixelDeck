namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Catches control transfers to addresses no code can live at, and remembers
/// how execution got there.
/// </summary>
/// <remarks>
/// <para>
/// A branch through a register holding zero is the single most common way an
/// emulated boot fails, and it is also the least informative. Address zero is
/// mapped and full of bytes — the IPL leaves the disc header there — so the
/// fetch succeeds, the disc identifier decodes as instructions, and the run
/// dies several instructions later on whichever byte pattern happens not to be
/// a valid opcode. The report then names an address the game never intended to
/// reach and an instruction it never wrote.
/// </para>
/// <para>
/// Stopping at the branch instead names the function that took it. The ring of
/// recent transfers is what turns that into a call path: the last few branches
/// before a wild one are the caller, its caller, and the code that loaded the
/// bad pointer.
/// </para>
/// </remarks>
public sealed partial class GekkoCpu
{
    /// <summary>
    /// How many control transfers are kept for the report. Deep enough to
    /// cross a couple of stack frames, small enough that recording one is
    /// three array stores on the interpreter's hot path.
    /// </summary>
    private const int RecentTransferCount = 32;

    /// <summary>
    /// Below this physical offset are the OS low-memory globals, the disc
    /// header copy and the exception vector table's first entry — data, never a
    /// branch target.
    /// </summary>
    private const int LowestCodeOffset = 0x100;

    /// <summary>
    /// How many calls are kept. Separate from the transfer ring because a
    /// ring of every branch is emptied by the first loop that runs: a fill
    /// routine spinning twenty-eight times pushes out the call that entered it,
    /// which is the one entry that identifies the fault. Calls are rare enough
    /// that sixteen of them reach back through most of a boot.
    /// </summary>
    private const int RecentCallCount = 16;

    private readonly uint[] _transferFrom = new uint[RecentTransferCount];
    private readonly uint[] _transferTo = new uint[RecentTransferCount];
    private readonly uint[] _transferInstruction = new uint[RecentTransferCount];
    private int _transferIndex;
    private int _transfersRecorded;

    private readonly uint[] _callFrom = new uint[RecentCallCount];
    private readonly uint[] _callTo = new uint[RecentCallCount];
    private int _callIndex;
    private int _callsRecorded;

    /// <summary>
    /// Whether a branch to an impossible address stops the run. On by default:
    /// carrying on past one produces a trace of a machine executing its own
    /// disc header.
    /// </summary>
    public bool StopOnWildBranch { get; set; } = true;

    /// <summary>The target of the branch that stopped the last run, if one did.</summary>
    public uint LastBranchTarget { get; private set; }

    private static bool IsPlausibleCodeAddress(uint address) =>
        GameCubeMemory.TryTranslate(address, out var offset) && offset >= LowestCodeOffset;

    private void RecordTransfer(uint from, uint to, uint instruction)
    {
        _transferFrom[_transferIndex] = from;
        _transferTo[_transferIndex] = to;
        _transferInstruction[_transferIndex] = instruction;
        _transferIndex = (_transferIndex + 1) % RecentTransferCount;
        _transfersRecorded++;

        // The link bit is bit zero on every form that can be a call: the
        // relative branch, the conditional branch, and the two that go through
        // a register. Nothing else in primary 19 reaches here with it set.
        var primary = instruction >> 26;
        if ((instruction & 1) == 0 || primary is not (16 or 18 or 19))
        {
            return;
        }

        _callFrom[_callIndex] = from;
        _callTo[_callIndex] = to;
        _callIndex = (_callIndex + 1) % RecentCallCount;
        _callsRecorded++;
    }

    /// <summary>The recent calls, oldest first: the call stack as it was built.</summary>
    public IReadOnlyList<(uint From, uint To)> CaptureRecentCalls()
    {
        var kept = Math.Min(_callsRecorded, RecentCallCount);
        var start = (_callIndex - kept + RecentCallCount) % RecentCallCount;
        var calls = new (uint, uint)[kept];
        for (var i = 0; i < kept; i++)
        {
            calls[i] = (_callFrom[(start + i) % RecentCallCount], _callTo[(start + i) % RecentCallCount]);
        }

        return calls;
    }

    /// <summary>
    /// The recent control transfers, oldest first: where each came from, where
    /// it went, and the instruction that took it.
    /// </summary>
    public IReadOnlyList<(uint From, uint To, uint Instruction)> CaptureRecentTransfers()
    {
        var kept = Math.Min(_transfersRecorded, RecentTransferCount);
        var start = (_transferIndex - kept + RecentTransferCount) % RecentTransferCount;
        var transfers = new (uint, uint, uint)[kept];
        for (var i = 0; i < kept; i++)
        {
            var slot = (start + i) % RecentTransferCount;
            transfers[i] = (_transferFrom[slot], _transferTo[slot], _transferInstruction[slot]);
        }

        return transfers;
    }

    /// <summary>
    /// Reports a branch to an address that cannot hold code, with the register
    /// state that produced it and the path that led there.
    /// </summary>
    private GekkoOutcome ReportWildBranch(uint instruction, uint target)
    {
        LastBranchTarget = target;

        _trace.Write(
            GameCubeTraceChannel.Cpu,
            GameCubeTraceLevel.Error,
            $"wild branch to 0x{target:X8} from 0x{Pc:X8}  " +
            $"{GekkoDisassembler.Describe(instruction, Pc)}");

        TraceContext(GameCubeTraceLevel.Error);
        return GekkoOutcome.WildBranch;
    }

    /// <summary>
    /// Writes the full processor context: every register, the stack around the
    /// stack pointer, the recent calls and the recent branches.
    /// </summary>
    /// <remarks>
    /// Shared by the wild-branch report and the stall report, because the two
    /// need exactly the same thing. A spin is a fault that has not stopped yet:
    /// the loop is waiting on an address held in a register, and without the
    /// registers there is no way to work out which address that is.
    /// </remarks>
    public void TraceContext(GameCubeTraceLevel level)
    {
        _trace.Write(
            GameCubeTraceChannel.Cpu,
            level,
            $"  lr=0x{Lr:X8} ctr=0x{Ctr:X8} msr=0x{Msr:X8} cr=0x{Cr:X8} " +
            $"after {InstructionsExecuted:N0} instructions");

        // Every register, not a chosen few. The argument registers of whatever
        // was running are the difference between "a pointer was wrong" and
        // "this pointer, this length, therefore this overlap" — and which ones
        // matter is not knowable before the fault has happened.
        for (var register = 0; register < GeneralRegisterCount; register += 4)
        {
            _trace.Write(
                GameCubeTraceChannel.Cpu,
                level,
                $"  r{register,-2}=0x{_gpr[register]:X8} r{register + 1,-2}=0x{_gpr[register + 1]:X8} " +
                $"r{register + 2,-2}=0x{_gpr[register + 2]:X8} r{register + 3,-2}=0x{_gpr[register + 3]:X8}");
        }

        TraceStack(level);

        // Calls first: it is the shorter list and the one that names the fault.
        foreach (var (from, to) in CaptureRecentCalls())
        {
            _trace.Write(
                GameCubeTraceChannel.Cpu,
                level,
                $"  called 0x{to:X8} from 0x{from:X8}");
        }

        foreach (var (from, to, taken) in CaptureRecentTransfers())
        {
            _trace.Write(
                GameCubeTraceChannel.Cpu,
                level,
                $"  came from 0x{from:X8} -> 0x{to:X8}  {GekkoDisassembler.Describe(taken, from)}");
        }
    }

    /// <summary>
    /// Dumps the words around the stack pointer, which is where a corrupted
    /// return address is visible as data rather than inferred from a branch.
    /// </summary>
    private void TraceStack(GameCubeTraceLevel level)
    {
        var stack = _gpr[1];
        if (!GameCubeMemory.TryTranslate(stack, out _))
        {
            return;
        }

        var start = stack >= 32 ? stack - 32 : 0;
        for (var offset = 0u; offset < 96; offset += 16)
        {
            var at = start + offset;
            _trace.Write(
                GameCubeTraceChannel.Cpu,
                level,
                $"  stack {at:X8}{(at == stack ? " <-" : "   ")} " +
                $"{_memory.ReadUInt32(at):X8} {_memory.ReadUInt32(at + 4):X8} " +
                $"{_memory.ReadUInt32(at + 8):X8} {_memory.ReadUInt32(at + 12):X8}");
        }
    }
}
