namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// The Flipper command processor: the FIFO a game feeds through the write-gather
/// pipe, and the decoder that reads back what it was asked to draw.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately stops short of drawing anything. What it does is make the
/// graphics stream <em>legible</em>: every command a game sends is decoded,
/// named and counted, so the work queue for the rest of GX comes from what
/// Super Mario Sunshine actually sends rather than from a feature list. That is
/// the same reason the trace log was built before the interpreter was.
/// </para>
/// <para>
/// The one part that cannot be approximated is vertex size. A primitive command
/// carries a vertex count and nothing else — the bytes per vertex come from the
/// current vertex descriptor and attribute table, which were set by earlier
/// commands. Get it wrong by one byte and the decoder resynchronises on garbage
/// and every command after it is fiction. So the descriptor is modelled fully
/// even though nothing consumes the vertices yet: it is what keeps the rest of
/// the stream trustworthy.
/// </para>
/// </remarks>
public sealed class GameCubeCommandProcessor
{
    // --- FIFO opcodes ------------------------------------------------------
    private const byte OpNop = 0x00;
    private const byte OpLoadCpRegister = 0x08;
    private const byte OpLoadXfRegister = 0x10;
    private const byte OpLoadIndexA = 0x20;
    private const byte OpLoadIndexB = 0x28;
    private const byte OpLoadIndexC = 0x30;
    private const byte OpLoadIndexD = 0x38;
    private const byte OpCallDisplayList = 0x40;
    private const byte OpInvalidateVertexCache = 0x48;
    private const byte OpLoadBpRegister = 0x61;

    /// <summary>The lowest primitive opcode; the eight kinds run in steps of eight.</summary>
    private const byte OpPrimitiveFirst = 0x80;
    private const byte OpPrimitiveLast = 0xBF;

    /// <summary>How deep a display list may call before this gives up.</summary>
    private const int MaximumDisplayListDepth = 8;

    /// <summary>
    /// The eight primitive kinds, indexed by <c>(opcode - 0x80) / 8</c>.
    /// </summary>
    private static readonly string[] PrimitiveNames =
    [
        "quads", "quads2", "triangles", "triangle strip",
        "triangle fan", "lines", "line strip", "points"
    ];

    /// <summary>
    /// Bytes per component for the five numeric formats a vertex attribute can
    /// use: u8, s8, u16, s16, f32.
    /// </summary>
    private static readonly int[] ComponentSizes = [1, 1, 2, 2, 4];

    /// <summary>
    /// Bytes per colour for the six packed colour formats: RGB565, RGB888,
    /// RGB888x, RGBA4444, RGBA6666, RGBA8888.
    /// </summary>
    private static readonly int[] ColourSizes = [2, 3, 4, 2, 3, 4];

    private readonly GameCubeMemory _memory;
    private readonly GameCubeTraceLog _trace;

    /// <summary>Vertex descriptor: which attributes are present, and how.</summary>
    private uint _vertexDescriptorLow;
    private uint _vertexDescriptorHigh;

    /// <summary>Vertex attribute table: how each present attribute is encoded.</summary>
    private readonly uint[] _attributeA = new uint[8];
    private readonly uint[] _attributeB = new uint[8];
    private readonly uint[] _attributeC = new uint[8];

    private long _commandsDecoded;
    private long _verticesSeen;
    private uint _lastFifoStart;
    private uint _lastFifoEnd;

