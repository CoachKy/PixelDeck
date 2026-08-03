using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

/// <summary>
/// Tests for the FIFO decoder, and specifically for vertex size.
/// </summary>
/// <remarks>
/// A primitive command says how many vertices follow and nothing about how
/// large they are: that comes from a descriptor and a format table set by
/// earlier commands. One byte wrong and the decoder lands mid-vertex, reads a
/// coordinate as an opcode, and reports a stream the game never sent. These
/// tests are the encoding of what those tables mean.
/// </remarks>
public class GameCubeCommandProcessorTests
{
    private const uint StreamBase = 0x8010_0000;

    // Vertex descriptor, low word: how each attribute is referenced.
    private const int PositionShift = 9;
    private const int Colour0Shift = 13;

    // Reference kinds.
    private const uint Direct = 1;
    private const uint Indexed16 = 3;

    // Component formats.
    private const uint Float32 = 4;
    private const uint Rgba8888 = 5;

    [Fact]
    public void ADirectPositionVertex_IsThreeFloats()
    {
        // Position, referenced directly, three components of f32.
        var vertexBytes = MeasureOneVertex(
            descriptorLow: Direct << PositionShift,
            descriptorHigh: 0,
            attributeA: 1 | (Float32 << 1));

        Assert.Equal(12, vertexBytes);
    }

    [Fact]
    public void APositionAndColourVertex_AddsThePackedColour()
    {
        var vertexBytes = MeasureOneVertex(
            descriptorLow: (Direct << PositionShift) | (Direct << Colour0Shift),
            descriptorHigh: 0,
            attributeA: 1 | (Float32 << 1) | (Rgba8888 << 14));

        Assert.Equal(12 + 4, vertexBytes);
    }

    [Fact]
    public void AnIndexedAttribute_CostsItsIndexRatherThanItsData()
    {
        // The data lives in an array the transform unit reads separately, so
        // however large the position is, the stream carries two bytes.
        var vertexBytes = MeasureOneVertex(
            descriptorLow: Indexed16 << PositionShift,
            descriptorHigh: 0,
            attributeA: 1 | (Float32 << 1));

        Assert.Equal(2, vertexBytes);
    }

    [Fact]
    public void TextureCoordinateFive_IsReadFromTheOffsetHalfOfTheFormatTable()
    {
        // Coordinates five to seven sit in the third format word, five bits in
        // rather than at its start. Reading them from zero is the mistake this
        // exists to catch: it would report eight bytes too few per vertex.
        var vertexBytes = MeasureOneVertex(
            descriptorLow: Direct << PositionShift,
            descriptorHigh: Direct << (5 * 2),
            attributeA: 1 | (Float32 << 1),
            attributeC: (1u << 5) | (Float32 << 6));

        Assert.Equal(12 + 8, vertexBytes);
    }

