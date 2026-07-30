using System.Buffers.Binary;
using System.Numerics;

namespace PixelDeck.Emulation.N64;

public sealed partial class Fast3dRenderer : IN64GraphicsBackend
{
    private const uint AlphaCompareMask = 0x3;
    private const uint ImageRead = 0x40;
    private const uint ForceBlend = 0x4000;
    private const int MaximumCommandsPerTask = 250_000;
    private const int MaximumDisplayListDepth = 32;

    private readonly N64Memory _memory;
    private readonly uint[] _segments = new uint[16];
    private readonly Dictionary<byte, long> _unsupportedCommandCounts = new();
    // Fast3D addresses 16 vertices; F3DEX2 raises the buffer to 32.
    private readonly Fast3dVertex[] _vertices = new Fast3dVertex[32];
    private N64Microcode _microcode = N64Microcode.Fast3d;
    private readonly Fast3dTile[] _tiles = new Fast3dTile[8];
    private readonly LoadedTexture[] _loadedTextures = new LoadedTexture[512];
    private readonly byte[] _textureMemory = new byte[4 * 1024];
    private readonly Stack<Matrix4x4> _modelViewStack = new();
    private uint _colorImageAddress;
    private int _colorImageWidth = 320;
    private int _colorImageSize;
    private uint _depthImageAddress = uint.MaxValue;
    private float[] _depthBuffer = Array.Empty<float>();
    private int _depthBufferWidth;
    private int _depthBufferHeight;
    private uint _fillColor;
    private readonly Vector3[] _lightColors = new Vector3[8];
    private readonly Vector3[] _lightDirections = new Vector3[8];
    private int _lightCount;
    private bool _lightsLoaded;
    private int _scissorLeft;
    private int _scissorTop;
    private int _scissorRight = 320;
    private int _scissorBottom = 240;
    private Matrix4x4 _projection = Matrix4x4.Identity;
    private Vector4 _viewportScale = new(160, 120, 511, 0);
    private Vector4 _viewportTranslate = new(160, 120, 0, 0);
    private uint _geometryMode;
    private bool _textureEnabled;
    private int _textureTile;
    private int _textureMaximumMipLevel;
    private float _textureScaleS = 1;
    private float _textureScaleT = 1;
    private uint _textureImageAddress;
    private int _textureImageFormat;
    private int _textureImageSize;
    private int _textureImageWidth = 1;
    private bool _combinerUsesTexture;
    private bool _combinerConfigured;
    private Vector4 _primitiveColor = Vector4.One;
    private Vector4 _environmentColor = Vector4.One;
    private Vector4 _fogColor;
    private Vector4 _blendColor;
    private CombinerCycle _combinerCycle0;
    private CombinerCycle _combinerCycle1;
    private uint _otherModeLow;
    private uint _otherModeHigh;
    private uint _keyGreenBlueWord0;
    private uint _keyGreenBlueWord1;
    private uint _keyRedWord1;
    private uint _convertWord0;
    private uint _convertWord1;
    private ushort _primitiveDepth;
    private ushort _primitiveDeltaDepth;

    public Fast3dRenderer(N64Memory memory)
    {
        _memory = memory;
    }

    public long CommandsProcessed { get; private set; }

    public long DisplayListsProcessed { get; private set; }

    public long FillRectanglesDrawn { get; private set; }

    public long TrianglesDrawn { get; private set; }

    public long LinesDrawn { get; private set; }

    public long VerticesTransformed { get; private set; }

    public long TexturedPixelsDrawn { get; private set; }

    public string Name => "Pixel64 Fast3D software renderer";

    /// <summary>
    /// Allows the host to parse a graphics task without writing its pixels.
    /// Keeping command/state processing active is important: a skipped frame
    /// must not corrupt the next rendered display list.
    /// </summary>
    public bool RasterizationEnabled { get; set; } = true;

    public long TextureRectanglesDrawn { get; private set; }

    public long DepthPixelsRejected { get; private set; }

    public long AlphaPixelsRejected { get; private set; }

    public long FramebufferPixelsBlended { get; private set; }

    public long TriviallyClippedTriangles { get; private set; }

    public float MaximumTriangleWidth { get; private set; }

    public float MaximumTriangleHeight { get; private set; }

    public uint ColorImageAddress => _colorImageAddress;

    public int ColorImageWidth => _colorImageWidth;

    public int ColorImageSize => _colorImageSize;

    internal uint OtherModeLow => _otherModeLow;

    internal uint OtherModeHigh => _otherModeHigh;

    public N64RdpStateSnapshot RdpState => new(
        _otherModeHigh,
        _otherModeLow,
        CycleType,
        _primitiveColor,
        _environmentColor,
        _fogColor,
        _blendColor,
        _combinerConfigured,
        _combinerUsesTexture,
        _keyGreenBlueWord0,
        _keyGreenBlueWord1,
        _keyRedWord1,
        _convertWord0,
        _convertWord1,
        _primitiveDepth,
        _primitiveDeltaDepth,
        AlphaPixelsRejected,
        FramebufferPixelsBlended);

    /// <summary>RDP cycle type from other-mode-high bits 20-21 (2 = COPY).</summary>
    private uint CycleType => (_otherModeHigh >> 20) & 3;

    public long UnsupportedCommands { get; private set; }

    public IReadOnlyDictionary<byte, long> UnsupportedCommandCounts => _unsupportedCommandCounts;

    public uint? FirstUnsupportedCommandAddress { get; private set; }

    public string? FirstUnsupportedCommandContext { get; private set; }

    public uint? FirstUnsupportedListHeaderAddress { get; private set; }

    public void Execute(N64RspTask task)
    {
        Array.Clear(_segments);
        Array.Clear(_vertices);
        _modelViewStack.Clear();
        _modelViewStack.Push(Matrix4x4.Identity);
        _projection = Matrix4x4.Identity;
        _viewportScale = new Vector4(160, 120, 511, 0);
        _viewportTranslate = new Vector4(160, 120, 0, 0);
        _geometryMode = 0;
        _textureEnabled = false;
        _textureTile = 0;
        _textureMaximumMipLevel = 0;
        _textureScaleS = 1;
        _textureScaleT = 1;
        _textureImageAddress = 0;
        _textureImageFormat = 0;
        _textureImageSize = 0;
        _textureImageWidth = 1;
        _primitiveColor = Vector4.One;
        MicrocodeBanner = ReadMicrocodeBanner(task);
        MicrocodeCrc32 = CalculateMicrocodeCrc32(task);
        _microcode = ClassifyMicrocode(MicrocodeBanner, MicrocodeCrc32);
        DetectedMicrocode = _microcode;
        var remainingBudget = MaximumCommandsPerTask;
        if (_microcode == N64Microcode.F5Rogue)
        {
            ResetF5RogueState();
            ExecuteF5RogueDisplayList(
                ResolveAddress(task.DataPointer),
                task.DataSize == 0 ? null : task.DataSize / 8,
                depth: 0,
                ref remainingBudget);
            return;
        }

        ExecuteDisplayList(
            ResolveAddress(task.DataPointer),
            task.DataSize == 0 ? null : task.DataSize / 8,
            depth: 0,
            ref remainingBudget);
    }

