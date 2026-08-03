using System.Diagnostics;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// PixelCube: PixelDeck's GameCube core.
/// </summary>
/// <remarks>
/// <para>
/// This is a scaffold, and says so rather than pretending otherwise. What
/// exists is everything up to the first instruction: the disc container, the
/// header, the file table, the boot executable, the memory map, and the trace
/// log that watches all of it. What does not exist is a Gekko interpreter,
/// which is why there is no <c>RunFrame</c> here — an emulator that returns
/// blank frames and calls it running is indistinguishable from a broken one,
/// and would cost more to debug later than it saves now.
/// </para>
/// <para>
/// <see cref="Boot"/> therefore does exactly what the apploader would do and
/// then stops: it places the DOL's sections in main memory, clears the BSS,
/// and reports the address a CPU would start from. When the interpreter
/// arrives, it starts at <see cref="EntryPoint"/> against a memory image that
/// has already been checked against a real disc.
/// </para>
/// </remarks>
public sealed class GameCubeMachine : IDisposable
{
    private readonly bool _ownsTrace;
    private bool _disposed;

    private GameCubeMachine(GameCubeDisc disc, GameCubeTraceLog trace, bool ownsTrace)
    {
        Disc = disc;
        Trace = trace;
        _ownsTrace = ownsTrace;
        Memory = new GameCubeMemory(trace);
        Memory.Hardware.Disc = disc;
        Cpu = new GekkoCpu(Memory, trace);
    }

    public GameCubeDisc Disc { get; }

    public GameCubeMemory Memory { get; }

    public GekkoCpu Cpu { get; }

    /// <summary>
    /// The log every part of this core reports through. Exposed so the
    /// dashboard can attach its own sink and change the level while a session
    /// is running.
    /// </summary>
    public GameCubeTraceLog Trace { get; }

    /// <summary>The boot executable, once <see cref="Boot"/> has read it.</summary>
    public GameCubeExecutable? BootExecutable { get; private set; }

    /// <summary>Where a Gekko interpreter would begin. Zero until booted.</summary>
    public uint EntryPoint => BootExecutable?.EntryPoint ?? 0;

    public bool IsBooted => BootExecutable is not null;

    /// <summary>
    /// Whether this core can run a game to completion. Still false: there is
    /// an interpreter now, but no floating point, no graphics, and no
    /// hardware behind the register block.
    /// </summary>
    public static bool HasExecutionCore => false;

    public int Width => 640;

    public int Height => 480;

    public double FramesPerSecond => Disc.Header.FramesPerSecond;

