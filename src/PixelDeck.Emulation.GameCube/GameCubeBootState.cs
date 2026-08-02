namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// The state a real boot would have left behind before the game's first
/// instruction runs.
/// </summary>
/// <remarks>
/// <para>
/// PixelCube skips the IPL and the apploader and places the DOL directly. That
/// is the right shortcut — the IPL is copyrighted, nobody has it, and emulating
/// it buys nothing — but it means the console's own setup never happened, and
/// a game's <c>__start</c> reads that setup within its first few hundred
/// instructions.
/// </para>
/// <para>
/// The values that matter most are the OS low-memory globals at
/// 0x8000_0020–0x8000_00FC. <c>__init_hardware</c> and <c>OSInit</c> branch on
/// the memory size and the console type; left at zero they take paths no real
/// console takes, and every trace line after that describes a machine that has
/// never existed. Installing them is small, and it is the difference between a
/// trace that is evidence and one that is noise.
/// </para>
/// </remarks>
public static class GameCubeBootState
{
    /// <summary>Says the system came up through the boot ROM.</summary>
    public const uint BootRomMagic = 0x0D15_EA5E;

    /// <summary>The retail console's 24 MB of main memory.</summary>
    public const uint PhysicalMemorySize = 0x0180_0000;

    /// <summary>Console type: a retail production board.</summary>
    public const uint ConsoleType = 0x0000_0003;

    /// <summary>162 MHz bus.</summary>
    public const uint BusClockSpeed = 0x09A7_EC80;

    /// <summary>486 MHz core.</summary>
    public const uint CoreClockSpeed = 0x1CF7_C580;

    /// <summary>Where the IPL leaves the second half of the disc header.</summary>
    public const uint DiscHeaderInformationAddress = 0x8000_00F4;

    /// <summary>
    /// The MSR a DOL starts under: machine check enabled, both address
    /// translations on, recoverable interrupt. External interrupts stay off
    /// until the game asks for them.
    /// </summary>
    public const uint InitialMsr = 0x0000_2032;

    /// <summary>
    /// Default stack pointer. Every retail <c>__start</c> loads its own from
    /// the linker's <c>_stack_addr</c> within a handful of instructions, so
    /// this only has to be somewhere valid rather than somewhere correct.
    /// </summary>
    public const uint InitialStackPointer = 0x816F_FFF0;

    /// <summary>A branch to <c>rfi</c>: the default exception handler body.</summary>
    private const uint ReturnFromInterrupt = 0x4C00_0064;

    /// <summary>The exception vectors a bare handler is installed at.</summary>
    private static readonly uint[] ExceptionVectors =
    [
        0x8000_0100, // system reset
        0x8000_0200, // machine check
        0x8000_0300, // data storage
        0x8000_0400, // instruction storage
        0x8000_0500, // external interrupt
        0x8000_0600, // alignment
        0x8000_0700, // program
        0x8000_0800, // floating point unavailable
        0x8000_0900, // decrementer
        0x8000_0C00, // system call
        0x8000_0D00, // trace
        0x8000_0F00, // performance monitor
        0x8000_1300, // instruction address breakpoint
        0x8000_1400, // system management
        0x8000_1700  // thermal management
    ];

    /// <summary>
    /// Writes the low-memory globals, the default exception handlers, and the
    /// disc header copy the IPL would have left, then sets the CPU's initial
    /// register state.
    /// </summary>
    public static void Install(
        GameCubeMemory memory,
        GekkoCpu cpu,
        GameCubeDisc disc,
        GameCubeExecutable executable,
        GameCubeTraceLog trace)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(disc);
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(trace);

        // The IPL copies the first 32 bytes of the disc header to address
        // zero, which is where DVDInit and the OS read the game ID from.
        memory.Write(0x8000_0000, disc.Read(0, 0x20));

        memory.WriteUInt32(0x8000_0020, BootRomMagic);
        memory.WriteUInt32(0x8000_0024, 1);
        memory.WriteUInt32(0x8000_0028, PhysicalMemorySize);
        memory.WriteUInt32(0x8000_002C, ConsoleType);
        memory.WriteUInt32(0x8000_00F0, PhysicalMemorySize);
        memory.WriteUInt32(0x8000_00F8, BusClockSpeed);
        memory.WriteUInt32(0x8000_00FC, CoreClockSpeed);