    public GameCubeCommandProcessor(GameCubeMemory memory, GameCubeTraceLog trace)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(trace);
        _memory = memory;
        _trace = trace;
    }

    /// <summary>How many FIFO commands have been decoded since reset.</summary>
    public long CommandsDecoded => _commandsDecoded;

    /// <summary>How many vertices those commands carried.</summary>
    public long VerticesSeen => _verticesSeen;

    public void Reset()
    {
        _vertexDescriptorLow = 0;
        _vertexDescriptorHigh = 0;
        Array.Clear(_attributeA);
        Array.Clear(_attributeB);
        Array.Clear(_attributeC);
        _commandsDecoded = 0;
        _verticesSeen = 0;
        _lastFifoStart = 0;
        _lastFifoEnd = 0;
    }

    /// <summary>
    /// Decodes a run of the command stream starting at <paramref name="address"/>
    /// and stopping at <paramref name="end"/>, and returns where it stopped.
    /// </summary>
    /// <remarks>
    /// A command that runs past the end of the run leaves the address on the
    /// opcode rather than consuming it, so the next call resumes on a command
    /// boundary. The FIFO is written faster than it is read and the two ends
    /// meet mid-command routinely; treating a partial command as a complete one
    /// is how a decoder ends up resynchronising on vertex data.
    /// </remarks>
    public uint Decode(uint address, uint end, int depth = 0)
    {
        while (address < end)
        {
            var opcode = _memory.ReadByte(address);
            if (!TryMeasureCommand(opcode, address, end, out var length))
            {
                return address;
            }

            Execute(opcode, address, depth);
            address += (uint)length;
            _commandsDecoded++;
        }

        return address;
    }

    /// <summary>
    /// Works out how many bytes a command occupies, including its payload.
    /// Returns false when the run ends before the command does.
    /// </summary>
    private bool TryMeasureCommand(byte opcode, uint address, uint end, out int length)
    {
        length = opcode switch
        {
            OpNop => 1,
            OpInvalidateVertexCache => 1,
            OpLoadCpRegister => 6,
            OpLoadBpRegister => 5,
            OpLoadIndexA or OpLoadIndexB or OpLoadIndexC or OpLoadIndexD => 5,
            OpCallDisplayList => 9,
            OpLoadXfRegister => 0,
            >= OpPrimitiveFirst and <= OpPrimitiveLast => 0,
            _ => -1
        };

        if (length == -1)
        {
            // Not a command at all. Reporting it rather than skipping a byte
            // and hoping: a decoder that resynchronises silently turns one bug
            // into a stream of plausible nonsense.
            //
            // The bytes either side are the whole diagnosis. "Opcode 0x3F is
            // wrong" says nothing; the surrounding stream says whether this is
            // a valid command read at the wrong offset, data being read as
            // commands, or an empty ring being decoded before anything arrived.
            _trace.WriteOnce(
                GameCubeTraceChannel.Graphics,
                GameCubeTraceLevel.Error,
                $"gx/unknown-opcode/0x{opcode:X2}",
                $"unrecognised FIFO opcode 0x{opcode:X2} at 0x{address:X8}, " +
                $"decoding up to 0x{end:X8}: {DescribeBytes(address >= 16 ? address - 16 : 0, 48)}");
            length = 1;
            return address + 1 <= end;
        }

        if (opcode == OpLoadXfRegister)
        {
            // Five bytes of header, not four: the opcode is followed by a whole
            // 32-bit word whose upper half holds the word count less one and
            // whose lower half is the register address. Counting the header as
            // four leaves the decoder one byte short, so it resumes on the last
            // byte of this command's payload and reads it as the next opcode —
            // which is precisely the 0x3F that took the stream out of step.
            if (address + 5 > end)
            {
                return false;
            }

            length = 5 + ((_memory.ReadUInt16(address + 1) & 0xF) + 1) * 4;
        }
        else if (opcode >= OpPrimitiveFirst)
        {
            // Three-byte header: the opcode carries the format index, then a
            // sixteen-bit vertex count.
            if (address + 3 > end)
            {
                return false;
            }

            length = 3 + (_memory.ReadUInt16(address + 1) * VertexSize(opcode & 7));
        }

        return address + (uint)length <= end;
    }

    /// <summary>Applies a command's effect, and records that it happened.</summary>
    private void Execute(byte opcode, uint address, int depth)
    {
        switch (opcode)
        {
            case OpNop:
            case OpInvalidateVertexCache:
                return;

            case OpLoadCpRegister:
                LoadCommandProcessorRegister(
                    _memory.ReadByte(address + 1),
                    _memory.ReadUInt32(address + 2));
                return;

            case OpLoadBpRegister:
            {
                var packed = _memory.ReadUInt32(address + 1);
                _trace.WriteOnce(
                    GameCubeTraceChannel.Graphics,
                    GameCubeTraceLevel.Debug,
                    $"gx/bp/0x{packed >> 24:X2}",
                    $"blitting processor register 0x{packed >> 24:X2} = 0x{packed & 0xFF_FFFF:X6}");
                return;
            }

            case OpLoadXfRegister:
            {
                var target = _memory.ReadUInt16(address + 3);
                _trace.WriteOnce(
                    GameCubeTraceChannel.Graphics,
                    GameCubeTraceLevel.Debug,
                    $"gx/xf/0x{target:X4}",
                    $"transform unit register 0x{target:X4}, " +
                    $"{(_memory.ReadUInt16(address + 1) & 0xF) + 1} words");
                return;
            }

            case OpLoadIndexA:
            case OpLoadIndexB:
            case OpLoadIndexC:
            case OpLoadIndexD:
                _trace.WriteOnce(
                    GameCubeTraceChannel.Graphics,
                    GameCubeTraceLevel.Debug,
                    $"gx/indexed-load/0x{opcode:X2}",
                    $"indexed transform load 0x{opcode:X2}");
                return;

            case OpCallDisplayList:
                CallDisplayList(
                    _memory.ReadUInt32(address + 1),
                    _memory.ReadUInt32(address + 5),
                    depth);
                return;

            default:
            {
                var vertices = _memory.ReadUInt16(address + 1);
                _verticesSeen += vertices;
                var kind = PrimitiveNames[(opcode - OpPrimitiveFirst) / 8];
                _trace.WriteEvery(
                    GameCubeTraceChannel.Graphics,
                    GameCubeTraceLevel.Debug,
                    $"gx/primitive/{kind}",
                    256,
                    $"{kind}: {vertices} vertices, format {opcode & 7}, " +
                    $"{VertexSize(opcode & 7)} bytes each");
                return;
            }
        }
    }

    /// <summary>
    /// Runs a display list: a command stream held in main memory rather than
    /// pushed through the FIFO, which is how a game replays static geometry
    /// without paying to send it again.
    /// </summary>
    private void CallDisplayList(uint address, uint size, int depth)
    {
        if (depth >= MaximumDisplayListDepth)
        {
            _trace.WriteOnce(
                GameCubeTraceChannel.Graphics,
                GameCubeTraceLevel.Warning,
                "gx/display-list-depth",
                $"display list at 0x{address:X8} nested more than " +
                $"{MaximumDisplayListDepth} deep; not followed");
            return;
        }

        _trace.WriteEvery(
            GameCubeTraceChannel.Graphics,
            GameCubeTraceLevel.Debug,
            "gx/display-list",
            256,
            $"display list at 0x{address:X8}, {size} bytes");

        Decode(address, address + size, depth + 1);
    }

    /// <summary>
    /// Applies a write to one of the command processor's own registers. Only
    /// the vertex descriptor and attribute table are kept, because only they
    /// change how the stream that follows is measured.
    /// </summary>
    private void LoadCommandProcessorRegister(byte register, uint value)
    {
        switch (register)
        {
            case 0x50:
                _vertexDescriptorLow = value;
                break;
            case 0x60:
                _vertexDescriptorHigh = value;
                break;
            case >= 0x70 and <= 0x77:
                _attributeA[register & 7] = value;
                break;
            case >= 0x80 and <= 0x87:
                _attributeB[register & 7] = value;
                break;
            case >= 0x90 and <= 0x97:
                _attributeC[register & 7] = value;
                break;
            default:
                _trace.WriteOnce(
                    GameCubeTraceChannel.Graphics,
                    GameCubeTraceLevel.Debug,
                    $"gx/cp/0x{register:X2}",
                    $"command processor register 0x{register:X2} = 0x{value:X8}");
                break;
        }
    }

    // ------------------------------------------------------------ vertex size

    /// <summary>
    /// How many bytes one vertex occupies under the current descriptor and the
    /// given attribute format index. This is what keeps the decoder in step.
    /// </summary>
    private int VertexSize(int format)
    {
        var low = _vertexDescriptorLow;
        var high = _vertexDescriptorHigh;
        var a = _attributeA[format];
        var b = _attributeB[format];
        var c = _attributeC[format];

        var size = 0;

        // Matrix indices are a plain byte each when present: one for position
        // and one for each of the eight texture coordinates.
        size += (int)(low & 1);
        for (var texture = 0; texture < 8; texture++)
        {
            size += (int)((low >> (1 + texture)) & 1);
        }

        // Position: two or three components, in one of the five formats.
        size += AttributeSize(
            (low >> 9) & 3,
            ((a >> 1) & 7),
            ((a & 1) != 0 ? 3 : 2));

        // Normal: three components, or nine when the attribute carries a
        // normal, a binormal and a tangent together.
        size += AttributeSize(
            (low >> 11) & 3,
            (a >> 10) & 7,
            ((a >> 9) & 1) != 0 ? 9 : 3);

        // The two colours, each in one of the six packed formats.
        size += ColourAttributeSize((low >> 13) & 3, (a >> 14) & 7);
        size += ColourAttributeSize((low >> 15) & 3, (a >> 18) & 7);

        // Texture coordinates zero to seven: one or two components each.
        // Coordinate zero lives in attribute word A; one to four in B; five to
        // seven in C. The packing is not uniform — word C starts five bits in,
        // because coordinate four's fractional bits were left behind there when
        // its element and format bits ran out of room at the top of B. Only the
        // element and format fields matter for size, so the stray fraction is
        // skipped rather than modelled.
        size += AttributeSize(high & 3, (a >> 22) & 7, ((a >> 21) & 1) != 0 ? 2 : 1);
        for (var texture = 1; texture < 8; texture++)
        {
            var word = texture < 5 ? b : c;
            var shift = texture < 5 ? (texture - 1) * 9 : 5 + ((texture - 5) * 9);
            size += AttributeSize(
                (high >> (texture * 2)) & 3,
                (word >> (shift + 1)) & 7,
                ((word >> shift) & 1) != 0 ? 2 : 1);
        }

        return size;
    }

    /// <summary>
    /// The bytes one attribute contributes, given how it is referenced, its
    /// component format and how many components it has.
    /// </summary>
    /// <remarks>
    /// The reference kind is what matters first: an attribute may be absent,
    /// sent inline, or replaced by an eight- or sixteen-bit index into an array
    /// the transform unit reads separately. An indexed attribute costs only its
    /// index in the stream however large the data behind it is.
    /// </remarks>
    private static int AttributeSize(uint reference, uint format, int components) =>
        reference switch
        {
            0 => 0,
            1 => components * ComponentSizes[Math.Min(format, 4)],
            2 => 1,
            _ => 2
        };

    /// <summary>Formats a run of memory as hex, for reading a stream by eye.</summary>
    private string DescribeBytes(uint address, int count)
    {
        var text = new System.Text.StringBuilder(count * 3);
        for (var index = 0; index < count; index++)
        {
            text.Append(index == 16 ? " [" : index == 17 ? "" : " ");
            text.Append($"{_memory.ReadByte(address + (uint)index):X2}");
            if (index == 16)
            {
                text.Append(']');
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Records the FIFO's bounds when they change, so a stream that decodes
    /// wrongly can be checked against where it was supposed to live.
    /// </summary>
    /// <remarks>
    /// Only on change, and deliberately. Counting this on every push made it
    /// the busiest key in the tally, and the stall report names the busiest key
    /// as its best guess at what a run is stuck on — so a diagnostic added to
    /// explain one problem became the answer to every other question. An
    /// observation about the emulator must never be able to outrank what the
    /// game is doing.
    /// </remarks>
    public void NoteFifoConfiguration(uint start, uint end, uint read, uint write)
    {
        if (start == _lastFifoStart && end == _lastFifoEnd)
        {
            return;
        }

        _lastFifoStart = start;
        _lastFifoEnd = end;
        _trace.Write(
            GameCubeTraceChannel.Graphics,
            GameCubeTraceLevel.Information,
            $"FIFO base=0x{start:X8} end=0x{end:X8} read=0x{read:X8} write=0x{write:X8} " +
            $"({end - start} bytes)");
    }

    private static int ColourAttributeSize(uint reference, uint format) =>
        reference switch
        {
            0 => 0,
            1 => ColourSizes[Math.Min(format, 5)],
            2 => 1,
            _ => 2
        };
}