    private void ExecuteDisplayList(
        uint address,
        uint? commandLimit,
        int depth,
        ref int remainingBudget)
    {
        if (depth >= MaximumDisplayListDepth || remainingBudget <= 0)
        {
            UnsupportedCommands++;
            return;
        }

        DisplayListsProcessed++;
        var commandsInList = 0u;
        while (remainingBudget-- > 0 &&
               (!commandLimit.HasValue || commandsInList < commandLimit.Value))
        {
            var word0 = _memory.ReadUInt32(address);
            var word1 = _memory.ReadUInt32(address + 4);
            address += 8;
            commandsInList++;
            CommandsProcessed++;

            var opcode = (byte)(word0 >> 24);
            _opcodeHistogram[opcode]++;
            switch (opcode)
            {
                case 0x01: // F3D G_MTX / F3DEX2 G_VTX
                    if (_microcode == N64Microcode.F3dex2)
                    {
                        LoadVerticesF3dex2(word0, word1);
                    }
                    else
                    {
                        LoadMatrix((int)((word0 >> 16) & 0xFF), word1);
                    }

                    break;
                case 0x02 when _microcode == N64Microcode.F3dex2: // G_MODIFYVTX
                    ModifyVertex(word0, word1);
                    break;
                case 0x03: // F3D G_MOVEMEM / F3DEX2 G_CULLDL
                    if (_microcode != N64Microcode.F3dex2)
                    {
                        MoveMemory(word0, word1);
                    }
                    else if (ShouldCullDisplayList(word0, word1))
                    {
                        return;
                    }

                    break;
                case 0x04: // F3D G_VTX / F3DEX2 G_BRANCH_Z
                    if (_microcode == N64Microcode.F3dex)
                    {
                        LoadVerticesF3dex(word0, word1);
                    }
                    else if (_microcode != N64Microcode.F3dex2)
                    {
                        LoadVertices(word0, word1);
                    }

                    // G_BRANCH_Z selects a level of detail by depth. Falling
                    // through keeps the already-loaded list.
                    break;
                case 0x05: // F3DEX2 G_TRI1
                    if (_microcode == N64Microcode.F3dex2)
                    {
                        DrawTriangleF3dex2(word0);
                    }

                    break;
                case 0x07: // F3DEX2 G_QUAD
                    if (_microcode == N64Microcode.F3dex2)
                    {
                        DrawTriangleF3dex2(word0);
                        DrawTriangleF3dex2(word1);
                    }

                    break;
                case 0xD7: // F3DEX2 G_TEXTURE
                    SetTexture(word0, word1);
                    break;
                case 0xD8: // F3DEX2 G_POPMTX
                    if (_modelViewStack.Count > 1)
                    {
                        _modelViewStack.Pop();
                    }

                    break;
                case 0xD9: // F3DEX2 G_GEOMETRYMODE
                    _geometryMode = (_geometryMode & (word0 & 0x00FFFFFF)) | word1;
                    break;
                case 0xDA: // F3DEX2 G_MTX
                    LoadMatrix(ConvertF3dex2MatrixParameters(word0), word1);
                    break;
                case 0xDB: // F3DEX2 G_MOVEWORD
                    MoveWordF3dex2(word0, word1);
                    break;
                case 0xDC: // F3DEX2 G_MOVEMEM
                    MoveMemoryF3dex2(word0, word1);
                    break;
                case 0x06 when _microcode == N64Microcode.F3dex2: // F3DEX2 G_TRI2
                    DrawTriangleF3dex2(word0);
                    DrawTriangleF3dex2(word1);
                    break;
                case 0x06: // F3D G_DL
                {
                    var target = ResolveAddress(word1);
                    var noPush = ((word0 >> 16) & 0xFF) != 0;
                    if (noPush)
                    {
                        address = target;
                        commandLimit = null;
                        commandsInList = 0;
                    }
                    else
                    {
                        ExecuteDisplayList(target, null, depth + 1, ref remainingBudget);
                    }

                    break;
                }
                case 0xBC: // F3D G_MOVEWORD
                    MoveWord(word0, word1);
                    break;
                case 0xB8: // F3D G_ENDDL
                case 0xDF: // F3DEX2 G_ENDDL
                    return;
                case 0xBD: // F3D G_POPMTX
                    if (_modelViewStack.Count > 1)
                    {
                        _modelViewStack.Pop();
                    }

                    break;
                case 0xBE: // F3D/F3DEX G_CULLDL
                    if (ShouldCullDisplayList(word0, word1))
                    {
                        return;
                    }

                    break;
                case 0xBF: // F3D G_TRI1
                    // Fast3D stores indices multiplied by 10; F3DEX by 2.
                    if (_microcode == N64Microcode.F3dex)
                    {
                        DrawTriangleF3dex2(word1);
                    }
                    else
                    {
                        DrawTriangle(word1);
                    }

                    break;
                case 0xB1 when _microcode == N64Microcode.F3dex: // F3DEX G_TRI2
                    DrawTriangleF3dex2(word0);
                    DrawTriangleF3dex2(word1);
                    break;
                case 0xB1: // F3D G_TRI4: four triangles as packed nibble indices
                    for (var triangle = 0; triangle < 4; triangle++)
                    {
                        var first = (int)((word0 >> (triangle * 4)) & 0xF);
                        var second = (int)((word1 >> (triangle * 8)) & 0xF);
                        var third = (int)((word1 >> ((triangle * 8) + 4)) & 0xF);
                        if (first == second && second == third)
                        {
                            continue; // unused slot padding
                        }

                        DrawTriangleIndices(first, second, third);
                    }

                    break;
                case 0xB5: // F3D/F3DEX G_LINE3D
                    RecordOmittedHlePrimitives(1);
                    if (_microcode == N64Microcode.F3dex)
                    {
                        DrawLineF3dex(word0);
                    }
                    else
                    {
                        DrawLine(word1);
                    }

                    break;
                case 0xB6: // F3D G_CLEARGEOMETRYMODE
                    _geometryMode &= ~word1;
                    break;
                case 0xB7: // F3D G_SETGEOMETRYMODE
                    _geometryMode |= word1;
                    break;
                case 0xBB: // F3D G_TEXTURE
                    SetTexture(word0, word1);
                    break;
                case 0xDE: // F3DEX2 G_DL
                {
                    var target = ResolveAddress(word1);
                    var noPush = ((word0 >> 16) & 0xFF) != 0;
                    if (noPush)
                    {
                        address = target;
                        commandLimit = null;
                        commandsInList = 0;
                    }
                    else
                    {
                        ExecuteDisplayList(target, null, depth + 1, ref remainingBudget);
                    }

                    break;
                }
                case 0xF6: // G_FILLRECT
                case 0xF7: // G_SETFILLCOLOR
                case 0xF2: // G_SETTILESIZE
                case 0xF3: // G_LOADBLOCK
                case 0xF5: // G_SETTILE
                case 0xFD: // G_SETTIMG
                case 0xFC: // G_SETCOMBINE
                case 0xFE: // G_SETZIMG
                case 0xFF: // G_SETCIMG
                case 0xF0: // G_LOADTLUT
                case 0xF4: // G_LOADTILE
                case 0xEF: // G_RDPSETOTHERMODE
                case 0xFB: // G_SETENVCOLOR
                case 0xE7: // G_RDPPIPESYNC
                case 0xE6: // G_RDPLOADSYNC
                case 0xE8: // G_RDPTILESYNC
                case 0xE9: // G_RDPFULLSYNC
                case 0xEA: // G_SETKEYGB
                case 0xEB: // G_SETKEYR
                case 0xEC: // G_SETCONVERT
                case 0xEE: // G_SETPRIMDEPTH
                case 0xF8: // G_SETFOGCOLOR
                case 0xF9: // G_SETBLENDCOLOR
                case 0xFA: // G_SETPRIMCOLOR
                    CaptureAndExecuteRdpCommand(word0, word1);
                    break;
                case 0xC0: // G_NOOP / G_NOOP_TAG
                    break;
                case 0xB9: // G_SETOTHERMODE_L
                    SetOtherModeLow(word0, word1);
                    CaptureCanonicalOtherMode();
                    break;
                case 0xBA: // G_SETOTHERMODE_H
                    SetOtherModeHigh(word0, word1);
                    CaptureCanonicalOtherMode();
                    break;
                case 0xE2: // F3DEX2 G_SETOTHERMODE_L
                    SetOtherModeLow(ConvertF3dex2ModeSelector(word0), word1);
                    CaptureCanonicalOtherMode();
                    break;
                case 0xE3: // F3DEX2 G_SETOTHERMODE_H
                    SetOtherModeHigh(ConvertF3dex2ModeSelector(word0), word1);
                    CaptureCanonicalOtherMode();
                    break;
                case 0xED: // G_SETSCISSOR — upper left in word0, lower right in word1
                    CaptureAndExecuteRdpCommand(word0, word1);
                    break;
                case 0x00:
                case 0xB4: // G_RDPHALF_1
                    break;
                case 0xE4: // G_TEXRECT
                case 0xE5: // G_TEXRECTFLIP
                {
                    if (remainingBudget < 2)
                    {
                        return;
                    }

                    var halfOne = _memory.ReadUInt32(address + 4);
                    var halfTwo = _memory.ReadUInt32(address + 12);
                    address += 16;
                    commandsInList += 2;
                    remainingBudget -= 2;
                    CommandsProcessed += 2;
                    CaptureAndExecuteRdpCommand(word0, word1, halfOne, halfTwo);
                    break;
                }
                default:
                    FirstUnsupportedCommandAddress ??= address - 8;
                    UnsupportedCommands++;
                    _unsupportedCommandCounts[opcode] =
                        _unsupportedCommandCounts.GetValueOrDefault(opcode) + 1;
                    break;
            }
        }
    }

    /// <summary>
    /// Identifies the graphics microcode from the version banner every
    /// libultra ucode carries in its data segment, for example
    /// "RSP Gfx ucode F3DEX.NoN fifo 2.08". The trailing version number is
    /// what separates F3DEX2 from the original F3DEX — both spell "F3DEX" —
    /// and F3DZEX (Ocarina of Time) is an F3DEX2 derivative.
    /// </summary>
    internal N64Microcode DetectedMicrocode { get; private set; } = N64Microcode.Fast3d;

    /// <summary>
    /// Human-readable microcode family detected for the most recent graphics
    /// task. Compatibility tooling can record this without depending on the
    /// renderer's internal dispatch enum.
    /// </summary>
    public string DetectedMicrocodeName => DetectedMicrocode.ToString();

    internal string? MicrocodeBanner { get; private set; }

    /// <summary>
    /// Strict CRC-32 of the 4 KiB graphics microcode text image. Some custom
    /// microcodes contain no libultra version banner, so compatibility tooling
    /// needs the same stable identity that graphics plugins use.
    /// </summary>
    public uint MicrocodeCrc32 { get; private set; }

    internal static N64Microcode ClassifyMicrocode(string? banner, uint crc32)
    {
        // Factor 5's Rogue Squadron microcode has no ordinary Fast3D banner
        // and changes both opcode meanings and command lengths.
        if (crc32 == 0xDA51CCDB)
        {
            return N64Microcode.F5Rogue;
        }

        if (banner is null)
        {
            return N64Microcode.Fast3d;
        }

        if (banner.Contains("F3DZEX", StringComparison.Ordinal) ||
            (banner.Contains("F3DEX", StringComparison.Ordinal) &&
             banner.Contains(" 2.", StringComparison.Ordinal)))
        {
            return N64Microcode.F3dex2;
        }

        return banner.Contains("F3DEX", StringComparison.Ordinal)
            ? N64Microcode.F3dex
            : N64Microcode.Fast3d;
    }

    private uint CalculateMicrocodeCrc32(N64RspTask task)
    {
        const int microcodeTextLength = 4096;
        var address = task.MicrocodePointer & 0x7FFFFF;
        if (address + microcodeTextLength > N64Memory.RdramSize)
        {
            return 0;
        }

        // Published graphics-microcode CRCs are computed from the traditional
        // N64 plugin memory layout, where bytes inside each 32-bit RDRAM word
        // are host-order reversed. Pixel64 keeps canonical big-endian RDRAM,
        // so feed each word to the CRC in that historical byte order.
        return ComputeStrictWordSwappedCrc32(
            _memory.Rdram.AsSpan((int)address, microcodeTextLength));
    }

