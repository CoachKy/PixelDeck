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

    /// <summary>The blitting processor's registers, kept so a copy can read them.</summary>
    private readonly uint[] _blitting = new uint[256];

    /// <summary>
    /// Where each indexed attribute's array starts and how far apart its
    /// entries are. A vertex that names an index rather than carrying its data
    /// is read from here: base plus index times stride.
    /// </summary>
    private readonly uint[] _arrayBase = new uint[16];
    private readonly uint[] _arrayStride = new uint[16];

    /// <summary>The embedded framebuffer and the code that draws into it.</summary>
    public GameCubeRasterizer Rasterizer { get; } = new();

    public GameCubeCommandProcessor(GameCubeMemory memory, GameCubeTraceLog trace)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(trace);
        _memory = memory;
        _trace = trace;
    }

    /// <summary>
    /// The size of the picture the last display copy produced, or zero before
    /// one has happened.
    /// </summary>
    public int DisplayWidth { get; private set; }

    public int DisplayHeight { get; private set; }

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
        Array.Clear(_blitting);
        Array.Clear(_arrayBase);
        Array.Clear(_arrayStride);
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
        else if (opcode is >= OpPrimitiveFirst and <= OpPrimitiveLast)
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
                var register = packed >> 24;
                var value = packed & 0x00FF_FFFF;
                _blitting[register & 0xFF] = value;
                _trace.WriteOnce(
                    GameCubeTraceChannel.Graphics,
                    GameCubeTraceLevel.Debug,
                    $"gx/bp/0x{register:X2}",
                    $"blitting processor register 0x{register:X2} = 0x{value:X6}");

                switch (register)
                {
                    case BpTriggerCopy:
                        ExecuteDisplayCopy(value);
                        break;

                    // Which faces to discard lives in bits 14 and 15 of the
                    // general mode register.
                    case BpGeneralMode:
                        Rasterizer.Cull = (GameCubeRasterizer.Culling)((value >> 14) & 3);
                        break;

                    // End of the drawing list. The bit matters: this register
                    // is written for other reasons too, and only bit one means
                    // a game is waiting to be told the list is done.
                    case BpSetDrawDone when (value & 2) != 0:
                        _memory.Hardware.SignalPixelEngineFinish();
                        break;

                    case BpPixelEngineToken:
                        _memory.Hardware.SignalPixelEngineToken((ushort)value, false);
                        break;

                    case BpPixelEngineTokenInterrupt:
                        _memory.Hardware.SignalPixelEngineToken((ushort)value, true);
                        break;
                }

                return;
            }

            case OpLoadXfRegister:
            {
                var count = (_memory.ReadUInt16(address + 1) & 0xF) + 1;
                var target = _memory.ReadUInt16(address + 3);
                for (var word = 0; word < count; word++)
                {
                    Rasterizer.SetTransformRegister(
                        (uint)(target + word),
                        _memory.ReadUInt32(address + 5 + (uint)(word * 4)));
                }

                _trace.WriteOnce(
                    GameCubeTraceChannel.Graphics,
                    GameCubeTraceLevel.Debug,
                    $"gx/xf/0x{target:X4}",
                    $"transform unit register 0x{target:X4}, {count} words");
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
                // Only the primitives reach here legitimately. An opcode that
                // was already reported as unrecognised is skipped a byte at a
                // time, and arrives here too — treating it as a primitive
                // indexes off the front of the table and throws, turning a
                // stream that is merely out of step into a crash.
                if (opcode is < OpPrimitiveFirst or > OpPrimitiveLast)
                {
                    return;
                }

                var vertices = _memory.ReadUInt16(address + 1);
                _verticesSeen += vertices;
                var kind = PrimitiveNames[(opcode - OpPrimitiveFirst) / 8];
                var format = opcode & 7;
                var size = VertexSize(format);
                _trace.WriteEvery(
                    GameCubeTraceChannel.Graphics,
                    GameCubeTraceLevel.Debug,
                    $"gx/primitive/{kind}",
                    256,
                    $"{kind}: {vertices} vertices, format {format}, {size} bytes each");

                DrawPrimitive((opcode - OpPrimitiveFirst) / 8, address + 3, vertices, format, size);
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
            // Where the indexed attributes live, and how far apart their
            // entries are. Array zero is position, one is normal, two and three
            // the colours, four to eleven the texture coordinates.
            case >= 0xA0 and <= 0xAF:
                _arrayBase[register & 0xF] = value;
                break;

            case >= 0xB0 and <= 0xBF:
                _arrayStride[register & 0xF] = value;
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

    /// <summary>
    /// Reads a run of vertices out of the command stream and draws them.
    /// </summary>
    /// <remarks>
    /// Only the position and the first colour are read. Everything else in a
    /// vertex — normals, texture coordinates, matrix indices — is measured so
    /// the stream stays in step and then skipped, because nothing downstream
    /// consumes it yet. Positions given by index rather than inline are not
    /// followed either: that needs the vertex arrays the transform unit reads
    /// separately, and a run containing them is reported rather than drawn
    /// wrongly.
    /// </remarks>
    private void DrawPrimitive(int kind, uint address, int count, int format, int size)
    {
        if (count <= 0 || size <= 0)
        {
            return;
        }

        var positionReference = (_vertexDescriptorLow >> 9) & 3;
        if (positionReference == 0)
        {
            return;
        }

        var attributes = _attributeA[format];
        var components = (attributes & 1) != 0 ? 3 : 2;
        var positionFormat = (attributes >> 1) & 7;
        var fraction = (int)((attributes >> 4) & 0x1F);
        var colourReference = (_vertexDescriptorLow >> 13) & 3;
        var colourFormat = (attributes >> 14) & 7;

        // The matrix indices sit ahead of the position when present.
        var lead = (int)(_vertexDescriptorLow & 1);
        for (var texture = 0; texture < 8; texture++)
        {
            lead += (int)((_vertexDescriptorLow >> (1 + texture)) & 1);
        }

        var vertices = new GameCubeRasterizer.Vertex[count];
        for (var index = 0; index < count; index++)
        {
            var cursor = address + (uint)(index * size) + (uint)lead;

            // Position: either carried here, or an index into an array the
            // transform unit reads separately. Indexed geometry is the common
            // case for anything a game draws more than once, so skipping it
            // means skipping most of a scene.
            var at = Resolve(ref cursor, positionReference, PositionArray);
            var x = ReadComponent(at, positionFormat, fraction, 0);
            var y = ReadComponent(at, positionFormat, fraction, 1);
            var z = components == 3 ? ReadComponent(at, positionFormat, fraction, 2) : 0f;

            if (positionReference == 1)
            {
                cursor += (uint)(components * ComponentSizes[Math.Min(positionFormat, 4)]);
            }

            // Anything between position and colour is measured and stepped
            // over: normals are not lit yet and texture coordinates have
            // nothing to sample.
            SkipAttribute(ref cursor, (_vertexDescriptorLow >> 11) & 3,
                ((attributes >> 9) & 1) != 0 ? 9 : 3, (attributes >> 10) & 7);

            var colour = 0xFFFF_FFFFu;
            if (colourReference != 0)
            {
                var colourAt = Resolve(ref cursor, colourReference, Colour0Array);
                colour = ReadColour(colourAt, colourFormat);
            }

            vertices[index] = new GameCubeRasterizer.Vertex(x, y, z, colour);
        }

        Rasterizer.Draw(kind, vertices);
    }

    /// <summary>The arrays an indexed attribute can be read from.</summary>
    private const int PositionArray = 0;
    private const int Colour0Array = 2;

    /// <summary>
    /// Works out where an attribute's data actually is, and advances the cursor
    /// past whatever the vertex carried for it.
    /// </summary>
    /// <remarks>
    /// A vertex either carries an attribute inline, or names an entry in an
    /// array by index — one byte or two, however large the entry itself is.
    /// Both forms are ordinary and a game uses whichever costs less to send.
    /// </remarks>
    private uint Resolve(ref uint cursor, uint reference, int array)
    {
        switch (reference)
        {
            case 2:
            {
                var index = _memory.ReadByte(cursor);
                cursor += 1;
                return _arrayBase[array] + (index * _arrayStride[array]);
            }

            case 3:
            {
                var index = _memory.ReadUInt16(cursor);
                cursor += 2;
                return _arrayBase[array] + (index * _arrayStride[array]);
            }

            default:
                return cursor;
        }
    }

    /// <summary>
    /// Steps the cursor past an attribute nothing downstream reads yet, which
    /// still has to be measured exactly or every later attribute is misread.
    /// </summary>
    private static void SkipAttribute(ref uint cursor, uint reference, int components, uint format) =>
        cursor += (uint)AttributeSize(reference, format, components);

    /// <summary>
    /// One component of a position, in whichever numeric format the attribute
    /// table names. The integer forms carry an implied binary point.
    /// </summary>
    private float ReadComponent(uint address, uint format, int fraction, int component)
    {
        var scale = 1f / (1 << fraction);
        switch (format)
        {
            case 0:
                return _memory.ReadByte(address + (uint)component) * scale;
            case 1:
                return (sbyte)_memory.ReadByte(address + (uint)component) * scale;
            case 2:
                return _memory.ReadUInt16(address + (uint)(component * 2)) * scale;
            case 3:
                return (short)_memory.ReadUInt16(address + (uint)(component * 2)) * scale;
            default:
                return BitConverter.UInt32BitsToSingle(
                    _memory.ReadUInt32(address + (uint)(component * 4)));
        }
    }

    /// <summary>One colour, in whichever of the six packed forms is in use.</summary>
    private uint ReadColour(uint address, uint format) => format switch
    {
        0 => Expand565(_memory.ReadUInt16(address)),
        1 or 2 => 0xFF00_0000u |
            ((uint)_memory.ReadByte(address) << 16) |
            ((uint)_memory.ReadByte(address + 1) << 8) |
            _memory.ReadByte(address + 2),
        3 => Expand4444(_memory.ReadUInt16(address)),
        _ => 0xFF00_0000u |
            ((uint)_memory.ReadByte(address) << 16) |
            ((uint)_memory.ReadByte(address + 1) << 8) |
            _memory.ReadByte(address + 2)
    };

    private static uint Expand565(ushort packed) =>
        0xFF00_0000u |
        ((uint)(((packed >> 11) & 0x1F) * 255 / 31) << 16) |
        ((uint)(((packed >> 5) & 0x3F) * 255 / 63) << 8) |
        (uint)((packed & 0x1F) * 255 / 31);

    private static uint Expand4444(ushort packed) =>
        0xFF00_0000u |
        ((uint)(((packed >> 12) & 0xF) * 17) << 16) |
        ((uint)(((packed >> 8) & 0xF) * 17) << 8) |
        (uint)(((packed >> 4) & 0xF) * 17);

    // ------------------------------------------------------------ vertex size

    /// <summary>
    /// How many bytes one vertex occupies under the current descriptor and the
    /// given attribute format index. This is what keeps the decoder in step.
    /// </summary>
    /// <remarks>
    /// Public so it can be measured directly. It cannot be inferred from how
    /// far the decoder gets, because anything left over in a command stream is
    /// zero and zero is a valid instruction — the decoder walks through padding
    /// happily and always reaches the end, so a test that measures the distance
    /// travelled measures the padding rather than the vertex.
    /// </remarks>
    public int VertexSize(int format)
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
        // Coordinate zero lives in attribute word A; one to four in B at bits
        // 0, 9, 18 and 27; five to seven in C at bits 5, 14 and 23. Word C
        // starts five bits in because coordinate four's fractional bits were
        // left behind there when its element and format fields ran out of room
        // at the top of B. Only element and format affect size, so the stray
        // fraction is skipped rather than modelled.
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

    // The blitting processor registers that describe a copy out of the embedded
    // framebuffer, and the one that performs it.
    private const uint BpCopySource = 0x49;
    private const uint BpCopySize = 0x4A;
    private const uint BpCopyDestination = 0x4B;
    private const uint BpCopyStride = 0x4D;
    private const uint BpClearAlphaRed = 0x4F;
    private const uint BpClearGreenBlue = 0x50;
    private const uint BpTriggerCopy = 0x52;

    /// <summary>
    /// The end-of-list marker and the two token registers, which are how the
    /// graphics processor reports progress back to a waiting game.
    /// </summary>
    /// <summary>General mode, which carries the culling selection.</summary>
    private const uint BpGeneralMode = 0x00;

    private const uint BpSetDrawDone = 0x45;
    private const uint BpPixelEngineToken = 0x47;
    private const uint BpPixelEngineTokenInterrupt = 0x48;

    private const uint CopyClears = 1u << 11;
    private const uint CopyToExternalFramebuffer = 1u << 14;

    /// <summary>
    /// Performs a copy out of the embedded framebuffer into the external one,
    /// which is the step that makes anything visible at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no embedded framebuffer to copy from yet — nothing rasterises —
    /// so what this writes is the clear colour the game asked for, across the
    /// region it asked to copy. That is not a placeholder for its own sake: a
    /// game clears the framebuffer to a known colour before drawing, and the
    /// copy is what puts it on the television. Getting the addressing, the
    /// dimensions and the colour conversion right against the real registers is
    /// most of the work, and every triangle that ever gets drawn arrives through
    /// this same path.
    /// </para>
    /// <para>
    /// Register numbers verified against documentation rather than recalled:
    /// destination 0x4B, source rectangle 0x49 and 0x4A packed as ten-bit x and
    /// y, clear colour 0x4F and 0x50, and bit 14 of the trigger meaning the copy
    /// is bound for the external framebuffer rather than a texture.
    /// </para>
    /// </remarks>
    private void ExecuteDisplayCopy(uint trigger)
    {
        if ((trigger & CopyToExternalFramebuffer) == 0)
        {
            _trace.WriteOnce(
                GameCubeTraceChannel.Graphics,
                GameCubeTraceLevel.Debug,
                "gx/copy-to-texture",
                "a copy to texture was requested; only copies to the external " +
                "framebuffer are performed");
            return;
        }

        // The destination is stored shifted right by five, as a physical address.
        var destination = _blitting[BpCopyDestination] << 5;
        var size = _blitting[BpCopySize];
        var width = (int)(size & 0x3FF) + 1;
        var height = (int)((size >> 10) & 0x3FF) + 1;

        // The stride is in units of 32 bytes and describes the destination.
        var stride = (int)(_blitting[BpCopyStride] & 0x3FF) * 32;
        if (stride <= 0)
        {
            stride = width * 2;
        }

        var alphaRed = _blitting[BpClearAlphaRed];
        var greenBlue = _blitting[BpClearGreenBlue];
        var red = (byte)(alphaRed & 0xFF);
        var green = (byte)((greenBlue >> 8) & 0xFF);
        var blue = (byte)(greenBlue & 0xFF);

        // Remembered so scan-out reads back exactly what was written. A game
        // chooses its own picture height — 448 lines here, not 480 — and
        // reading past the end shows whatever the memory happened to hold,
        // which in this encoding is a vivid green rather than anything subtle.
        DisplayWidth = width;
        DisplayHeight = height;

        _trace.WriteEvery(
            GameCubeTraceChannel.Graphics,
            GameCubeTraceLevel.Information,
            "gx/display-copy",
            120,
            $"copy to external framebuffer 0x{destination:X8}, {width}x{height}, " +
            $"stride {stride}, clear=({red},{green},{blue})" +
            $"{((trigger & CopyClears) != 0 ? ", clearing" : string.Empty)}");

        CopyEmbeddedFramebuffer(destination, width, height, stride, red, green, blue);

        // Clearing happens as part of the copy, not before it: the image being
        // sent to the television is the one that was there, and the clear is
        // what prepares the next frame.
        if ((trigger & CopyClears) != 0)
        {
            Rasterizer.Clear(red, green, blue);
        }
    }

    /// <summary>
    /// Moves the embedded framebuffer to the external one, converting to the
    /// encoding the video interface reads back.
    /// </summary>
    /// <remarks>
    /// Two pixels share one pair of colour samples, so they are converted
    /// together and their colour averaged — which is what the hardware's copy
    /// filter does, and why a copy is where a picture loses chroma resolution
    /// rather than where it gains it.
    /// </remarks>
    private void CopyEmbeddedFramebuffer(
        uint destination,
        int width,
        int height,
        int stride,
        byte red,
        byte green,
        byte blue)
    {
        var clear = 0xFF00_0000u | ((uint)red << 16) | ((uint)green << 8) | blue;

        for (var y = 0; y < height; y++)
        {
            var line = destination + (uint)(y * stride);
            for (var x = 0; x < width; x += 2)
            {
                var left = Sample(x, y, width, height, clear);
                var right = Sample(x + 1, y, width, height, clear);

                var lumaLeft = Luma(left);
                var lumaRight = Luma(right);
                var averageRed = (((left >> 16) & 0xFF) + ((right >> 16) & 0xFF)) / 2;
                var averageGreen = (((left >> 8) & 0xFF) + ((right >> 8) & 0xFF)) / 2;
                var averageBlue = ((left & 0xFF) + (right & 0xFF)) / 2;

                var at = line + (uint)(x * 2);
                _memory.WriteByte(at, lumaLeft);
                _memory.WriteByte(at + 1, ChromaBlue(averageRed, averageGreen, averageBlue));
                _memory.WriteByte(at + 2, lumaRight);
                _memory.WriteByte(at + 3, ChromaRed(averageRed, averageGreen, averageBlue));
            }
        }
    }

    private uint Sample(int x, int y, int width, int height, uint fallback) =>
        x < GameCubeRasterizer.Width && y < GameCubeRasterizer.Height && x < width && y < height
            ? Rasterizer.Pixel(x, y)
            : fallback;

    private static byte Luma(uint colour) => (byte)Math.Clamp(
        (0.257 * ((colour >> 16) & 0xFF)) + (0.504 * ((colour >> 8) & 0xFF)) +
        (0.098 * (colour & 0xFF)) + 16,
        0,
        255);

    private static byte ChromaBlue(uint red, uint green, uint blue) => (byte)Math.Clamp(
        (-0.148 * red) - (0.291 * green) + (0.439 * blue) + 128, 0, 255);

    private static byte ChromaRed(uint red, uint green, uint blue) => (byte)Math.Clamp(
        (0.439 * red) - (0.368 * green) - (0.071 * blue) + 128, 0, 255);

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
