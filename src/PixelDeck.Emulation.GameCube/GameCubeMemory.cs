using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// The GameCube's address space, so far as PixelCube models it.
/// </summary>
/// <remarks>
/// Only the parts a boot needs exist today: 24 MB of main memory, 16 MB of
/// ARAM, and the segment translation that makes 0x8000_0000 and 0xC000_0000
/// two views of the same physical bytes. Everything else — the hardware
/// register block at 0xCC00_0000, the embedded framebuffer, the L2 locked
/// cache at 0xE000_0000 — is deliberately absent, and every access to it is
/// reported through <see cref="GameCubeTraceChannel.Unimplemented"/> rather
/// than quietly returning zero.
///
/// That choice is the whole point of building the trace log first. A memory
/// map that answers every read with zero produces a black screen and no
/// explanation; this one produces a black screen and a list of exactly which
/// registers the game wanted.
/// </remarks>
public sealed class GameCubeMemory
{
    /// <summary>Retail main memory: 24 MB of 1T-SRAM.</summary>
    public const int MainMemorySize = 24 * 1024 * 1024;

    /// <summary>The audio DRAM the DSP streams from: 16 MB.</summary>
    public const int AuxiliaryMemorySize = 16 * 1024 * 1024;

    /// <summary>Cached main memory begins here in the effective address space.</summary>
    public const uint CachedBase = 0x8000_0000;

    /// <summary>Uncached main memory: the same bytes, seen past the cache.</summary>
    public const uint UncachedBase = 0xC000_0000;

    /// <summary>The memory-mapped hardware register block.</summary>
    public const uint HardwareRegisterBase = 0xCC00_0000;

    private readonly byte[] _mainMemory = new byte[MainMemorySize];
    private readonly byte[] _auxiliaryMemory = new byte[AuxiliaryMemorySize];
    private readonly GameCubeTraceLog _trace;

    public GameCubeMemory(GameCubeTraceLog trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        _trace = trace;
        Hardware = new GameCubeHardware(trace, this);
    }

    /// <summary>The memory-mapped register block.</summary>
    public GameCubeHardware Hardware { get; }

    /// <summary>
    /// Where execution currently is, for diagnostics only.
    /// </summary>
    /// <remarks>
    /// A trace line saying an unmapped write happened at some address is half
    /// an answer: the address a wild pointer lands on says nothing about which
    /// code produced it, and the code is what needs fixing. The CPU keeps this
    /// current so memory can name the instruction responsible. Nothing reads
    /// it except the trace.
    /// </remarks>
    public uint InstructionAddress { get; set; }

    /// <summary>
    /// A range of addresses whose writes are reported, with the instruction
    /// that made them. Zero length watches nothing.
    /// </summary>
    /// <remarks>
    /// The question "what corrupted this" has no answer in a tally of where
    /// wild pointers landed — the damage and its cause are in different places
    /// and usually far apart in time. A watchpoint is the only cheap way to
    /// close that gap: name the address that ends up wrong, and the trace names
    /// the code that wrote it.
    /// </remarks>
    public uint WatchAddress { get; set; }

    public int WatchLength { get; set; }

    /// <summary>
    /// Reports a write that overlaps the watched range. Checked on the write
    /// paths only, and skipped entirely when nothing is being watched.
    /// </summary>
    private void ReportWatchedWrite(uint address, int length, ulong value)
    {
        if (WatchLength <= 0)
        {
            return;
        }

        var physical = address & 0x3FFF_FFFF;
        var watched = WatchAddress & 0x3FFF_FFFF;
        if (physical + (uint)length <= watched || physical >= watched + (uint)WatchLength)
        {
            return;
        }

        _trace.Write(
            GameCubeTraceChannel.Memory,
            GameCubeTraceLevel.Information,
            $"watch: {length}-byte write of 0x{value:X} to 0x{address:X8} " +
            $"from 0x{InstructionAddress:X8}");
    }

    public Span<byte> MainMemory => _mainMemory;

    public Span<byte> AuxiliaryMemory => _auxiliaryMemory;

    /// <summary>
    /// Turns an effective address into a main-memory index, or returns false
    /// when it falls outside the memory PixelCube has.
    /// </summary>
    public static bool TryTranslate(uint address, out int physicalOffset)
    {
        var physical = address & 0x3FFF_FFFF;
        if (physical < MainMemorySize)
        {
            physicalOffset = (int)physical;
            return true;
        }

        physicalOffset = -1;
        return false;
    }

