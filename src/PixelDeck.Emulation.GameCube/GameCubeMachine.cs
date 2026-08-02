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
            return new GameCubeMachine(disc, log, ownsTrace);
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
        }

        return result;
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