    /// <summary>
    /// Opens a disc and prepares a machine for it. The trace log is
    /// configured from the environment unless one is supplied, so a session
    /// can be traced in detail without a rebuild:
    /// <c>PIXELCUBE_TRACE=debug:disc,executable</c>.
    /// </summary>
    public static GameCubeMachine Load(string path, GameCubeTraceLog? trace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var ownsTrace = trace is null;
        var log = trace ?? GameCubeTraceLog.CreateFromEnvironment();
        try
        {
            log.Write(
                GameCubeTraceChannel.Boot,
                GameCubeTraceLevel.Information,
                $"PixelCube {typeof(GameCubeMachine).Assembly.GetName().Version} starting; " +
                $"trace level={log.Level} channels={log.Channels}");

            var disc = GameCubeDisc.Open(path, log);
            var machine = new GameCubeMachine(disc, log, ownsTrace);
            machine.ApplyWatchFromEnvironment();
            return machine;
        }
        catch (Exception exception)
        {
            log.Write(
                GameCubeTraceChannel.Boot,
                GameCubeTraceLevel.Error,
                $"disc could not be opened: {exception.Message}");
            log.Flush();
            if (ownsTrace)
            {
                log.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Performs the part of startup PixelCube can already do: reads the boot
    /// executable, copies its sections into main memory, and clears the BSS.
    /// Safe to call more than once; a second call reloads from the disc.
    /// </summary>
    public GameCubeExecutable Boot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var start = Stopwatch.GetTimestamp();
        var executable = Disc.ReadBootExecutable();

        // BSS is cleared first, then the sections are placed over it. A DOL's
        // declared BSS range routinely overlaps sections the linker put there
        // deliberately — Super Mario Sunshine's data6 sits squarely inside it
        // — so clearing afterwards erases loaded data and leaves the game
        // running with holes in itself.
        if (executable.BssSize > 0)
        {
            Memory.Clear(executable.BssAddress, executable.BssSize);
        }

        foreach (var section in executable.Sections)
        {
            Memory.Write(section.LoadAddress, section.Data.Span);
        }

        BootExecutable = executable;

        // Installed after the sections rather than before: a DOL section may
        // legitimately cover the low-memory area, and the globals a real boot
        // leaves behind have to be the ones that survive.
        GameCubeBootState.Install(Memory, Cpu, Disc, executable, Trace);
        Cpu.Pc = executable.EntryPoint;

        Trace.Write(
            GameCubeTraceChannel.Boot,
            GameCubeTraceLevel.Information,
            $"boot image placed in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1} ms: " +
            $"{executable.Sections.Count} sections, {executable.TotalSectionBytes:N0} bytes, " +
            $"entry=0x{executable.EntryPoint:X8}");
        Trace.Flush();

        return executable;
    }

    /// <summary>
    /// Runs the interpreter for up to <paramref name="maximumInstructions"/>
    /// instructions, booting first if it has not booted yet, and reports what
    /// stopped it.
    /// </summary>
    public GekkoRunResult Run(long maximumInstructions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsBooted)
        {
            Boot();
        }

        var result = Cpu.Run(maximumInstructions);
        if (result.Outcome != GekkoOutcome.Completed)
        {
            Trace.Write(
                GameCubeTraceChannel.Cpu,
                GameCubeTraceLevel.Warning,
                $"execution stopped after {Cpu.InstructionsExecuted:N0} instructions: " +
                $"{result.Outcome} at 0x{result.Pc:X8} " +
                $"({GekkoDisassembler.Describe(result.Instruction, result.Pc)})");

            // What the game made of the arena, rather than what it was handed.
            // OSInit owns these two words, so their value at a fault says
            // whether the game got as far as claiming its own memory.
            Trace.Write(
                GameCubeTraceChannel.Cpu,
                GameCubeTraceLevel.Warning,
                $"  os globals: arenaLo=0x{Memory.ReadUInt32(0x8000_0030):X8} " +
                $"arenaHi=0x{Memory.ReadUInt32(0x8000_0034):X8} " +
                $"currentThread=0x{Memory.ReadUInt32(0x8000_00E4):X8}");
            TraceDisassemblyAround(result.Pc);
        }

        return result;
    }

    /// <summary>Names the environment variable that arms a memory watchpoint.</summary>
    public const string WatchVariable = "PIXELCUBE_WATCH";

    /// <summary>
    /// Arms a watchpoint from <c>PIXELCUBE_WATCH</c>, given as a hexadecimal
    /// address with an optional length: <c>8040E800</c> or <c>8040E800:4</c>.
    /// </summary>
    /// <remarks>
    /// Every write to the address is then reported along with the instruction
    /// that made it. This exists for one question, which keeps coming up and
    /// has no other answer: a game is spinning on a global waiting for a
    /// handler to change it, and the only thing worth knowing is whether
    /// anything writes it at all, and if so from where.
    /// </remarks>
    private void ApplyWatchFromEnvironment()
    {
        var specification = Environment.GetEnvironmentVariable(WatchVariable);
        if (string.IsNullOrWhiteSpace(specification))
        {
            // Said out loud, because silence here is ambiguous in the worst
            // way: a watch that was never asked for and a watch that was asked
            // for and not read produce exactly the same empty log, and the
            // second one wastes a run.
            Trace.Write(
                GameCubeTraceChannel.Boot,
                GameCubeTraceLevel.Information,
                $"no memory watch set ({WatchVariable} is empty)");
            return;
        }

        var parts = specification.Split(':', 2);
        if (!uint.TryParse(
                parts[0].Trim().TrimStart('0', 'x', 'X'),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var address))
        {
            Trace.Write(
                GameCubeTraceChannel.Boot,
                GameCubeTraceLevel.Warning,
                $"{WatchVariable}=\"{specification}\" is not a hexadecimal address; no watch was set");
            return;
        }

        var length = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var parsed) ? parsed : 4;
        Memory.WatchAddress = address;
        Memory.WatchLength = length;
        Trace.Write(
            GameCubeTraceChannel.Boot,
            GameCubeTraceLevel.Information,
            $"watching {length} bytes at 0x{address:X8}; every write will be reported with its instruction");
    }