        InstallArena(memory, executable, trace);
        InstallFileSystem(memory, disc, trace);
        InstallDefaultExceptionHandlers(memory);

        cpu.Msr = InitialMsr;
        cpu.Gpr.Clear();
        cpu.Gpr[1] = InitialStackPointer;
        cpu.Cr = 0;
        cpu.Xer = 0;
        cpu.Lr = 0;
        cpu.Ctr = 0;
        cpu.TimeBase = 0;

        trace.Write(
            GameCubeTraceChannel.Boot,
            GameCubeTraceLevel.Information,
            $"boot state installed: memory={PhysicalMemorySize / (1024 * 1024)} MB " +
            $"console=0x{ConsoleType:X8} msr=0x{InitialMsr:X8} sp=0x{InitialStackPointer:X8}");
    }

    /// <summary>
    /// Marks the bottom of the free memory the game may allocate from: the end
    /// of everything the executable occupies.
    /// </summary>
    /// <remarks>
    /// Leaving this at zero is not a harmless omission. The operating system
    /// hands out memory from here upward, so a game that allocates gets
    /// addresses starting at zero — it then writes its own structures over the
    /// low-memory globals, including the pointer to the running thread, and
    /// the scheduler starts following a pointer into nowhere. The symptom
    /// appears hundreds of millions of instructions later and looks nothing
    /// like its cause.
    /// </remarks>
    private static void InstallArena(
        GameCubeMemory memory,
        GameCubeExecutable executable,
        GameCubeTraceLog trace)
    {
        var top = executable.BssAddress + executable.BssSize;
        foreach (var section in executable.Sections)
        {
            top = Math.Max(top, section.EndAddress);
        }

        // Rounded up to a cache line, as the apploader leaves it.
        var arenaLow = (top + 31) & ~31u;
        memory.WriteUInt32(0x8000_0030, arenaLow);

        trace.Write(
            GameCubeTraceChannel.Boot,
            GameCubeTraceLevel.Debug,
            $"arena low set to 0x{arenaLow:X8}, past the executable's last section and BSS");
    }

    /// <summary>
    /// Places the file table where the apploader would and points the OS
    /// globals at it. A game that opens a file before doing anything else —
    /// and many do — reads these two words to find the table.
    /// </summary>
    private static void InstallFileSystem(
        GameCubeMemory memory,
        GameCubeDisc disc,
        GameCubeTraceLog trace)
    {
        var size = disc.Header.FileSystemSize;
        if (size == 0 || size > GameCubeMemory.MainMemorySize / 2)
        {
            trace.Write(
                GameCubeTraceChannel.Boot,
                GameCubeTraceLevel.Warning,
                $"file table not installed: the disc declares {size} bytes");
            return;
        }

        // The apploader puts it at the top of memory and hands everything
        // below to the arena. Rounded down to a cache line, as it would be.
        var top = GameCubeMemory.CachedBase + GameCubeMemory.MainMemorySize - 0x2000;
        var address = (top - size) & ~31u;

        memory.Write(address, disc.Read(disc.Header.FileSystemOffset, (int)size));
        memory.WriteUInt32(0x8000_0038, address);
        memory.WriteUInt32(0x8000_003C, disc.Header.MaximumFileSystemSize);

        // Arena high is where the apploader stopped, immediately below the
        // table. Arena low is set separately, from the executable's extent.
        memory.WriteUInt32(0x8000_0034, address);

        // The second half of the disc header lives just above the table, and
        // the OS finds it through this pointer.
        var informationAddress = top;
        memory.Write(informationAddress, disc.Read(0x440, 0x2000));
        memory.WriteUInt32(DiscHeaderInformationAddress, informationAddress);

        trace.Write(
            GameCubeTraceChannel.Boot,
            GameCubeTraceLevel.Debug,
            $"file table at 0x{address:X8}+0x{size:X}, disc information at " +
            $"0x{informationAddress:X8}, arena high 0x{address:X8}");
    }

    /// <summary>
    /// Installs a bare <c>rfi</c> at each exception vector. Nothing raises
    /// exceptions yet, but a game that installs its own handlers reads these
    /// locations first, and a vector full of zeroes decodes as an illegal
    /// instruction rather than as an empty handler.
    /// </summary>
    private static void InstallDefaultExceptionHandlers(GameCubeMemory memory)
    {
        foreach (var vector in ExceptionVectors)
        {
            memory.WriteUInt32(vector, ReturnFromInterrupt);
        }
    }
}