    internal static uint ComputeStrictCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ 0xEDB88320
                    : crc >> 1;
            }
        }

        return ~crc;
    }

    internal static uint ComputeStrictWordSwappedCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        var offset = 0;
        for (; offset + 4 <= data.Length; offset += 4)
        {
            for (var byteInWord = 3; byteInWord >= 0; byteInWord--)
            {
                crc = AccumulateCrc32(crc, data[offset + byteInWord]);
            }
        }

        for (; offset < data.Length; offset++)
        {
            crc = AccumulateCrc32(crc, data[offset]);
        }

        return ~crc;
    }

    private static uint AccumulateCrc32(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0
                ? (crc >> 1) ^ 0xEDB88320
                : crc >> 1;
        }

        return crc;
    }

    private string? ReadMicrocodeBanner(N64RspTask task)
    {
        var address = task.MicrocodeDataPointer & 0x7FFFFF;
        var length = (int)Math.Min(task.MicrocodeDataSize, 2048);
        if (length <= 0 || address + length > N64Memory.RdramSize)
        {
            return null;
        }

        var data = _memory.Rdram.AsSpan((int)address, length);
        ReadOnlySpan<byte> marker = "RSP Gfx ucode"u8;
        var start = data.IndexOf(marker);
        if (start < 0)
        {
            return null;
        }

        var text = data[start..];
        var end = 0;
        while (end < text.Length && end < 96 && text[end] is >= 0x20 and <= 0x7E)
        {
            end++;
        }

        return System.Text.Encoding.ASCII.GetString(text[..end]);
    }

    private void SetTexture(uint word0, uint word1)
    {
        _textureEnabled = (word0 & 0xFF) != 0;
        _textureTile = (int)((word0 >> 8) & 7);
        _textureMaximumMipLevel = (int)((word0 >> 11) & 7);
        _textureScaleS = ((word1 >> 16) & 0xFFFF) / 65536f;
        _textureScaleT = (word1 & 0xFFFF) / 65536f;
    }

    private void SetOtherModeLow(uint word0, uint word1)
    {
        var shift = (int)((word0 >> 8) & 0xFF);
        var length = (int)(word0 & 0xFF);
        if (shift is < 0 or > 31 || length <= 0)
        {
            return;
        }

        length = Math.Min(length, 32 - shift);
        var valueMask = length == 32
            ? uint.MaxValue
            : ((1u << length) - 1u) << shift;
        _otherModeLow = (_otherModeLow & ~valueMask) | (word1 & valueMask);
    }

    private void SetOtherModeHigh(uint word0, uint word1)
    {
        var shift = (int)((word0 >> 8) & 0xFF);
        var length = (int)(word0 & 0xFF);
        if (shift is < 0 or > 31 || length <= 0)
        {
            return;
        }

        length = Math.Min(length, 32 - shift);
        var valueMask = length == 32
            ? uint.MaxValue
            : ((1u << length) - 1u) << shift;
        _otherModeHigh = (_otherModeHigh & ~valueMask) | (word1 & valueMask);
    }

    private void LoadTextureLookupTable(uint word0, uint word1)
    {
        // Palette entries land in upper TMEM at the tile's base. Hardware
        // quadricates each 16-bit entry across a 64-bit word; storing the
        // same replication keeps CI sampling arithmetic identical.
        var tileIndex = (int)((word1 >> 24) & 7);
        var firstEntry = (int)((word0 >> 14) & 0x3FF);
        var lastEntry = (int)((word1 >> 14) & 0x3FF);
        var count = Math.Clamp(lastEntry - firstEntry + 1, 0, 256);
        var destination = _tiles[tileIndex].Tmem * 8;
        for (var entry = 0; entry < count; entry++)
        {
            var source = _textureImageAddress + (uint)((firstEntry + entry) * 2);
            if (source + 2 > N64Memory.RdramSize)
            {
                break;
            }

            var value = (ushort)((_memory.Rdram[source] << 8) | _memory.Rdram[source + 1]);
            for (var replica = 0; replica < 4; replica++)
            {
                var offset = destination + (entry * 8) + (replica * 2);
                if (offset + 2 <= _textureMemory.Length)
                {
                    _textureMemory[offset] = (byte)(value >> 8);
                    _textureMemory[offset + 1] = (byte)value;
                }
            }
        }
    }

    private void SetTextureImage(uint word0, uint word1)
    {
        _textureImageFormat = (int)((word0 >> 21) & 7);
        _textureImageSize = (int)((word0 >> 19) & 3);
        _textureImageWidth = (int)(word0 & 0xFFF) + 1;
        _textureImageAddress = ResolveAddress(word1);
    }

    private void SetTile(uint word0, uint word1)
    {
        var tileIndex = (int)((word1 >> 24) & 7);
        _tiles[tileIndex] = _tiles[tileIndex] with
        {
            Format = (int)((word0 >> 21) & 7),
            Size = (int)((word0 >> 19) & 3),
            Line = (int)((word0 >> 9) & 0x1FF),
            Tmem = (int)(word0 & 0x1FF),
            Palette = (int)((word1 >> 20) & 0xF),
            ClampT = ((word1 >> 19) & 1) != 0,
            MirrorT = ((word1 >> 18) & 1) != 0,
            MaskT = (int)((word1 >> 14) & 0xF),
            ShiftT = (int)((word1 >> 10) & 0xF),
            ClampS = ((word1 >> 9) & 1) != 0,
            MirrorS = ((word1 >> 8) & 1) != 0,
            MaskS = (int)((word1 >> 4) & 0xF),
            ShiftS = (int)(word1 & 0xF)
        };
    }

    private void SetTileSize(uint word0, uint word1)
    {
        var tileIndex = (int)((word1 >> 24) & 7);
        _tiles[tileIndex] = _tiles[tileIndex] with
        {
            UpperLeftS = (int)((word0 >> 12) & 0xFFF),
            UpperLeftT = (int)(word0 & 0xFFF),
            LowerRightS = (int)((word1 >> 12) & 0xFFF),
            LowerRightT = (int)(word1 & 0xFFF)
        };
    }

    /// <summary>
    /// F3DEX2 stores the other-mode shift and length differently: the word
    /// holds (32 - shift - length) and (length - 1). Repack them into the
    /// Fast3D shift/length pair the shared setters expect.
    /// </summary>
    private static uint ConvertF3dex2ModeSelector(uint word0)
    {
        var length = (int)(word0 & 0xFF) + 1;
        var shift = 32 - (int)((word0 >> 8) & 0xFF) - length;
        return (uint)((Math.Clamp(shift, 0, 31) << 8) | Math.Clamp(length, 0, 32));
    }

    /// <summary>
    /// Copies a rectangular region of the current texture image into TMEM.
    /// LoadTile pads every row to the descriptor's 64-bit Line stride and
    /// swaps the two 32-bit halves of each word on odd rows.
    /// </summary>
    private void LoadTextureTile(uint word0, uint word1)
    {
        var tileIndex = (int)((word1 >> 24) & 7);
        var tile = _tiles[tileIndex];
        var upperLeftS = (int)((word0 >> 12) & 0xFFF) >> 2;
        var upperLeftT = (int)(word0 & 0xFFF) >> 2;
        var lowerRightS = (int)((word1 >> 12) & 0xFFF) >> 2;
        var lowerRightT = (int)(word1 & 0xFFF) >> 2;
        var width = lowerRightS - upperLeftS + 1;
        var height = lowerRightT - upperLeftT + 1;
        if (width <= 0 || height <= 0 || _textureImageWidth <= 0)
        {
            return;
        }

        var bitsPerTexel = BitsPerTexel(_textureImageSize);
        var rowBits = width * bitsPerTexel;
        var rowStrideBits = tile.Line > 0
            ? tile.Line * 64
            : ((rowBits + 63) / 64) * 64;
        for (var row = 0; row < height; row++)
        {
            var sourceTexel = ((upperLeftT + row) * _textureImageWidth) + upperLeftS;
            CopyRdramRowToTmem(
                _textureImageAddress,
                sourceTexel * bitsPerTexel,
                (tile.Tmem * 64) + (row * rowStrideBits),
                rowBits,
                ((upperLeftT + row) & 1) != 0);
        }

        _loadedTextures[tile.Tmem] = new LoadedTexture(
            _textureImageAddress,
            _textureImageFormat,
            _textureImageSize,
            width * height,
            tile.Tmem,
            rowStrideBits * height);
    }

    private void LoadTextureBlock(uint word0, uint word1)
    {
        var tileIndex = (int)((word1 >> 24) & 7);
        var tmem = _tiles[tileIndex].Tmem;
        var upperLeftS = (int)((word0 >> 12) & 0xFFF) >> 2;
        var upperLeftT = (int)(word0 & 0xFFF) >> 2;
        var texels = (int)((word1 >> 12) & 0xFFF) + 1;
        var bitsPerTexel = BitsPerTexel(_textureImageSize);
        var sourceTexel = (upperLeftT * _textureImageWidth) + upperLeftS;
        var sourceBitOffset = sourceTexel * bitsPerTexel;
        var destinationBitOffset = tmem * 64;
        var bitCount = texels * bitsPerTexel;
        var dxt = (int)(word1 & 0xFFF);
        CopyRdramBlockToTmem(
            _textureImageAddress,
            sourceBitOffset,
            destinationBitOffset,
            bitCount,
            upperLeftT,
            dxt);
        _loadedTextures[tmem] = new LoadedTexture(
            _textureImageAddress,
            _textureImageFormat,
            _textureImageSize,
            texels,
            tmem,
            bitCount);
    }

    /// <summary>
    /// LoadTile transfers one source row at a time. TMEM interleaves its four
    /// byte banks by exchanging the two 32-bit halves of every 64-bit word on
    /// odd texture rows.
    /// </summary>
    private void CopyRdramRowToTmem(
        uint sourceAddress,
        int sourceBitOffset,
        int destinationBitOffset,
        int bitCount,
        bool swapWordHalves)
    {
        if (((sourceBitOffset | destinationBitOffset | bitCount) & 7) == 0)
        {
            var sourceByteOffset = sourceBitOffset >> 3;
            var destinationByteOffset = destinationBitOffset >> 3;
            var byteCount = bitCount >> 3;
            for (var index = 0; index < byteCount; index++)
            {
                var destination = destinationByteOffset + index;
                if (swapWordHalves)
                {
                    destination ^= 4;
                }

                _textureMemory[destination & 0xFFF] =
                    _memory.ReadByte(sourceAddress + (uint)(sourceByteOffset + index));
            }

            return;
        }

        for (var bit = 0; bit < bitCount; bit++)
        {
            var logicalDestination = destinationBitOffset + bit;
            var physicalDestination = swapWordHalves
                ? logicalDestination ^ 32
                : logicalDestination;
            CopyRdramBitToTmem(sourceAddress, sourceBitOffset + bit, physicalDestination);
        }
    }

    /// <summary>
    /// LoadBlock transfers one contiguous span. Its 1.11 DXT accumulator
    /// derives the source row for each 64-bit word so odd rows receive the
    /// TMEM word-half swap required by the texture sampler.
    /// </summary>
    private void CopyRdramBlockToTmem(
        uint sourceAddress,
        int sourceBitOffset,
        int destinationBitOffset,
        int bitCount,
        int initialRow,
        int dxt)
    {
        if (((sourceBitOffset | destinationBitOffset | bitCount) & 7) == 0)
        {
            var sourceByteOffset = sourceBitOffset >> 3;
            var destinationByteOffset = destinationBitOffset >> 3;
            var byteCount = bitCount >> 3;
            for (var index = 0; index < byteCount; index++)
            {
                var word = index >> 3;
                var row = initialRow + ((word * dxt) >> 11);
                var destination = destinationByteOffset + index;
                if ((row & 1) != 0)
                {
                    destination ^= 4;
                }

                _textureMemory[destination & 0xFFF] =
                    _memory.ReadByte(sourceAddress + (uint)(sourceByteOffset + index));
            }

            return;
        }

        for (var bit = 0; bit < bitCount; bit++)
        {
            var word = bit >> 6;
            var row = initialRow + ((word * dxt) >> 11);
            var logicalDestination = destinationBitOffset + bit;
            var physicalDestination = (row & 1) != 0
                ? logicalDestination ^ 32
                : logicalDestination;
            CopyRdramBitToTmem(sourceAddress, sourceBitOffset + bit, physicalDestination);
        }
    }

    private void CopyRdramBitToTmem(
        uint sourceAddress,
        int sourceBit,
        int destinationBit)
    {
        var sourceByte = _memory.ReadByte(sourceAddress + (uint)(sourceBit >> 3));
        var sourceMask = 1 << (7 - (sourceBit & 7));
        var destinationByte = (destinationBit >> 3) & 0xFFF;
        var destinationMask = (byte)(1 << (7 - (destinationBit & 7)));
        if ((sourceByte & sourceMask) != 0)
        {
            _textureMemory[destinationByte] |= destinationMask;
        }
        else
        {
            _textureMemory[destinationByte] &= (byte)~destinationMask;
        }
    }

    private void CopyRdramBitsToTmem(
        uint sourceAddress,
        int sourceBitOffset,
        int destinationBitOffset,
        int bitCount)
    {
        if ((sourceBitOffset & 7) == 0 &&
            (destinationBitOffset & 7) == 0 &&
            (bitCount & 7) == 0)
        {
            var sourceByteOffset = sourceBitOffset >> 3;
            var destinationByteOffset = destinationBitOffset >> 3;
            for (var index = 0; index < (bitCount >> 3); index++)
            {
                _textureMemory[(destinationByteOffset + index) & 0xFFF] =
                    _memory.ReadByte(sourceAddress + (uint)(sourceByteOffset + index));
            }

            return;
        }

        for (var bit = 0; bit < bitCount; bit++)
        {
            var source = sourceBitOffset + bit;
            var sourceByte = _memory.ReadByte(sourceAddress + (uint)(source >> 3));
            var sourceMask = 1 << (7 - (source & 7));
            var destination = destinationBitOffset + bit;
            var destinationByte = (destination >> 3) & 0xFFF;
            var destinationMask = (byte)(1 << (7 - (destination & 7)));
            if ((sourceByte & sourceMask) != 0)
            {
                _textureMemory[destinationByte] |= destinationMask;
            }
            else
            {
                _textureMemory[destinationByte] &= (byte)~destinationMask;
            }
        }
    }

    private void MoveWord(uint word0, uint word1)
    {
        const int numberOfLightsIndex = 0x02;
        const int segmentMoveWordIndex = 0x06;
        var index = (int)(word0 & 0xFF);
        if (index == numberOfLightsIndex)
        {
            // Encoded as NUMLIGHTS_n = 0x80000000 + (n + 1) * 32.
            _lightCount = Math.Clamp((int)((word1 - 0x80000000) / 32) - 1, 0, 7);
            return;
        }

        if (index != segmentMoveWordIndex)
        {
            return;
        }

        var offset = (word0 >> 8) & 0xFFFF;
        var segment = (int)(offset / 4);
        if (segment < _segments.Length)
        {
            _segments[segment] = word1 & 0x00FFFFFF;
        }
    }

    private void MoveMemory(uint word0, uint word1)
    {
        const int viewportIndex = 0x80;
        const int firstLightIndex = 0x86;
        const int lastLightIndex = 0x94;
        var index = (int)((word0 >> 16) & 0xFF);
        if (index is >= firstLightIndex and <= lastLightIndex && (index & 1) == 0)
        {
            // Light_t: color bytes 0-2 (repeated at 4-6), signed direction
            // bytes 8-10. The ambient light is loaded one slot past the
            // directional lights and carries no direction.
            ReadLight((index - firstLightIndex) / 2, ResolveAddress(word1));
            return;
        }

        if (index != viewportIndex)
        {
            return;
        }

        ReadViewport(ResolveAddress(word1));
    }

    private void ReadLight(int slot, uint address)
    {
        _lightColors[slot] = new Vector3(
            _memory.ReadByte(address) / 255f,
            _memory.ReadByte(address + 1) / 255f,
            _memory.ReadByte(address + 2) / 255f);
        _lightDirections[slot] = new Vector3(
            (sbyte)_memory.ReadByte(address + 8) / 128f,
            (sbyte)_memory.ReadByte(address + 9) / 128f,
            (sbyte)_memory.ReadByte(address + 10) / 128f);
        _lightsLoaded = true;
    }

    private void ReadViewport(uint address)
    {
        _viewportScale = new Vector4(
            (short)_memory.ReadUInt16(address) / 4f,
            (short)_memory.ReadUInt16(address + 2) / 4f,
            (short)_memory.ReadUInt16(address + 4) / 4f,
            (short)_memory.ReadUInt16(address + 6) / 4f);
        _viewportTranslate = new Vector4(
            (short)_memory.ReadUInt16(address + 8) / 4f,
            (short)_memory.ReadUInt16(address + 10) / 4f,
            (short)_memory.ReadUInt16(address + 12) / 4f,
            (short)_memory.ReadUInt16(address + 14) / 4f);
    }

    private Vector4 ComputeLitVertexColor(uint vertexAddress, Matrix4x4 modelView)
    {
        var alpha = _memory.ReadByte(vertexAddress + 15) / 255f;
        if (!_lightsLoaded)
        {
            return new Vector4(1, 1, 1, alpha);
        }

        var normal = new Vector3(
            (sbyte)_memory.ReadByte(vertexAddress + 12) / 128f,
            (sbyte)_memory.ReadByte(vertexAddress + 13) / 128f,
            (sbyte)_memory.ReadByte(vertexAddress + 14) / 128f);
        var rotated = Vector3.TransformNormal(normal, modelView);
        var lengthSquared = rotated.LengthSquared();
        if (lengthSquared > 0.000001f)
        {
            rotated /= MathF.Sqrt(lengthSquared);
        }

        // The ambient light occupies the slot after the directional lights.
        var color = _lightColors[Math.Min(_lightCount, _lightColors.Length - 1)];
        for (var light = 0; light < _lightCount; light++)
        {
            var intensity = Vector3.Dot(rotated, _lightDirections[light]);
            if (intensity > 0)
            {
                color += _lightColors[light] * intensity;
            }
        }

        return new Vector4(
            Math.Min(color.X, 1),
            Math.Min(color.Y, 1),
            Math.Min(color.Z, 1),
            alpha);
    }

    /// <summary>
    /// F3DEX2 reassigns the matrix flag bits (projection moves from 1 to 4,
    /// push from 4 to 1) and stores push inverted, so translate them into the
    /// Fast3D layout the shared loader expects.
    /// </summary>
    private static int ConvertF3dex2MatrixParameters(uint word0)
    {
        var encoded = word0 & 0xFF;
        var parameters = 0;
        if ((encoded & 4) != 0)
        {
            parameters |= 1; // projection
        }

        if ((encoded & 2) != 0)
        {
            parameters |= 2; // load rather than multiply
        }

        if ((encoded & 1) == 0)
        {
            parameters |= 4; // push (F3DEX2 encodes no-push as the set bit)
        }

        return parameters;
    }

    private void LoadMatrix(int parameters, uint word1)
    {
        const int projectionFlag = 1;
        const int loadFlag = 2;
        const int pushFlag = 4;
        var incoming = ReadMatrix(ResolveAddress(word1));
        // The RSP multiplies the incoming matrix into the current one as
        // incoming * current, which for this row-vector convention means the
        // new matrix is applied before whatever is already on the stack.
        if ((parameters & projectionFlag) != 0)
        {
            _projection = (parameters & loadFlag) != 0
                ? incoming
                : Matrix4x4.Multiply(incoming, _projection);
            return;
        }

        var current = _modelViewStack.Peek();
        var result = (parameters & loadFlag) != 0
            ? incoming
            : Matrix4x4.Multiply(incoming, current);
        if ((parameters & pushFlag) != 0)
        {
            _modelViewStack.Push(result);
        }
        else
        {
            _modelViewStack.Pop();
            _modelViewStack.Push(result);
        }
    }

    private Matrix4x4 ReadMatrix(uint address)
    {
        Span<float> values = stackalloc float[16];
        for (var index = 0; index < values.Length; index++)
        {
            var integerAddress = address + (uint)(index * 2);
            var fractionAddress = address + 32 + (uint)(index * 2);
            var fixedPoint =
                ((uint)_memory.ReadUInt16(integerAddress) << 16) |
                _memory.ReadUInt16(fractionAddress);
            values[index] = unchecked((int)fixedPoint) / 65536f;
        }

        return new Matrix4x4(
            values[0], values[1], values[2], values[3],
            values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11],
            values[12], values[13], values[14], values[15]);
    }

    private void LoadVertices(uint word0, uint word1)
    {
        var parameters = (int)((word0 >> 16) & 0xFF);
        LoadVerticesInto((parameters & 0xF), (parameters >> 4) + 1, ResolveAddress(word1));
    }

    private void LoadVerticesInto(int destination, int count, uint address)
    {
        var combined = Matrix4x4.Multiply(_modelViewStack.Peek(), _projection);
        for (var index = 0; index < count && destination + index < _vertices.Length; index++)
        {
            var vertexAddress = address + (uint)(index * 16);
            var position = new Vector4(
                (short)_memory.ReadUInt16(vertexAddress),
                (short)_memory.ReadUInt16(vertexAddress + 2),
                (short)_memory.ReadUInt16(vertexAddress + 4),
                1);
            var clip = Vector4.Transform(position, combined);
            var lightingEnabled = (_geometryMode & 0x00020000) != 0;
            _vertices[destination + index] = CreateVertex(
                clip,
                lightingEnabled
                    ? ComputeLitVertexColor(vertexAddress, _modelViewStack.Peek())
                    : new Vector4(
                        _memory.ReadByte(vertexAddress + 12) / 255f,
                        _memory.ReadByte(vertexAddress + 13) / 255f,
                        _memory.ReadByte(vertexAddress + 14) / 255f,
                        _memory.ReadByte(vertexAddress + 15) / 255f),
                new Vector2(
                    (short)_memory.ReadUInt16(vertexAddress + 8) / 32f * _textureScaleS,
                    (short)_memory.ReadUInt16(vertexAddress + 10) / 32f * _textureScaleT));
            VerticesTransformed++;
        }
    }

    /// <summary>
    /// F3DEX2 packs three vertex indices as byte pairs in a single word; the
    /// stored value is the index doubled.
    /// </summary>
    private void DrawTriangleF3dex2(uint word) =>
        DrawTriangleIndices(
            (int)((word >> 16) & 0xFF) / 2,
            (int)((word >> 8) & 0xFF) / 2,
            (int)(word & 0xFF) / 2);

    /// <summary>
    /// F3DEX2 G_VTX names the vertex slot one past the last one written, so
    /// the destination is that index minus the count.
    /// </summary>
    private void LoadVerticesF3dex2(uint word0, uint word1) =>
        LoadVerticesEndIndexed((int)((word0 >> 12) & 0xFF), word0, word1);

    /// <summary>
    /// Both F3DEX revisions name the slot one past the last vertex written;
    /// only the width and position of the count field differ.
    /// </summary>
    private void LoadVerticesEndIndexed(int count, uint word0, uint word1)
    {
        var destination = (int)((word0 >> 1) & 0x7F) - count;
        if (count <= 0 || destination < 0)
        {
            return;
        }

        LoadVerticesInto(destination, count, ResolveAddress(word1));
    }

    /// <summary>
    /// F3DEX version 1 keeps Fast3D's G_VTX opcode but repacks its operands:
    /// the count sits at bits 10-15 and the field at bit 1 is the slot one
    /// past the last vertex written.
    /// </summary>
    private void LoadVerticesF3dex(uint word0, uint word1) =>
        LoadVerticesEndIndexed((int)((word0 >> 10) & 0x3F), word0, word1);

    /// <summary>
    /// F3DEX2 G_MODIFYVTX patches one attribute of an already-loaded vertex
    /// in place rather than re-running the transform. WWF WrestleMania 2000
    /// builds most of its geometry this way.
    /// </summary>
    private void ModifyVertex(uint word0, uint word1)
    {
        const int pointRgba = 0x10;
        const int pointSt = 0x14;
        const int pointXyScreen = 0x18;
        const int pointZScreen = 0x1C;
        var field = (int)((word0 >> 16) & 0xFF);
        var index = (int)((word0 >> 1) & 0x7F) / 2;
        if (index < 0 || index >= _vertices.Length)
        {
            return;
        }

        var vertex = _vertices[index];
        switch (field)
        {
            case pointRgba:
                _vertices[index] = vertex with
                {
                    Color = new Vector4(
                        ((word1 >> 24) & 0xFF) / 255f,
                        ((word1 >> 16) & 0xFF) / 255f,
                        ((word1 >> 8) & 0xFF) / 255f,
                        (word1 & 0xFF) / 255f)
                };
                break;
            case pointSt:
                _vertices[index] = vertex with
                {
                    TextureCoordinate = new Vector2(
                        (short)(word1 >> 16) / 32f * _textureScaleS,
                        (short)word1 / 32f * _textureScaleT)
                };
                break;
            case pointXyScreen:
                _vertices[index] = vertex with
                {
                    Position = new Vector3(
                        (short)(word1 >> 16) / 4f,
                        (short)word1 / 4f,
                        vertex.Position.Z),
                    Valid = true
                };
                break;
            case pointZScreen:
                _vertices[index] = vertex with
                {
                    Position = new Vector3(
                        vertex.Position.X,
                        vertex.Position.Y,
                        unchecked((int)word1) / 65536f)
                };
                break;
        }
    }

    private void MoveWordF3dex2(uint word0, uint word1)
    {
        const int numberOfLightsIndex = 0x02;
        const int segmentIndex = 0x06;
        var index = (int)((word0 >> 16) & 0xFF);
        var offset = (int)(word0 & 0xFFFF);
        if (index == numberOfLightsIndex)
        {
            // F3DEX2 encodes the count as lights * 24.
            _lightCount = Math.Clamp((int)(word1 / 24), 0, 7);
            return;
        }

        if (index != segmentIndex)
        {
            return;
        }

        var segment = offset / 4;
        if (segment >= 0 && segment < _segments.Length)
        {
            _segments[segment] = word1 & 0x00FFFFFF;
        }
    }

    private void MoveMemoryF3dex2(uint word0, uint word1)
    {
        const int viewportIndex = 0x08;
        const int lightIndex = 0x0A;
        var index = (int)(word0 & 0xFF);
        var offset = (int)((word0 >> 8) & 0xFF) * 8;
        var address = ResolveAddress(word1);
        if (index == viewportIndex)
        {
            ReadViewport(address);
            return;
        }

        if (index != lightIndex)
        {
            return;
        }

        // Light n lives at offset (n + 1) * 24.
        var slot = (offset / 24) - 1;
        if (slot is < 0 or > 7)
        {
            return;
        }

        ReadLight(slot, address);
    }

    private void DrawTriangle(uint word1) =>
        DrawTriangleIndices(
            (int)((word1 >> 16) & 0xFF) / 10,
            (int)((word1 >> 8) & 0xFF) / 10,
            (int)(word1 & 0xFF) / 10);

    private void DrawLine(uint word1) =>
        DrawLineIndices(
            (int)((word1 >> 16) & 0xFF) / 10,
            (int)((word1 >> 8) & 0xFF) / 10,
            (byte)word1);

    private void DrawLineF3dex(uint word0) =>
        DrawLineIndices(
            (int)((word0 >> 16) & 0xFF) / 2,
            (int)((word0 >> 8) & 0xFF) / 2,
            (byte)word0);

    private void DrawLineIndices(int first, int second, int encodedWidth)
    {
        if (!RasterizationEnabled ||
            first < 0 ||
            second < 0 ||
            first >= _vertices.Length ||
            second >= _vertices.Length ||
            _colorImageAddress >= N64Memory.RdramSize ||
            _colorImageWidth <= 0 ||
            _colorImageSize is not (2 or 3))
        {
            return;
        }

        var a = _vertices[first];
        var b = _vertices[second];
        if (!a.Valid || !b.Valid || (a.ClipFlags & b.ClipFlags) != 0)
        {
            return;
        }

        // The line microcode guarantees a minimum 1.5-pixel footprint and
        // encodes additional width in half-pixel units.
        var lineWidth = 1.5f + (encodedWidth * 0.5f);
        var radius = lineWidth * 0.5f;
        var delta = b.Position - a.Position;
        var lengthSquared = (delta.X * delta.X) + (delta.Y * delta.Y);
        if (lengthSquared < 0.000001f)
        {
            return;
        }

        var bytesPerPixel = _colorImageSize == 2 ? 2 : 4;
        var remainingBytes = N64Memory.RdramSize - (int)_colorImageAddress;
        var maximumHeight = Math.Clamp(
            remainingBytes / (_colorImageWidth * bytesPerPixel),
            1,
            480);
        var minX = Math.Clamp(
            Math.Max((int)MathF.Floor(MathF.Min(a.Position.X, b.Position.X) - radius), _scissorLeft),
            0,
            _colorImageWidth - 1);
        var maxX = Math.Clamp(
            Math.Min((int)MathF.Ceiling(MathF.Max(a.Position.X, b.Position.X) + radius), _scissorRight - 1),
            0,
            _colorImageWidth - 1);
        var minY = Math.Clamp(
            Math.Max((int)MathF.Floor(MathF.Min(a.Position.Y, b.Position.Y) - radius), _scissorTop),
            0,
            maximumHeight - 1);
        var maxY = Math.Clamp(
            Math.Min((int)MathF.Ceiling(MathF.Max(a.Position.Y, b.Position.Y) + radius), _scissorBottom - 1),
            0,
            maximumHeight - 1);
        if (maxX < minX || maxY < minY)
        {
            return;
        }

        const uint zCompare = 0x10;
        const uint zUpdate = 0x20;
        const uint zSourcePrimitive = 0x4;
        var hasDepth =
            (_geometryMode & 1) != 0 &&
            _depthImageAddress < N64Memory.RdramSize;
        var compareDepth = hasDepth && (_otherModeLow & zCompare) != 0;
        var updateDepth = hasDepth && (_otherModeLow & zUpdate) != 0;
        var usePrimitiveDepth = (_otherModeLow & zSourcePrimitive) != 0;
        if (compareDepth || updateDepth)
        {
            EnsureDepthBuffer(_colorImageWidth, maximumHeight);
        }

        var radiusSquared = radius * radius;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var px = x + 0.5f - a.Position.X;
                var py = y + 0.5f - a.Position.Y;
                var amount = Math.Clamp(
                    ((px * delta.X) + (py * delta.Y)) / lengthSquared,
                    0,
                    1);
                var nearestX = a.Position.X + (delta.X * amount);
                var nearestY = a.Position.Y + (delta.Y * amount);
                var distanceX = x + 0.5f - nearestX;
                var distanceY = y + 0.5f - nearestY;
                if ((distanceX * distanceX) + (distanceY * distanceY) > radiusSquared)
                {
                    continue;
                }

                var depthIndex = (y * _colorImageWidth) + x;
                var depth = usePrimitiveDepth
                    ? (_primitiveDepth & 0x7FFF) / 32f
                    : a.Position.Z + ((b.Position.Z - a.Position.Z) * amount);
                if (compareDepth && depth > _depthBuffer[depthIndex])
                {
                    DepthPixelsRejected++;
                    continue;
                }

                var shade = Vector4.Lerp(a.Color, b.Color, amount);
                var color = _combinerConfigured
                    ? EvaluateCombiner(shade, Vector4.Zero)
                    : shade;
                if (WriteColorPixel(x, y, color, shade) && updateDepth)
                {
                    _depthBuffer[depthIndex] = depth;
                }
            }
        }

        LinesDrawn++;
    }

    private void DrawTriangleIndices(int first, int second, int third)
    {
        if (first >= _vertices.Length || second >= _vertices.Length || third >= _vertices.Length)
        {
            return;
        }

        var a = _vertices[first];
        var b = _vertices[second];
        var c = _vertices[third];
        if (!a.Valid || !b.Valid || !c.Valid)
        {
            return;
        }

        // The overwhelming majority of game geometry is already wholly inside
        // the homogeneous view volume. Avoid running those triangles through
        // seven clipping planes and allocating/copying the temporary polygons.
        // Mario's interactive title-screen face is particularly sensitive to
        // this overhead because it submits many small, fully visible triangles.
        var commonClipFlags = (byte)(a.ClipFlags & b.ClipFlags & c.ClipFlags);
        if (commonClipFlags != 0)
        {
            TriviallyClippedTriangles++;
            return;
        }

        var combinedClipFlags = (byte)(a.ClipFlags | b.ClipFlags | c.ClipFlags);
        if (combinedClipFlags == 0)
        {
            RasterizeTriangle(a, b, c);
            return;
        }

        Span<Fast3dVertex> source = stackalloc Fast3dVertex[16];
        Span<Fast3dVertex> destination = stackalloc Fast3dVertex[16];
        source[0] = a;
        source[1] = b;
        source[2] = c;
        var vertexCount = 3;
        for (var plane = 0; plane < 7; plane++)
        {
            vertexCount = ClipPolygonAgainstPlane(
                source,
                vertexCount,
                destination,
                plane);
            if (vertexCount < 3)
            {
                TriviallyClippedTriangles++;
                return;
            }

            var swap = source;
            source = destination;
            destination = swap;
        }

        for (var index = 1; index < vertexCount - 1; index++)
        {
            RasterizeTriangle(source[0], source[index], source[index + 1]);
        }
    }

    private Fast3dVertex CreateVertex(
        Vector4 clipPosition,
        Vector4 color,
        Vector2 textureCoordinate)
    {
        var inverseW = Math.Abs(clipPosition.W) > 0.000001f
            ? 1f / clipPosition.W
            : 0;
        var screen = ProjectClipToScreen(
            clipPosition,
            inverseW,
            _viewportScale,
            _viewportTranslate);
        return new Fast3dVertex(
            clipPosition,
            screen,
            color,
            textureCoordinate,
            inverseW,
            ComputeClipFlags(clipPosition),
            float.IsFinite(screen.X) &&
            float.IsFinite(screen.Y) &&
            float.IsFinite(screen.Z));
    }

    internal static Vector3 ProjectClipToScreen(
        Vector4 clipPosition,
        float inverseW,
        Vector4 viewportScale,
        Vector4 viewportTranslate) =>
        new(
            (clipPosition.X * inverseW * viewportScale.X) + viewportTranslate.X,
            viewportTranslate.Y - (clipPosition.Y * inverseW * viewportScale.Y),
            (clipPosition.Z * inverseW * viewportScale.Z) + viewportTranslate.Z);

    private int ClipPolygonAgainstPlane(
        ReadOnlySpan<Fast3dVertex> source,
        int sourceCount,
        Span<Fast3dVertex> destination,
        int plane)
    {
        var destinationCount = 0;
        var previous = source[sourceCount - 1];
        var previousDistance = ClipDistance(previous.ClipPosition, plane);
        var previousInside = previousDistance >= 0;
        for (var index = 0; index < sourceCount; index++)
        {
            var current = source[index];
            var currentDistance = ClipDistance(current.ClipPosition, plane);
            var currentInside = currentDistance >= 0;
            if (currentInside != previousInside)
            {
                var amount = previousDistance / (previousDistance - currentDistance);
                destination[destinationCount++] = CreateVertex(
                    Vector4.Lerp(previous.ClipPosition, current.ClipPosition, amount),
                    Vector4.Lerp(previous.Color, current.Color, amount),
                    Vector2.Lerp(previous.TextureCoordinate, current.TextureCoordinate, amount));
            }

            if (currentInside)
            {
                destination[destinationCount++] = current;
            }

            previous = current;
            previousDistance = currentDistance;
            previousInside = currentInside;
        }

        return destinationCount;
    }

    private static float ClipDistance(Vector4 position, int plane) =>
        plane switch
        {
            0 => position.W - 0.000001f,
            1 => position.X + position.W,
            2 => position.W - position.X,
            3 => position.Y + position.W,
            4 => position.W - position.Y,
            5 => position.Z + position.W,
            6 => position.W - position.Z,
            _ => throw new ArgumentOutOfRangeException(nameof(plane))
        };

    private void RasterizeTriangle(Fast3dVertex a, Fast3dVertex b, Fast3dVertex c)
    {
        if (!RasterizationEnabled && _capturedRdpCommands is null)
        {
            return;
        }

        if (_colorImageAddress >= N64Memory.RdramSize ||
            _colorImageWidth <= 0 ||
            _colorImageSize is not (2 or 3))
        {
            return;
        }

        var area = Edge(a.Position, b.Position, c.Position.X, c.Position.Y);
        if (Math.Abs(area) < 0.0001f)
        {
            return;
        }

        if (ShouldCullTriangle(_geometryMode, area))
        {
            return;
        }

        CaptureHleTriangle(a, b, c);
        if (!RasterizationEnabled)
        {
            return;
        }

        var bytesPerPixel = _colorImageSize == 2 ? 2 : 4;
        var remainingBytes = N64Memory.RdramSize - (int)_colorImageAddress;
        var maximumHeight = Math.Clamp(
            remainingBytes / (_colorImageWidth * bytesPerPixel),
            1,
            480);
        var rawMinX = (int)MathF.Floor(MathF.Min(a.Position.X, MathF.Min(b.Position.X, c.Position.X)));
        var rawMaxX = (int)MathF.Ceiling(MathF.Max(a.Position.X, MathF.Max(b.Position.X, c.Position.X)));
        var rawMinY = (int)MathF.Floor(MathF.Min(a.Position.Y, MathF.Min(b.Position.Y, c.Position.Y)));
        var rawMaxY = (int)MathF.Ceiling(MathF.Max(a.Position.Y, MathF.Max(b.Position.Y, c.Position.Y)));
        MaximumTriangleWidth = Math.Max(MaximumTriangleWidth, rawMaxX - rawMinX);
        MaximumTriangleHeight = Math.Max(MaximumTriangleHeight, rawMaxY - rawMinY);
        if (rawMaxX < 0 ||
            rawMinX >= _colorImageWidth ||
            rawMaxY < 0 ||
            rawMinY >= maximumHeight)
        {
            return;
        }

        var minX = Math.Clamp(Math.Max(rawMinX, _scissorLeft), 0, _colorImageWidth - 1);
        var maxX = Math.Clamp(Math.Min(rawMaxX, _scissorRight - 1), 0, _colorImageWidth - 1);
        var minY = Math.Clamp(Math.Max(rawMinY, _scissorTop), 0, maximumHeight - 1);
        var maxY = Math.Clamp(Math.Min(rawMaxY, _scissorBottom - 1), 0, maximumHeight - 1);
        var inverseArea = 1f / area;
        const uint zCompare = 0x10;
        const uint zUpdate = 0x20;
        const uint zSourcePrimitive = 0x4;
        var hasDepth =
            (_geometryMode & 1) != 0 &&
            _depthImageAddress < N64Memory.RdramSize;
        var compareDepth = hasDepth && (_otherModeLow & zCompare) != 0;
        var updateDepth = hasDepth && (_otherModeLow & zUpdate) != 0;
        var usePrimitiveDepth = (_otherModeLow & zSourcePrimitive) != 0;
        if (compareDepth || updateDepth)
        {
            EnsureDepthBuffer(_colorImageWidth, maximumHeight);
        }

        // The edge functions are linear in x and y, so evaluate them once at
        // the top-left sample and step them incrementally across the box.
        var drawTextured =
            _textureEnabled &&
            _combinerUsesTexture &&
            HasTextureForTile(_textureTile);
        var textureState = drawTextured
            ? CreateTextureSampleState(_textureTile)
            : default;
        var stepAx = (c.Position.Y - b.Position.Y) * inverseArea;
        var stepAy = (b.Position.X - c.Position.X) * inverseArea;
        var stepBx = (a.Position.Y - c.Position.Y) * inverseArea;
        var stepBy = (c.Position.X - a.Position.X) * inverseArea;
        var rowWeightA = Edge(b.Position, c.Position, minX + 0.5f, minY + 0.5f) * inverseArea;
        var rowWeightB = Edge(c.Position, a.Position, minX + 0.5f, minY + 0.5f) * inverseArea;
        for (var y = minY; y <= maxY; y++)
        {
            var weightA = rowWeightA;
            var weightB = rowWeightB;
            rowWeightA += stepAy;
            rowWeightB += stepBy;
            for (var x = minX; x <= maxX; x++, weightA += stepAx, weightB += stepBx)
            {
                var weightC = 1f - weightA - weightB;
                if (weightA < 0 || weightB < 0 || weightC < 0)
                {
                    continue;
                }

                var depthIndex = (y * _colorImageWidth) + x;
                // Primitive Z is a 15-bit screen-space value. Fast3D's
                // viewport depth convention spans approximately 0..1024,
                // so the RDP's 0..0x7fff range maps directly at 1/32 scale.
                var depth = usePrimitiveDepth
                    ? (_primitiveDepth & 0x7FFF) / 32f
                    : (a.Position.Z * weightA) +
                      (b.Position.Z * weightB) +
                      (c.Position.Z * weightC);
                if (compareDepth && depth > _depthBuffer[depthIndex])
                {
                    DepthPixelsRejected++;
                    continue;
                }

                var shade =
                    (a.Color * weightA) + (b.Color * weightB) + (c.Color * weightC);
                var color = shade;
                var texel = Vector4.Zero;
                if (drawTextured)
                {
                    var reciprocalW =
                        (weightA * a.ReciprocalW) +
                        (weightB * b.ReciprocalW) +
                        (weightC * c.ReciprocalW);
                    if (Math.Abs(reciprocalW) > 0.000001f)
                    {
                        var textureCoordinate =
                            ((a.TextureCoordinate * (weightA * a.ReciprocalW)) +
                             (b.TextureCoordinate * (weightB * b.ReciprocalW)) +
                             (c.TextureCoordinate * (weightC * c.ReciprocalW))) /
                            reciprocalW;
                        var textureColor = SampleTexture(textureCoordinate, textureState);
                        if (textureColor.W <= 0)
                        {
                            continue;
                        }

                        texel = textureColor;
                        TexturedPixelsDrawn++;
                    }
                }

                // The colour combiner is part of every RDP pixel path, not
                // only textured spans. Mario 64's pause-screen shade is
                // untextured geometry whose alpha comes from this state.
                // Keep the legacy shade fallback only until a display list
                // has actually configured a combiner.
                if (_combinerConfigured)
                {
                    color = EvaluateCombiner(shade, texel);
                }

                if (WriteColorPixel(x, y, color, shade) && updateDepth)
                {
                    _depthBuffer[depthIndex] = depth;
                }
            }
        }

        TrianglesDrawn++;
    }

    private static float Edge(Vector3 a, Vector3 b, float x, float y) =>
        ((x - a.X) * (b.Y - a.Y)) - ((y - a.Y) * (b.X - a.X));

    internal static bool ShouldCullTriangle(uint geometryMode, float signedArea)
    {
        const uint cullFront = 0x00001000;
        const uint cullBack = 0x00002000;
        return ((geometryMode & cullBack) != 0 && signedArea < 0) ||
               ((geometryMode & cullFront) != 0 && signedArea > 0);
    }

    internal static byte ComputeClipFlags(Vector4 clip)
    {
        byte flags = 0;
        if (clip.X < -clip.W)
        {
            flags |= 1 << 0;
        }
        else if (clip.X > clip.W)
        {
            flags |= 1 << 1;
        }

        if (clip.Y < -clip.W)
        {
            flags |= 1 << 2;
        }
        else if (clip.Y > clip.W)
        {
            flags |= 1 << 3;
        }

        if (clip.Z < -clip.W)
        {
            flags |= 1 << 4;
        }
        else if (clip.Z > clip.W)
        {
            flags |= 1 << 5;
        }

        return flags;
    }

    private bool ShouldCullDisplayList(uint word0, uint word1)
    {
        var (start, end) = DecodeCullVertexRange(_microcode, word0, word1);
        if (start < 0 || end < start || end >= _vertices.Length)
        {
            return false;
        }

        Span<byte> clipFlags = stackalloc byte[end - start + 1];
        for (var index = start; index <= end; index++)
        {
            if (!_vertices[index].Valid)
            {
                // A malformed or partially populated range is safer to draw
                // than to discard. Real display lists populate every vertex
                // referenced by G_CULLDL.
                return false;
            }

            clipFlags[index - start] = _vertices[index].ClipFlags;
        }

        return AllVerticesShareClipPlane(clipFlags);
    }

    internal static (int Start, int End) DecodeCullVertexRange(
        N64Microcode microcode,
        uint word0,
        uint word1)
    {
        if (microcode is N64Microcode.F3dex or N64Microcode.F3dex2)
        {
            return ((int)(word0 & 0xFFFF) / 2, (int)(word1 & 0xFFFF) / 2);
        }

        // Original Fast3D stores vertex byte offsets (40 bytes per vertex).
        // The SDK masks (vend + 1) to four bits, so an encoded zero denotes
        // the inclusive final slot 15 rather than an empty range.
        var start = ((int)(word0 & 0xFFFF) / 40) & 0xF;
        var encodedEnd = ((int)(word1 & 0xFFFF) / 40) & 0xF;
        var end = (encodedEnd + 15) & 0xF;
        return (start, end);
    }

    internal static bool AllVerticesShareClipPlane(ReadOnlySpan<byte> clipFlags)
    {
        if (clipFlags.IsEmpty)
        {
            return false;
        }

        byte sharedPlanes = 0x3F;
        foreach (var flags in clipFlags)
        {
            sharedPlanes &= flags;
        }

        return sharedPlanes != 0;
    }

    private void DrawTextureRectangle(
        uint word0,
        uint word1,
        uint textureOrigin,
        uint textureStep,
        bool flip)
    {
        if (!RasterizationEnabled)
        {
            return;
        }

        if (_colorImageAddress >= N64Memory.RdramSize ||
            _colorImageWidth <= 0 ||
            _colorImageSize is not (2 or 3) ||
            !HasTextureForTile((int)((word1 >> 24) & 7)))
        {
            return;
        }

        var tileIndex = (int)((word1 >> 24) & 7);
        var left = (int)((word1 >> 12) & 0xFFF) / 4;
        var top = (int)(word1 & 0xFFF) / 4;
        var right = (int)((word0 >> 12) & 0xFFF) / 4;
        var bottom = (int)(word0 & 0xFFF) / 4;
        var startS = (short)(textureOrigin >> 16) / 32f;
        var startT = (short)textureOrigin / 32f;
        var stepS = (short)(textureStep >> 16) / 1024f;
        var stepT = (short)textureStep / 1024f;
        if (CycleType == 2)
        {
            // COPY mode encodes dsdx as 4.0 because the RDP copies four
            // texels per clock; the effective per-pixel step is dsdx / 4.
            stepS /= 4f;
        }
        if (right < left || bottom < top)
        {
            return;
        }

        var maximumHeight = Math.Min(
            480,
            Math.Max(
                1,
                (N64Memory.RdramSize - (int)_colorImageAddress) /
                (_colorImageWidth * (_colorImageSize == 2 ? 2 : 4))));
        var firstX = Math.Clamp(left, 0, _colorImageWidth - 1);
        var lastX = Math.Clamp(right, 0, _colorImageWidth - 1);
        var firstY = Math.Clamp(top, 0, maximumHeight - 1);
        var lastY = Math.Clamp(bottom, 0, maximumHeight - 1);
        var textureState = CreateTextureSampleState(tileIndex);
        for (var y = firstY; y <= lastY; y++)
        {
            for (var x = firstX; x <= lastX; x++)
            {
                var deltaX = x - left;
                var deltaY = y - top;
                var textureCoordinate = flip
                    ? new Vector2(startS + (deltaY * stepS), startT + (deltaX * stepT))
                    : new Vector2(startS + (deltaX * stepS), startT + (deltaY * stepT));
                var color = SampleTexture(textureCoordinate, textureState);
                if (color.W <= 0)
                {
                    continue;
                }

                var shade = Vector4.One;
                var output = _combinerConfigured
                    ? EvaluateCombiner(shade, color)
                    : color * _primitiveColor;
                WriteColorPixel(x, y, output, shade);
                TexturedPixelsDrawn++;
            }
        }

        TextureRectanglesDrawn++;
    }

    private TextureSampleState CreateTextureSampleState(int selectedTile)
    {
        var tileIndex = Math.Clamp(selectedTile, 0, _tiles.Length - 1);
        var tile = _tiles[tileIndex];
        var width = Math.Max(1, ((tile.LowerRightS - tile.UpperLeftS) >> 2) + 1);
        var height = Math.Max(1, ((tile.LowerRightT - tile.UpperLeftT) >> 2) + 1);
        var bitsPerTexel = BitsPerTexel(tile.Size);
        var rowStrideBits = tile.Line > 0
            ? tile.Line * 64
            : width * bitsPerTexel;
        RecordSampledTextureFormatSupport(tile.Format, tile.Size);
        return new TextureSampleState(
            tile.Format,
            tile.Size,
            tile.Palette,
            width,
            height,
            bitsPerTexel,
            rowStrideBits,
            tile.Tmem * 64,
            tile.UpperLeftS / 4f,
            tile.UpperLeftT / 4f,
            tile.UpperLeftT >> 2,
            tile.ShiftS,
            tile.ShiftT,
            tile.MaskS,
            tile.MaskT,
            tile.ClampS,
            tile.ClampT,
            tile.MirrorS,
            tile.MirrorT);
    }

    private Vector4 SampleTexture(
        Vector2 textureCoordinate,
        in TextureSampleState state)
    {
        var s = ApplyTextureShift(textureCoordinate.X - state.UpperLeftS, state.ShiftS);
        var t = ApplyTextureShift(textureCoordinate.Y - state.UpperLeftT, state.ShiftT);
        var x = ApplyTextureAddressing(
            (int)MathF.Floor(s),
            state.Width,
            state.MaskS,
            state.ClampS,
            state.MirrorS);
        var y = ApplyTextureAddressing(
            (int)MathF.Floor(t),
            state.Height,
            state.MaskT,
            state.ClampT,
            state.MirrorT);
        var bitOffset =
            state.BaseBitOffset +
            (y * state.RowStrideBits) +
            (x * state.BitsPerTexel);
        if (((state.UpperLeftRow + y) & 1) != 0)
        {
            bitOffset ^= 32;
        }
        return (state.Format, state.Size) switch
        {
            (0, 2) => DecodeRgba16(ReadTmemUInt16(bitOffset >> 3)),
            (0, 3) => DecodeRgba32(ReadTmemUInt32(bitOffset >> 3)),
            (2, 0) => DecodePaletteTexel(
                state.Palette * 16 +
                ((x & 1) == 0
                    ? ReadTmemByte(bitOffset >> 3) >> 4
                    : ReadTmemByte(bitOffset >> 3) & 0xF)),
            (2, 1) => DecodePaletteTexel(ReadTmemByte(bitOffset >> 3)),
            (3, 0) => DecodeIntensityAlpha4(
                ReadTmemByte(bitOffset >> 3),
                x),
            (3, 1) => DecodeIntensityAlpha8(ReadTmemByte(bitOffset >> 3)),
            (3, 2) => DecodeIntensityAlpha16(ReadTmemUInt16(bitOffset >> 3)),
            (4, 0) => DecodeIntensity4(
                ReadTmemByte(bitOffset >> 3),
                x),
            (4, 1) => DecodeIntensity8(ReadTmemByte(bitOffset >> 3)),
            _ => Vector4.One
        };
    }

    /// <summary>
    /// Counts sampled tile configurations whose texel format the software
    /// renderer cannot decode. Load-only tiles intentionally use otherwise
    /// invalid format/size pairs as transfer widths, so recording every
    /// G_SETTILE produces false compatibility warnings.
    /// </summary>
    private readonly long[] _unsupportedTextureFormats = new long[32];

    internal IEnumerable<(int Format, int Size, long Count)> UnsupportedTextureFormats =>
        _unsupportedTextureFormats
            .Select((count, key) => (Format: key >> 2, Size: key & 3, Count: count))
            .Where(entry => entry.Count > 0)
            .OrderByDescending(entry => entry.Count);

    /// <summary>
    /// Unsupported texture formats observed by the renderer, keyed by an
    /// N64 format/size pair. Counts are per primitive that attempted to sample
    /// the format rather than per pixel.
    /// </summary>
    public IReadOnlyDictionary<string, long> UnsupportedTextureFormatCounts =>
        UnsupportedTextureFormats.ToDictionary(
            entry => $"format-{entry.Format}/size-{entry.Size}",
            entry => entry.Count,
            StringComparer.Ordinal);

    private void RecordSampledTextureFormatSupport(int format, int size)
    {
        var supported = (format, size) switch
        {
            (0, 2) or (0, 3) or (2, 0) or (2, 1) => true,
            (3, 0) or (3, 1) or (3, 2) or (4, 0) or (4, 1) => true,
            _ => false
        };
        if (!supported)
        {
            _unsupportedTextureFormats[((format & 7) << 2) | (size & 3)]++;
        }
    }

    /// <summary>
    /// The mux selectors for one combiner cycle. Decoded once when
    /// G_SETCOMBINE is handled rather than re-extracted per pixel.
    /// </summary>
    private readonly record struct CombinerCycle(
        int ColourA,
        int ColourB,
        int ColourC,
        int ColourD,
        int AlphaA,
        int AlphaB,
        int AlphaC,
        int AlphaD);

    private void DecodeCombiner(uint word0, uint word1)
    {
        _combinerCycle0 = new CombinerCycle(
            (int)((word0 >> 20) & 0xF),
            (int)((word1 >> 28) & 0xF),
            (int)((word0 >> 15) & 0x1F),
            (int)((word1 >> 15) & 0x7),
            (int)((word0 >> 12) & 0x7),
            (int)((word1 >> 12) & 0x7),
            (int)((word0 >> 9) & 0x7),
            (int)((word1 >> 9) & 0x7));
        _combinerCycle1 = new CombinerCycle(
            (int)((word0 >> 5) & 0xF),
            (int)((word1 >> 24) & 0xF),
            (int)(word0 & 0x1F),
            (int)((word1 >> 6) & 0x7),
            (int)((word1 >> 21) & 0x7),
            (int)((word1 >> 3) & 0x7),
            (int)((word1 >> 18) & 0x7),
            (int)(word1 & 0x7));
        _combinerUsesTexture = CombineUsesTexture(word0, word1);
        _combinerConfigured = true;
    }

    /// <summary>
    /// Evaluates the RDP colour combiner: each cycle computes
    /// (A - B) * C + D independently for colour and alpha. In two-cycle mode
    /// the first cycle's result feeds the second as the COMBINED source.
    /// </summary>
    private Vector4 EvaluateCombiner(Vector4 shade, Vector4 texel)
    {
        var combined = EvaluateCombinerCycle(_combinerCycle0, shade, texel, Vector4.Zero);
        if (CycleType == 1)
        {
            combined = EvaluateCombinerCycle(_combinerCycle1, shade, texel, combined);
        }

        return Vector4.Clamp(combined, Vector4.Zero, Vector4.One);
    }

    private Vector4 EvaluateCombinerCycle(
        CombinerCycle cycle,
        Vector4 shade,
        Vector4 texel,
        Vector4 combined)
    {
        // Fold the alpha selectors into the W lane so the cycle evaluates as
        // one vector expression rather than three scalar lanes plus a scalar
        // alpha; this runs for every textured pixel.
        var a = WithAlpha(CombinerColour(cycle.ColourA, shade, texel, combined), cycle.AlphaA, shade, texel, combined);
        var b = WithAlpha(CombinerColour(cycle.ColourB, shade, texel, combined), cycle.AlphaB, shade, texel, combined);
        var c = WithAlpha(CombinerScale(cycle.ColourC, shade, texel, combined), cycle.AlphaC, shade, texel, combined);
        var d = WithAlpha(CombinerColour(cycle.ColourD, shade, texel, combined), cycle.AlphaD, shade, texel, combined);
        return ((a - b) * c) + d;
    }

    private Vector4 WithAlpha(
        Vector4 colour,
        int alphaSource,
        Vector4 shade,
        Vector4 texel,
        Vector4 combined) =>
        colour with { W = CombinerColour(alphaSource, shade, texel, combined).W };

    private Vector4 CombinerColour(int source, Vector4 shade, Vector4 texel, Vector4 combined) =>
        source switch
        {
            0 => combined,
            1 or 2 => texel,
            3 => _primitiveColor,
            4 => shade,
            5 => _environmentColor,
            6 => Vector4.One,
            _ => Vector4.Zero
        };

    /// <summary>
    /// The C multiplier shares the colour sources but can also select an
    /// alpha channel, broadcast across all lanes.
    /// </summary>
    private Vector4 CombinerScale(int source, Vector4 shade, Vector4 texel, Vector4 combined) =>
        source switch
        {
            < 6 => CombinerColour(source, shade, texel, combined),
            7 => new Vector4(combined.W),
            8 or 9 => new Vector4(texel.W),
            10 => new Vector4(_primitiveColor.W),
            11 => new Vector4(shade.W),
            12 => new Vector4(_environmentColor.W),
            _ => Vector4.Zero
        };

    private Vector4 DecodePaletteTexel(int paletteIndex)
    {
        // The lookup table lives in upper TMEM with each entry replicated
        // across a 64-bit word. Other-mode-high bits 14-15 select the entry
        // format: 3 = IA16, otherwise RGBA16.
        var entry = ReadTmemUInt16(0x800 + (paletteIndex * 8));
        return ((_otherModeHigh >> 14) & 3) == 3
            ? DecodeIntensityAlpha16(entry)
            : DecodeRgba16(entry);
    }

    private byte ReadTmemByte(int address) => _textureMemory[address & 0xFFF];

    private ushort ReadTmemUInt16(int address) =>
        (ushort)((ReadTmemByte(address) << 8) | ReadTmemByte(address + 1));

    private uint ReadTmemUInt32(int address) =>
        ((uint)ReadTmemByte(address) << 24) |
        ((uint)ReadTmemByte(address + 1) << 16) |
        ((uint)ReadTmemByte(address + 2) << 8) |
        ReadTmemByte(address + 3);

    private static int BitsPerTexel(int size) => size switch
    {
        0 => 4,
        1 => 8,
        2 => 16,
        3 => 32,
        _ => 8
    };

    internal static bool CombineUsesTexture(uint word0, uint word1)
    {
        static bool IsColorTexture(int source) =>
            source is 1 or 2 or 8 or 9;

        static bool IsAlphaTexture(int source) =>
            source is 1 or 2;

        return
            IsColorTexture((int)((word0 >> 20) & 0xF)) ||
            IsColorTexture((int)((word0 >> 15) & 0x1F)) ||
            IsAlphaTexture((int)((word0 >> 12) & 0x7)) ||
            IsAlphaTexture((int)((word0 >> 9) & 0x7)) ||
            IsColorTexture((int)((word0 >> 5) & 0xF)) ||
            IsColorTexture((int)(word0 & 0x1F)) ||
            IsColorTexture((int)((word1 >> 28) & 0xF)) ||
            IsColorTexture((int)((word1 >> 24) & 0xF)) ||
            IsAlphaTexture((int)((word1 >> 21) & 0x7)) ||
            IsAlphaTexture((int)((word1 >> 18) & 0x7)) ||
            IsColorTexture((int)((word1 >> 15) & 0x7)) ||
            IsAlphaTexture((int)((word1 >> 12) & 0x7)) ||
            IsAlphaTexture((int)((word1 >> 9) & 0x7)) ||
            IsColorTexture((int)((word1 >> 6) & 0x7)) ||
            IsAlphaTexture((int)((word1 >> 3) & 0x7)) ||
            IsAlphaTexture((int)(word1 & 0x7));
    }

    private bool HasTextureForTile(int tileIndex)
    {
        var tile = _tiles[Math.Clamp(tileIndex, 0, _tiles.Length - 1)];
        return _loadedTextures[tile.Tmem].Valid;
    }

    private static float ApplyTextureShift(float coordinate, int shift) =>
        shift <= 10 ? coordinate / (1 << shift) : coordinate * (1 << (16 - shift));

    private static int ApplyTextureAddressing(
        int coordinate,
        int dimension,
        int mask,
        bool clamp,
        bool mirror)
    {
        if (mask == 0)
        {
            return Math.Clamp(coordinate, 0, dimension - 1);
        }

        if (clamp)
        {
            coordinate = Math.Clamp(coordinate, 0, dimension - 1);
        }

        var period = 1 << mask;
        var value = ((coordinate % period) + period) % period;
        var periodIndex = coordinate / period;
        if (coordinate < 0 && coordinate % period != 0)
        {
            periodIndex--;
        }
        if (mirror && (periodIndex & 1) != 0)
        {
            value = period - 1 - value;
        }

        return Math.Clamp(value, 0, dimension - 1);
    }

    private static Vector4 DecodeRgba16(ushort pixel) =>
        new(
            ((pixel >> 11) & 31) / 31f,
            ((pixel >> 6) & 31) / 31f,
            ((pixel >> 1) & 31) / 31f,
            (pixel & 1) != 0 ? 1 : 0);

    private static Vector4 DecodeRgba32(uint pixel) =>
        new(
            ((pixel >> 24) & 0xFF) / 255f,
            ((pixel >> 16) & 0xFF) / 255f,
            ((pixel >> 8) & 0xFF) / 255f,
            (pixel & 0xFF) / 255f);

    private static Vector4 DecodeIntensityAlpha4(byte packed, int x)
    {
        var texel = (x & 1) == 0 ? packed >> 4 : packed & 0xF;
        var intensity = (texel >> 1) / 7f;
        return new Vector4(intensity, intensity, intensity, texel & 1);
    }

    private static Vector4 DecodeIntensityAlpha8(byte pixel)
    {
        var intensity = (pixel >> 4) / 15f;
        return new Vector4(intensity, intensity, intensity, (pixel & 0xF) / 15f);
    }

    private static Vector4 DecodeIntensityAlpha16(ushort pixel)
    {
        var intensity = (pixel >> 8) / 255f;
        return new Vector4(intensity, intensity, intensity, (pixel & 0xFF) / 255f);
    }

    private static Vector4 DecodeIntensity4(byte packed, int x)
    {
        var intensity = (x & 1) == 0 ? packed >> 4 : packed & 0xF;
        var value = intensity / 15f;
        return new Vector4(value, value, value, value);
    }

    private static Vector4 DecodeIntensity8(byte pixel)
    {
        var value = pixel / 255f;
        return new Vector4(value, value, value, value);
    }

    private bool WriteColorPixel(int x, int y, Vector4 color, Vector4 shade)
    {
        var bytesPerPixel = _colorImageSize == 2 ? 2u : 4u;
        var destination =
            _colorImageAddress + (((uint)y * (uint)_colorImageWidth + (uint)x) * bytesPerPixel);
        if (destination + bytesPerPixel > N64Memory.RdramSize)
        {
            return false;
        }

        color = Vector4.Clamp(color, Vector4.Zero, Vector4.One);
        if (!PassesAlphaCompare(x, y, color.W))
        {
            AlphaPixelsRejected++;
            return false;
        }

        // The RDP blender's colour and alpha selectors are encoded in the
        // upper half of other-mode-low. Decode those selectors instead of
        // assuming every translucent mode is a conventional source-alpha
        // blend. This is still a coverage approximation, but it preserves the
        // framebuffer-dependent shade and overlay modes used by Mario 64.
        var readsFramebuffer =
            (_otherModeLow & ForceBlend) != 0 ||
            ((_otherModeLow & ImageRead) != 0 && color.W < 0.999f);
        if (readsFramebuffer)
        {
            var existing = ReadColorPixel(destination);
            color = ApplyBlenderCycle(color, existing, shade, cycle: 0);
            if (CycleType == 1)
            {
                color = ApplyBlenderCycle(color, existing, shade, cycle: 1);
            }

            FramebufferPixelsBlended++;
        }

        var red = (uint)Math.Clamp((int)MathF.Round(color.X * 255), 0, 255);
        var green = (uint)Math.Clamp((int)MathF.Round(color.Y * 255), 0, 255);
        var blue = (uint)Math.Clamp((int)MathF.Round(color.Z * 255), 0, 255);
        var alpha = (uint)Math.Clamp((int)MathF.Round(color.W * 255), 0, 255);

        // The RDP addresses RDRAM physically; write the frame-buffer pixel
        // directly instead of routing it through virtual-address translation.
        if (_colorImageSize == 2)
        {
            var rgba5551 = (ushort)(
                ((red >> 3) << 11) |
                ((green >> 3) << 6) |
                ((blue >> 3) << 1) |
                (alpha >= 128 ? 1u : 0u));
            BinaryPrimitives.WriteUInt16BigEndian(
                _memory.Rdram.AsSpan((int)destination, 2),
                rgba5551);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(
                _memory.Rdram.AsSpan((int)destination, 4),
                (red << 24) | (green << 16) | (blue << 8) | alpha);
        }

        return true;
    }

    private bool PassesAlphaCompare(int x, int y, float alpha)
    {
        return (_otherModeLow & AlphaCompareMask) switch
        {
            0 => true,
            1 => alpha >= _blendColor.W,
            // Hardware uses an alpha dither/noise source. A stable Bayer
            // threshold keeps the coverage pattern deterministic for tests.
            2 => alpha >= BayerAlphaThreshold(x, y),
            _ => true
        };
    }

    private static float BayerAlphaThreshold(int x, int y)
    {
        ReadOnlySpan<byte> bayer =
        [
            0, 8, 2, 10,
            12, 4, 14, 6,
            3, 11, 1, 9,
            15, 7, 13, 5
        ];
        return (bayer[((y & 3) * 4) + (x & 3)] + 0.5f) / 16f;
    }

    private Vector4 ReadColorPixel(uint destination) =>
        _colorImageSize == 2
            ? DecodeRgba16(BinaryPrimitives.ReadUInt16BigEndian(
                _memory.Rdram.AsSpan((int)destination, 2)))
            : DecodeRgba32(BinaryPrimitives.ReadUInt32BigEndian(
                _memory.Rdram.AsSpan((int)destination, 4)));

    private Vector4 ApplyBlenderCycle(
        Vector4 input,
        Vector4 memory,
        Vector4 shade,
        int cycle)
    {
        var pShift = cycle == 0 ? 30 : 28;
        var aShift = cycle == 0 ? 26 : 24;
        var mShift = cycle == 0 ? 22 : 20;
        var bShift = cycle == 0 ? 18 : 16;
        var pSelector = (int)((_otherModeLow >> pShift) & 3);
        var aSelector = (int)((_otherModeLow >> aShift) & 3);
        var mSelector = (int)((_otherModeLow >> mShift) & 3);
        var bSelector = (int)((_otherModeLow >> bShift) & 3);
        var p = BlenderColor(pSelector, input, memory);
        var m = BlenderColor(mSelector, input, memory);
        var a = BlenderAlpha(aSelector, input, shade);
        var b = BlenderInverseAlpha(bSelector, a, memory);
        var denominator = a + b;
        if (denominator <= 0.000001f)
        {
            return input;
        }

        var rgb = ((new Vector3(p.X, p.Y, p.Z) * a) +
                   (new Vector3(m.X, m.Y, m.Z) * b)) / denominator;
        return Vector4.Clamp(
            new Vector4(rgb, Math.Max(input.W, memory.W)),
            Vector4.Zero,
            Vector4.One);
    }

    private Vector4 BlenderColor(int source, Vector4 input, Vector4 memory) =>
        source switch
        {
            0 => input,
            1 => memory,
            2 => _blendColor,
            3 => _fogColor,
            _ => input
        };

    private float BlenderAlpha(int source, Vector4 input, Vector4 shade) =>
        source switch
        {
            0 => input.W,
            1 => _fogColor.W,
            2 => shade.W,
            _ => 0f
        };

    private static float BlenderInverseAlpha(int source, float alpha, Vector4 memory) =>
        source switch
        {
            0 => 1f - alpha,
            1 => memory.W,
            2 => 1f,
            _ => 0f
        };

    private uint ResolveAddress(uint address)
    {
        var segment = (int)(address >> 24);
        if (segment is > 0 and < 16)
        {
            return (_segments[segment] + (address & 0x00FFFFFF)) & 0x00FFFFFF;
        }

        return _memory.TranslateVirtualAddress(address) & 0x00FFFFFF;
    }

    private void FillRectangle(uint word0, uint word1)
    {
        if (!RasterizationEnabled)
        {
            return;
        }

        if (_colorImageAddress >= N64Memory.RdramSize ||
            _colorImageWidth <= 0 ||
            _colorImageSize is not (2 or 3))
        {
            return;
        }

        var right = (int)((word0 >> 14) & 0x3FF);
        var bottom = (int)((word0 >> 2) & 0x3FF);
        var left = (int)((word1 >> 14) & 0x3FF);
        var top = (int)((word1 >> 2) & 0x3FF);
        if (right < left || bottom < top)
        {
            return;
        }

        var bytesPerPixel = _colorImageSize == 2 ? 2u : 4u;
        var clearingDepth =
            _colorImageAddress == _depthImageAddress &&
            _depthImageAddress < N64Memory.RdramSize;
        if (clearingDepth)
        {
            var remainingBytes = N64Memory.RdramSize - (int)_colorImageAddress;
            var maximumHeight = Math.Clamp(
                remainingBytes / (_colorImageWidth * (int)bytesPerPixel),
                1,
                480);
            EnsureDepthBuffer(_colorImageWidth, maximumHeight);
        }

        for (var y = Math.Max(0, top); y <= bottom; y++)
        {
            for (var x = Math.Max(0, left); x <= right && x < _colorImageWidth; x++)
            {
                var destination =
                    _colorImageAddress + (((uint)y * (uint)_colorImageWidth + (uint)x) * bytesPerPixel);
                if (destination + bytesPerPixel > N64Memory.RdramSize)
                {
                    continue;
                }

                if (clearingDepth &&
                    y < _depthBufferHeight &&
                    x < _depthBufferWidth)
                {
                    _depthBuffer[(y * _depthBufferWidth) + x] =
                        float.PositiveInfinity;
                }

                // Fill cycle writes the replicated fill register directly.
                // In one/two-cycle modes G_FILLRECT is a regular RDP span and
                // must pass through the configured combiner and blender.
                if (CycleType == 3 || !_combinerConfigured)
                {
                    if (_colorImageSize == 2)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(
                            _memory.Rdram.AsSpan((int)destination, 2),
                            (ushort)_fillColor);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(
                            _memory.Rdram.AsSpan((int)destination, 4),
                            _fillColor);
                    }
                }
                else
                {
                    var shade = _primitiveColor;
                    var color = EvaluateCombiner(shade, Vector4.Zero);
                    WriteColorPixel(x, y, color, shade);
                }
            }
        }

        FillRectanglesDrawn++;
    }

    private void EnsureDepthBuffer(int width, int height)
    {
        if (_depthBufferWidth == width &&
            _depthBufferHeight == height &&
            _depthBuffer.Length == width * height)
        {
            return;
        }

        _depthBufferWidth = width;
        _depthBufferHeight = height;
        _depthBuffer = new float[width * height];
        Array.Fill(_depthBuffer, float.PositiveInfinity);
    }

    private readonly record struct Fast3dVertex(
        Vector4 ClipPosition,
        Vector3 Position,
        Vector4 Color,
        Vector2 TextureCoordinate,
        float ReciprocalW,
        byte ClipFlags,
        bool Valid);

    private readonly record struct Fast3dTile(
        int Format,
        int Size,
        int Line,
        int Tmem,
        int Palette,
        bool ClampT,
        bool MirrorT,
        int MaskT,
        int ShiftT,
        bool ClampS,
        bool MirrorS,
        int MaskS,
        int ShiftS,
        int UpperLeftS,
        int UpperLeftT,
        int LowerRightS,
        int LowerRightT);

    private readonly record struct TextureSampleState(
        int Format,
        int Size,
        int Palette,
        int Width,
        int Height,
        int BitsPerTexel,
        int RowStrideBits,
        int BaseBitOffset,
        float UpperLeftS,
        float UpperLeftT,
        int UpperLeftRow,
        int ShiftS,
        int ShiftT,
        int MaskS,
        int MaskT,
        bool ClampS,
        bool ClampT,
        bool MirrorS,
        bool MirrorT);

    /// <summary>
    /// Records which display-list opcodes a cartridge issues. Cheap enough to
    /// leave on: one array increment per command, never per pixel.
    /// </summary>
    private readonly long[] _opcodeHistogram = new long[256];

    internal IEnumerable<(byte Opcode, long Count)> OpcodeHistogram =>
        _opcodeHistogram
            .Select((count, opcode) => (Opcode: (byte)opcode, Count: count))
            .Where(entry => entry.Count > 0)
            .OrderByDescending(entry => entry.Count);

    internal enum N64Microcode
    {
        Fast3d,
        F3dex,
        F3dex2,
        F5Rogue
    }

    private readonly record struct LoadedTexture(
        uint SourceAddress,
        int Format,
        int Size,
        int Texels,
        int Tmem,
        int Bits)
    {
        public bool Valid =>
            SourceAddress < N64Memory.RdramSize &&
            Texels > 0 &&
            Bits > 0;
    }
}