    /// <summary>
    /// Finds the address a stalled loop keeps reading and watches it, so every
    /// later write to it is reported with the instruction that made it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A spin is nearly always a load, a compare and a conditional branch
    /// waiting for something else to change memory. The address is not in the
    /// instruction — it is a register plus an offset — so it cannot be known
    /// before the machine is actually sitting on the loop, which is exactly
    /// when this runs.
    /// </para>
    /// <para>
    /// Self-arming because the alternative was asking for an address that has
    /// to be worked out from a register dump first, set by hand, and carried
    /// into the next run. Anything already watched deliberately wins.
    /// </para>
    /// </remarks>
    public bool TryWatchStalledLoad(uint pc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Memory.WatchLength > 0)
        {
            return false;
        }

        // The loop is a handful of instructions, so the load is within a couple
        // either side of wherever the sample happened to land.
        for (var delta = -8; delta <= 8; delta += 4)
        {
            var at = (uint)((int)pc + delta);
            if (!Memory.TryReadInstruction(at, out var instruction))
            {
                continue;
            }

            // The D-form loads: lwz, lbz, lhz, lha.
            var primary = instruction >> 26;
            if (primary is not (32 or 34 or 40 or 42))
            {
                continue;
            }

            var register = (int)((instruction >> 16) & 0x1F);
            var address = (register == 0 ? 0u : Cpu.Gpr[register]) +
                (uint)(short)(instruction & 0xFFFF);

            Memory.WatchAddress = address;
            Memory.WatchLength = 4;
            Trace.Write(
                GameCubeTraceChannel.Memory,
                GameCubeTraceLevel.Warning,
                $"  watching 0x{address:X8}, which the loop at 0x{at:X8} keeps reading " +
                $"({GekkoDisassembler.Describe(instruction, at)}); " +
                "every write to it will be reported from here on");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Dumps a run of memory as words, for reading a structure by eye.
    /// </summary>
    public void TraceMemory(uint address, int bytes, string label)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Trace.Write(GameCubeTraceChannel.Memory, GameCubeTraceLevel.Warning, $"  {label}:");
        for (var offset = 0u; offset < bytes; offset += 16)
        {
            var at = address + offset;
            if (!GameCubeMemory.TryTranslate(at, out _))
            {
                return;
            }

            Trace.Write(
                GameCubeTraceChannel.Memory,
                GameCubeTraceLevel.Warning,
                $"    {at:X8}  {Memory.ReadUInt32(at):X8} {Memory.ReadUInt32(at + 4):X8} " +
                $"{Memory.ReadUInt32(at + 8):X8} {Memory.ReadUInt32(at + 12):X8}");
        }
    }

    /// <summary>
    /// Dumps the operating system's own bookkeeping: the low-memory thread
    /// globals, and the run queue the scheduler chooses from.
    /// </summary>
    /// <remarks>
    /// Every device interrupt has now been accounted for and the machine still
    /// idles, which means the question is no longer "what is not being
    /// delivered" but "what is every thread waiting for". That is answered by
    /// the operating system's structures rather than by hardware registers —
    /// whether any threads exist at all, which queue holds them, and whether
    /// the scheduler simply has nothing to choose.
    /// </remarks>
    public void TraceOperatingSystemState(uint runQueue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        TraceMemory(0x8000_00C0, 64, "OS thread globals at 0x800000C0");
        if (runQueue != 0)
        {
            TraceMemory(runQueue, 64, $"run queue at 0x{runQueue:X8} (head/tail per priority)");
        }

        // Every distinct thread or context the globals point at, described. A
        // thread that is waiting records the queue it is waiting on, and that
        // pointer is the name of whatever is supposed to wake it — which is the
        // one thing none of the hardware registers could ever say.
        var seen = new List<uint>();
        for (var global = 0x8000_00D4u; global <= 0x8000_00E4u; global += 4)
        {
            var thread = Memory.ReadUInt32(global);
            if (thread == 0 || seen.Contains(thread) || !GameCubeMemory.TryTranslate(thread, out _))
            {
                continue;
            }

            seen.Add(thread);
            DescribeThread(thread);
        }
    }