    public byte ReadByte(uint address)
    {
        if (TryTranslate(address, out var offset))
        {
            return _mainMemory[offset];
        }

        if (GameCubeHardware.Contains(address))
        {
            return (byte)Hardware.Read(address, 1);
        }

        ReportUnmapped(address, "read", 1);
        return 0;
    }

    public ushort ReadUInt16(uint address)
    {
        if (TryTranslate(address, out var offset) && offset + 2 <= MainMemorySize)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(_mainMemory.AsSpan(offset, 2));
        }

        if (GameCubeHardware.Contains(address))
        {
            return (ushort)Hardware.Read(address, 2);
        }

        ReportUnmapped(address, "read", 2);
        return 0;
    }

    public uint ReadUInt32(uint address)
    {
        if (TryTranslate(address, out var offset) && offset + 4 <= MainMemorySize)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(_mainMemory.AsSpan(offset, 4));
        }

        if (GameCubeHardware.Contains(address))
        {
            return Hardware.Read(address, 4);
        }

        ReportUnmapped(address, "read", 4);
        return 0;
    }

    /// <summary>
    /// Reads a word without reporting anything, and says whether the address
    /// was mapped at all. Instruction fetch uses this: a fetch from nowhere is
    /// a fault the CPU has to stop on, not a zero to carry on with, and
    /// reporting it as an unmapped data read would put it under the wrong key.
    /// </summary>
    public bool TryReadInstruction(uint address, out uint instruction)
    {
        if (TryTranslate(address, out var offset) && offset + 4 <= MainMemorySize)
        {
            instruction = BinaryPrimitives.ReadUInt32BigEndian(_mainMemory.AsSpan(offset, 4));
            return true;
        }

        instruction = 0;
        return false;
    }

    public void WriteByte(uint address, byte value)
    {
        ReportWatchedWrite(address, 1, value);
        if (TryTranslate(address, out var offset))
        {
            _mainMemory[offset] = value;
            return;
        }

        if (GameCubeHardware.Contains(address))
        {
            Hardware.Write(address, 1, value);
            return;
        }

        ReportUnmapped(address, "write", 1);
    }

    public void WriteUInt16(uint address, ushort value)
    {
        ReportWatchedWrite(address, 2, value);
        if (TryTranslate(address, out var offset) && offset + 2 <= MainMemorySize)
        {
            BinaryPrimitives.WriteUInt16BigEndian(_mainMemory.AsSpan(offset, 2), value);
            return;
        }

        if (GameCubeHardware.Contains(address))
        {
            Hardware.Write(address, 2, value);
            return;
        }

        ReportUnmapped(address, "write", 2);
    }

    public void WriteUInt32(uint address, uint value)
    {
        ReportWatchedWrite(address, 4, value);
        if (TryTranslate(address, out var offset) && offset + 4 <= MainMemorySize)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_mainMemory.AsSpan(offset, 4), value);
            return;
        }

        if (GameCubeHardware.Contains(address))
        {
            Hardware.Write(address, 4, value);
            return;
        }

        ReportUnmapped(address, "write", 4);
    }

    public ulong ReadUInt64(uint address)
    {
        if (TryTranslate(address, out var offset) && offset + 8 <= MainMemorySize)
        {
            return BinaryPrimitives.ReadUInt64BigEndian(_mainMemory.AsSpan(offset, 8));
        }

        ReportUnmapped(address, "read", 8);
        return 0;
    }

    public void WriteUInt64(uint address, ulong value)
    {
        ReportWatchedWrite(address, 8, value);
        if (TryTranslate(address, out var offset) && offset + 8 <= MainMemorySize)
        {
            BinaryPrimitives.WriteUInt64BigEndian(_mainMemory.AsSpan(offset, 8), value);
            return;
        }

        ReportUnmapped(address, "write", 8);
    }

    /// <summary>
    /// Copies a block into main memory, reporting and clipping anything that
    /// would fall outside it.
    /// </summary>
    public void Write(uint address, ReadOnlySpan<byte> source)
    {
        ReportWatchedWrite(address, source.Length, 0);
        if (!TryTranslate(address, out var offset))
        {
            ReportUnmapped(address, "block write", source.Length);
            return;
        }

        var writable = Math.Min(source.Length, MainMemorySize - offset);
        if (writable < source.Length)
        {
            _trace.Write(
                GameCubeTraceChannel.Memory,
                GameCubeTraceLevel.Warning,
                $"block write at 0x{address:X8} runs {source.Length - writable} bytes " +
                "past the end of main memory and was clipped");
        }

        source[..writable].CopyTo(_mainMemory.AsSpan(offset, writable));
    }

    /// <summary>Fills a range with zeroes, as the BSS setup does.</summary>
    public void Clear(uint address, uint length)
    {
        if (!TryTranslate(address, out var offset))
        {
            ReportUnmapped(address, "clear", (int)length);
            return;
        }

        var clearable = (int)Math.Min(length, (uint)(MainMemorySize - offset));
        _mainMemory.AsSpan(offset, clearable).Clear();
    }

    /// <summary>
    /// Names the region an address belongs to, so a trace line says
    /// "hardware registers" rather than only a number.
    /// </summary>
    public static string DescribeRegion(uint address) => address switch
    {
        >= 0xE000_0000 => "locked cache",
        >= HardwareRegisterBase => DescribeHardwareBlock(address),
        >= UncachedBase => "uncached main memory",
        >= CachedBase => "cached main memory",
        _ => "physical / translated"
    };

    /// <summary>
    /// Names the hardware block an address in the register window belongs to.
    /// </summary>
    /// <remarks>
    /// Worth the table. A tally that says "978,850 reads of hardware
    /// registers" tells you where to look and nothing more; one that says
    /// "978,850 reads of DI at 0x28" names the register a game is spinning on
    /// and turns the number into an instruction.
    /// </remarks>
    public static string DescribeHardwareBlock(uint address) => (address & 0x00FF_FFFF) switch
    {
        < 0x1000 => "CP command processor",
        < 0x2000 => "PE pixel engine",
        < 0x3000 => "VI video interface",
        < 0x4000 => "PI processor interface",
        < 0x5000 => "MI memory interface",
        < 0x6000 => "DSP / ARAM / audio DMA",
        < 0x6400 => "DI DVD interface",
        < 0x6800 => "SI serial interface",
        < 0x6C00 => "EXI external interface",
        < 0x7000 => "AI audio interface",
        < 0x8000 => "unassigned register window",
        _ => "GX command FIFO"
    };

    /// <summary>The offset of an address within its hardware block.</summary>
    public static uint GetHardwareBlockOffset(uint address)
    {
        var offset = address & 0x00FF_FFFF;
        return offset switch
        {
            < 0x6000 => offset & 0xFFF,
            < 0x6400 => offset - 0x6000,
            < 0x6800 => offset - 0x6400,
            < 0x6C00 => offset - 0x6800,
            < 0x7000 => offset - 0x6C00,
            < 0x8000 => offset - 0x7000,
            _ => offset - 0x8000
        };
    }

    private void ReportUnmapped(uint address, string operation, int length)
    {
        // Keyed by region and operation rather than by address: a game polling
        // one unimplemented register produces one line and a count, not one
        // line per poll. The key itself is cached, because building it on
        // every access would reintroduce exactly the per-access allocation
        // that suppressing the message is meant to avoid.
        var region = DescribeRegion(address);

        // Hardware registers are keyed individually, because "which register"
        // is the whole question there. Everywhere else the region is enough:
        // a game walking off the end of memory does not produce a useful list
        // of addresses, only a useful count.
        var isRegister = address >= HardwareRegisterBase && address < 0xE000_0000;
        var key = isRegister
            // Keyed on the exact address, not the containing word: most of
            // this space is 16-bit registers, and rounding to a word merges
            // two unrelated ones into a count that names neither.
            ? RegisterKeys.GetOrAdd(
                (operation, address),
                static pair => $"register/{pair.Operation}/" +
                    $"{DescribeHardwareBlock(pair.Address)}+0x{GetHardwareBlockOffset(pair.Address):X3}")
            // Keyed by the instruction rather than the region. "Thirty-five
            // million reads went nowhere" says how bad it is; "this one load
            // did it" says what to fix, and the number of code sites making
            // wild accesses is small even when the number of accesses is not.
            : UnmappedKeys.GetOrAdd(
                (operation, InstructionAddress),
                static pair => $"memory/{pair.Operation}/from 0x{pair.Address:X8}");

        _trace.WriteOnce(
            GameCubeTraceChannel.Unimplemented,
            GameCubeTraceLevel.Warning,
            key,
            $"unmapped {operation} of {length} byte(s) at 0x{address:X8} ({region}" +
            (isRegister ? $" +0x{GetHardwareBlockOffset(address):X3})" : ")") +
            $" from 0x{InstructionAddress:X8}");
    }

    private static readonly ConcurrentDictionary<(string Operation, uint Address), string>
        UnmappedKeys = new();

    private static readonly ConcurrentDictionary<(string Operation, uint Address), string>
        RegisterKeys = new();
}