    [Fact]
    public void ATransformUnitLoad_ConsumesItsFiveByteHeaderAndItsPayload()
    {
        using var fixture = new ProcessorFixture();

        // Exactly the sequence Super Mario Sunshine sends during GXInit:
        // opcode, then a word holding (count-1) above the register address,
        // then one word of payload. Nine bytes, and a four-byte header leaves
        // the decoder sitting on the payload's last byte.
        var stream = new StreamWriter(fixture.Memory, StreamBase);
        stream.Byte(0x10);
        stream.UInt32(0x0000_1000);
        stream.UInt32(0x0000_003F);
        var afterLoad = stream.Address;
        stream.Byte(0x00);          // a nop the decoder must reach

        var stopped = fixture.Processor.Decode(StreamBase, stream.Address);

        Assert.Equal(afterLoad + 1, stopped);
        Assert.Equal(2, fixture.Processor.CommandsDecoded);
        Assert.DoesNotContain(
            fixture.Trace.CaptureCounters(),
            counter => counter.Key.StartsWith("gx/unknown-opcode", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIncompleteCommand_LeavesTheAddressOnItsOpcode()
    {
        using var fixture = new ProcessorFixture();

        // A triangle header claiming one vertex, with the vertex absent.
        var stream = new StreamWriter(fixture.Memory, StreamBase);
        stream.LoadCpRegister(0x50, Direct << PositionShift);
        stream.LoadCpRegister(0x70, 1 | (Float32 << 1));
        var primitiveAt = stream.Address;
        stream.Byte(0x90);
        stream.UInt16(1);

        var stopped = fixture.Processor.Decode(StreamBase, stream.Address);

        Assert.Equal(primitiveAt, stopped);
        Assert.Equal(0, fixture.Processor.VerticesSeen);
    }

    [Fact]
    public void ADisplayList_IsFollowedAndItsPrimitivesCounted()
    {
        using var fixture = new ProcessorFixture();
        const uint listBase = StreamBase + 0x1000;

        var list = new StreamWriter(fixture.Memory, listBase);
        list.Byte(0x98);        // triangle strip
        list.UInt16(4);
        list.Pad(4 * 12);

        var stream = new StreamWriter(fixture.Memory, StreamBase);
        stream.LoadCpRegister(0x50, Direct << PositionShift);
        stream.LoadCpRegister(0x70, 1 | (Float32 << 1));
        stream.Byte(0x40);
        stream.UInt32(listBase);
        stream.UInt32(list.Address - listBase);

        fixture.Processor.Decode(StreamBase, stream.Address);

        Assert.Equal(4, fixture.Processor.VerticesSeen);
    }

    [Fact]
    public void AnUnrecognisedOpcode_IsReportedRatherThanSkippedQuietly()
    {
        using var fixture = new ProcessorFixture();

        var stream = new StreamWriter(fixture.Memory, StreamBase);
        stream.Byte(0x77);

        fixture.Processor.Decode(StreamBase, stream.Address);

        Assert.Contains(
            fixture.Trace.CaptureCounters(),
            counter => counter.Key == "gx/unknown-opcode/0x77");
    }

    /// <summary>
    /// Sets the descriptor and format table, then decodes a one-vertex triangle
    /// and reports how many bytes that vertex took.
    /// </summary>
    private static int MeasureOneVertex(
        uint descriptorLow,
        uint descriptorHigh,
        uint attributeA,
        uint attributeB = 0,
        uint attributeC = 0)
    {
        using var fixture = new ProcessorFixture();

        var stream = new StreamWriter(fixture.Memory, StreamBase);
        stream.LoadCpRegister(0x50, descriptorLow);
        stream.LoadCpRegister(0x60, descriptorHigh);
        stream.LoadCpRegister(0x70, attributeA);
        stream.LoadCpRegister(0x80, attributeB);
        stream.LoadCpRegister(0x90, attributeC);

        var primitiveAt = stream.Address;
        stream.Byte(0x90);      // triangles, format zero
        stream.UInt16(1);
        stream.Pad(64);         // room for whatever one vertex turns out to be

        var stopped = fixture.Processor.Decode(StreamBase, stream.Address);

        // The decoder stops when the run ends, so what it consumed past the
        // primitive header is exactly one vertex.
        Assert.Equal(1, fixture.Processor.VerticesSeen);
        return (int)(stopped - primitiveAt) - 3;
    }

    private sealed class ProcessorFixture : IDisposable
    {
        public ProcessorFixture()
        {
            Trace = new GameCubeTraceLog(
                new GameCubeTraceSettings(GameCubeTraceLevel.Warning, GameCubeTraceChannel.All));
            Memory = new GameCubeMemory(Trace);
            Processor = new GameCubeCommandProcessor(Memory, Trace);
        }

        public GameCubeTraceLog Trace { get; }

        public GameCubeMemory Memory { get; }

        public GameCubeCommandProcessor Processor { get; }

        public void Dispose() => Trace.Dispose();
    }

    /// <summary>Builds a command stream in memory, tracking where it ends.</summary>
    private sealed class StreamWriter(GameCubeMemory memory, uint address)
    {
        public uint Address { get; private set; } = address;

        public void Byte(byte value)
        {
            memory.WriteByte(Address, value);
            Address++;
        }

        public void UInt16(ushort value)
        {
            memory.WriteUInt16(Address, value);
            Address += 2;
        }

        public void UInt32(uint value)
        {
            memory.WriteUInt32(Address, value);
            Address += 4;
        }

        public void Pad(int bytes) => Address += (uint)bytes;

        public void LoadCpRegister(byte register, uint value)
        {
            Byte(0x08);
            Byte(register);
            UInt32(value);
        }
    }
}