    /// <summary>
    /// Reports one thread's scheduling fields, which sit immediately after its
    /// saved context.
    /// </summary>
    private void DescribeThread(uint thread)
    {
        var state = Memory.ReadUInt16(thread + 0x2C8);
        var name = state switch
        {
            1 => "READY",
            2 => "RUNNING",
            4 => "WAITING",
            8 => "MORIBUND",
            _ => "unknown"
        };

        Trace.Write(
            GameCubeTraceChannel.Memory,
            GameCubeTraceLevel.Warning,
            $"  thread 0x{thread:X8}: state={state} ({name}) " +
            $"suspend={(int)Memory.ReadUInt32(thread + 0x2CC)} " +
            $"priority={(int)Memory.ReadUInt32(thread + 0x2D0)} " +
            $"base={(int)Memory.ReadUInt32(thread + 0x2D4)} " +
            $"waitingOnQueue=0x{Memory.ReadUInt32(thread + 0x2DC):X8}");
    }

    /// <summary>
    /// Reports the three things that all have to be true for an interrupt to
    /// reach the CPU, so a run that takes none says which one is missing.
    /// </summary>
    /// <remarks>
    /// A device raises its cause, the processor interface has to have that
    /// device unmasked, and the machine state register has to allow external
    /// interrupts through. Any one of the three being wrong produces the same
    /// symptom — a game waiting forever on a flag a handler would have set —
    /// and none of them is visible from the outside. Guessing which it is has
    /// already cost two rounds of work aimed at the wrong half of the problem.
    /// </remarks>
    public void TraceInterruptState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cause = Memory.Hardware.Read(GameCubeHardware.Base + 0x3000, 4);
        var mask = Memory.Hardware.Read(GameCubeHardware.Base + 0x3004, 4);
        Trace.Write(
            GameCubeTraceChannel.Interrupts,
            GameCubeTraceLevel.Warning,
            $"  interrupts: msr=0x{Cpu.Msr:X8} external={(Cpu.AreInterruptsEnabled ? "on" : "OFF")} " +
            $"pi cause=0x{cause:X8} mask=0x{mask:X8} pending={Memory.Hardware.IsInterruptPending} " +
            $"dec=0x{Cpu.Decrementer:X8} dspCsr=0x{Memory.Hardware.Read(GameCubeHardware.Base + 0x500A, 2):X4}");
    }

    /// <summary>
    /// Disassembles the code either side of an address that stopped a run.
    /// </summary>
    /// <remarks>
    /// The single instruction that failed is rarely the one worth reading. A
    /// branch through an empty register is set up somewhere above it, and the
    /// function it belongs to is what names the fault — so the report has to
    /// carry the code around it, not just the instruction at it. Emitted where
    /// the stop itself is recorded, because the alternative is a second tool
    /// and a second run to see the thing the first run was already sitting on.
    /// </remarks>
    public void TraceDisassemblyAround(uint address, int before = 16, int after = 16)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Aligned and clamped: an address near zero would otherwise wrap.
        var start = address & ~3u;
        start = start < (uint)(before * 4) ? 0 : start - (uint)(before * 4);

        for (var index = 0; index < before + after; index++)
        {
            var at = start + (uint)(index * 4);
            if (!Memory.TryReadInstruction(at, out var instruction))
            {
                continue;
            }

            Trace.Write(
                GameCubeTraceChannel.Cpu,
                GameCubeTraceLevel.Warning,
                $"  {(at == address ? "->" : "  ")} {at:X8}  {instruction:X8}  " +
                $"{GekkoDisassembler.Describe(instruction, at)}");
        }
    }

    /// <summary>
    /// Writes a startup report — disc identity, layout, file count, and boot
    /// image — at <see cref="GameCubeTraceLevel.Information"/>. This is the
    /// block worth pasting into a bug report, and it is why the file system is
    /// read here rather than left until something needs it.
    /// </summary>
    public void TraceStartupReport()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var header = Disc.Header;
        Trace.Write(
            GameCubeTraceChannel.Boot,
            GameCubeTraceLevel.Information,
            $"disc: \"{header.Title}\" {header.GameId} {header.RegionText} " +
            $"container={Disc.ContainerName} size={Disc.Length:N0} bytes " +
            $"video={FramesPerSecond:F2} Hz");

        var fileSystem = Disc.FileSystem;
        Trace.Write(
            GameCubeTraceChannel.Disc,
            GameCubeTraceLevel.Debug,
            $"file system: {fileSystem.Files.Count} files in " +
            $"{fileSystem.Entries.Count - fileSystem.Files.Count} directories");

        if (!IsBooted)
        {
            Boot();
        }

        Trace.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Trace.WriteCounterSummary();
        Trace.Flush();
        Disc.Dispose();
        if (_ownsTrace)
        {
            Trace.Dispose();
        }
    }
}
