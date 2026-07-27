using System.Buffers.Binary;
using System.Numerics;

namespace PixelDeck.Emulation.N64;

public sealed class Fast3dRenderer
{
    private const int MaximumCommandsPerTask = 250_000;
    private const int MaximumDisplayListDepth = 32;

    private readonly N64Memory _memory;
    private readonly uint[] _segments = new uint[16];
    private readonly Dictionary<byte, long> _unsupportedCommandCounts = new();
    private readonly Fast3dVertex[] _vertices = new Fast3dVertex[16];
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
    private Matrix4x4 _projection = Matrix4x4.Identity;
    private Vector4 _viewportScale = new(160, 120, 511, 0);
    private Vector4 _viewportTranslate = new(160, 120, 0, 0);
    private uint _geometryMode;
    private bool _textureEnabled;
    private int _textureTile;
    private float _textureScaleS = 1;
    private float _textureScaleT = 1;
    private uint _textureImageAddress;
    private int _textureImageFormat;
    private int _textureImageSize;
    private int _textureImageWidth = 1;
    private bool _combinerUsesTexture;
    private Vector4 _primitiveColor = Vector4.One;
    private uint _otherModeLow;
    private uint _otherModeHigh;

    public Fast3dRenderer(N64Memory memory)
    {
        _memory = memory;
    }

    public long CommandsProcessed { get; private set; }

    public long DisplayListsProcessed { get; private set; }

    public long FillRectanglesDrawn { get; private set; }

    public long TrianglesDrawn { get; private set; }

    public long VerticesTransformed { get; private set; }

    public long TexturedPixelsDrawn { get; private set; }

    public long TextureRectanglesDrawn { get; private set; }

    public long DepthPixelsRejected { get; private set; }

    public long TriviallyClippedTriangles { get; private set; }

    public float MaximumTriangleWidth { get; private set; }

    public float MaximumTriangleHeight { get; private set; }

    public uint ColorImageAddress => _colorImageAddress;

    public int ColorImageWidth => _colorImageWidth;

    public int ColorImageSize => _colorImageSize;

    internal uint OtherModeLow => _otherModeLow;

    internal uint OtherModeHigh => _otherModeHigh;

    /// <summary>RDP cycle type from other-mode-high bits 20-21 (2 = COPY).</summary>
    private uint CycleType => (_otherModeHigh >> 20) & 3;

    public long UnsupportedCommands { get; private set; }

    public IReadOnlyDictionary<byte, long> UnsupportedCommandCounts => _unsupportedCommandCounts;

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
        _textureScaleS = 1;
        _textureScaleT = 1;
        _textureImageAddress = 0;
        _textureImageFormat = 0;
        _textureImageSize = 0;
        _textureImageWidth = 1;
        _primitiveColor = Vector4.One;
        var remainingBudget = MaximumCommandsPerTask;
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
            switch (opcode)
            {
                case 0x01: // F3D G_MTX
                    LoadMatrix(word0, word1);
                    break;
                case 0x03: // F3D G_MOVEMEM
                    MoveMemory(word0, word1);
                    break;
                case 0x04: // F3D G_VTX
                    LoadVertices(word0, word1);
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
                case 0xBF: // F3D G_TRI1
                    DrawTriangle(word1);
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
                    FillRectangle(word0, word1);
                    break;
                case 0xF7: // G_SETFILLCOLOR
                    _fillColor = word1;
                    break;
                case 0xF2: // G_SETTILESIZE
                    SetTileSize(word0, word1);
                    break;
                case 0xF3: // G_LOADBLOCK
                    LoadTextureBlock(word0, word1);
                    break;
                case 0xF5: // G_SETTILE
                    SetTile(word0, word1);
                    break;
                case 0xFD: // G_SETTIMG
                    SetTextureImage(word0, word1);
                    break;
                case 0xFC: // G_SETCOMBINE
                    _combinerUsesTexture = CombineUsesTexture(word0, word1);
                    break;
                case 0xFE: // G_SETZIMG
                    _depthImageAddress = ResolveAddress(word1);
                    break;
                case 0xFF: // G_SETCIMG
                    _colorImageSize = (int)((word0 >> 19) & 3);
                    _colorImageWidth = (int)(word0 & 0xFFF) + 1;
                    _colorImageAddress = ResolveAddress(word1);
                    break;
                case 0xB9: // G_SETOTHERMODE_L
                    SetOtherModeLow(word0, word1);
                    break;
                case 0xBA: // G_SETOTHERMODE_H
                    SetOtherModeHigh(word0, word1);
                    break;
                case 0xF0: // G_LOADTLUT
                    LoadTextureLookupTable(word0, word1);
                    break;
                case 0x00:
                case 0xE7:
                case 0xE6:
                case 0xE8:
                case 0xE9:
                case 0xED:
                case 0xFB:
                case 0xB4:
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
                    DrawTextureRectangle(word0, word1, halfOne, halfTwo, opcode == 0xE5);
                    break;
                }
                case 0xF9: // G_SETBLENDCOLOR
                    break;
                case 0xFA: // G_SETPRIMCOLOR
                    _primitiveColor = DecodeRgba32(word1);
                    break;
                default:
                    UnsupportedCommands++;
                    _unsupportedCommandCounts[opcode] =
                        _unsupportedCommandCounts.GetValueOrDefault(opcode) + 1;
                    break;
            }
        }
    }

    private void SetTexture(uint word0, uint word1)
    {
        _textureEnabled = (word0 & 0xFF) != 0;
        _textureTile = (int)((word0 >> 8) & 7);
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
        CopyRdramBitsToTmem(
            _textureImageAddress,
            sourceBitOffset,
            destinationBitOffset,
            bitCount);
        _loadedTextures[tmem] = new LoadedTexture(
            _textureImageAddress,
            _textureImageFormat,
            _textureImageSize,
            texels,
            tmem,
            bitCount);
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
        const int segmentMoveWordIndex = 0x06;
        var index = (int)(word0 & 0xFF);
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
        var index = (int)((word0 >> 16) & 0xFF);
        if (index != viewportIndex)
        {
            return;
        }

        var address = ResolveAddress(word1);
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

    private void LoadMatrix(uint word0, uint word1)
    {
        const int projectionFlag = 1;
        const int loadFlag = 2;
        const int pushFlag = 4;
        var parameters = (int)((word0 >> 16) & 0xFF);
        var incoming = ReadMatrix(ResolveAddress(word1));
        if ((parameters & projectionFlag) != 0)
        {
            _projection = (parameters & loadFlag) != 0
                ? incoming
                : Matrix4x4.Multiply(_projection, incoming);
            return;
        }

        var current = _modelViewStack.Peek();
        var result = (parameters & loadFlag) != 0
            ? incoming
            : Matrix4x4.Multiply(current, incoming);
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
        var count = (parameters >> 4) + 1;
        var destination = parameters & 0xF;
        var address = ResolveAddress(word1);
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
                    ? Vector4.One
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

    private void DrawTriangle(uint word1)
    {
        var first = (int)((word1 >> 16) & 0xFF) / 10;
        var second = (int)((word1 >> 8) & 0xFF) / 10;
        var third = (int)(word1 & 0xFF) / 10;
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

        var minX = Math.Clamp(rawMinX, 0, _colorImageWidth - 1);
        var maxX = Math.Clamp(rawMaxX, 0, _colorImageWidth - 1);
        var minY = Math.Clamp(rawMinY, 0, maximumHeight - 1);
        var maxY = Math.Clamp(rawMaxY, 0, maximumHeight - 1);
        var inverseArea = 1f / area;
        const uint zCompare = 0x10;
        const uint zUpdate = 0x20;
        var hasDepth =
            (_geometryMode & 1) != 0 &&
            _depthImageAddress < N64Memory.RdramSize;
        var compareDepth = hasDepth && (_otherModeLow & zCompare) != 0;
        var updateDepth = hasDepth && (_otherModeLow & zUpdate) != 0;
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
                var depth =
                    (a.Position.Z * weightA) +
                    (b.Position.Z * weightB) +
                    (c.Position.Z * weightC);
                if (compareDepth && depth > _depthBuffer[depthIndex])
                {
                    DepthPixelsRejected++;
                    continue;
                }

                var color =
                    (a.Color * weightA) + (b.Color * weightB) + (c.Color * weightC);
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
                        var textureColor = SampleTexture(textureCoordinate);
                        if (textureColor.W <= 0)
                        {
                            continue;
                        }

                        color *= textureColor;
                        TexturedPixelsDrawn++;
                    }
                }

                if (updateDepth)
                {
                    _depthBuffer[depthIndex] = depth;
                }

                WriteColorPixel(x, y, color);
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

    private void DrawTextureRectangle(
        uint word0,
        uint word1,
        uint textureOrigin,
        uint textureStep,
        bool flip)
    {
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
        for (var y = firstY; y <= lastY; y++)
        {
            for (var x = firstX; x <= lastX; x++)
            {
                var deltaX = x - left;
                var deltaY = y - top;
                var textureCoordinate = flip
                    ? new Vector2(startS + (deltaY * stepS), startT + (deltaX * stepT))
                    : new Vector2(startS + (deltaX * stepS), startT + (deltaY * stepT));
                var color = SampleTexture(textureCoordinate, tileIndex);
                if (color.W <= 0)
                {
                    continue;
                }

                WriteColorPixel(x, y, color * _primitiveColor);
                TexturedPixelsDrawn++;
            }
        }

        TextureRectanglesDrawn++;
    }

    private Vector4 SampleTexture(Vector2 textureCoordinate, int? selectedTile = null)
    {
        var tileIndex = Math.Clamp(selectedTile ?? _textureTile, 0, _tiles.Length - 1);
        var tile = _tiles[tileIndex];
        var width = Math.Max(1, ((tile.LowerRightS - tile.UpperLeftS) >> 2) + 1);
        var height = Math.Max(1, ((tile.LowerRightT - tile.UpperLeftT) >> 2) + 1);
        var s = ApplyTextureShift(textureCoordinate.X - (tile.UpperLeftS / 4f), tile.ShiftS);
        var t = ApplyTextureShift(textureCoordinate.Y - (tile.UpperLeftT / 4f), tile.ShiftT);
        var x = ApplyTextureAddressing((int)MathF.Floor(s), width, tile.MaskS, tile.ClampS, tile.MirrorS);
        var y = ApplyTextureAddressing((int)MathF.Floor(t), height, tile.MaskT, tile.ClampT, tile.MirrorT);

        var format = tile.Format;
        var size = tile.Size;
        var texel = (y * width) + x;
        var bitOffset = (tile.Tmem * 64) + (texel * BitsPerTexel(size));
        return (format, size) switch
        {
            (0, 2) => DecodeRgba16(ReadTmemUInt16(bitOffset >> 3)),
            (0, 3) => DecodeRgba32(ReadTmemUInt32(bitOffset >> 3)),
            (2, 0) => DecodePaletteTexel(
                tile.Palette * 16 +
                ((x & 1) == 0
                    ? ReadTmemByte(bitOffset >> 3) >> 4
                    : ReadTmemByte(bitOffset >> 3) & 0xF)),
            (2, 1) => DecodePaletteTexel(ReadTmemByte(bitOffset >> 3)),
            (3, 1) => DecodeIntensityAlpha8(ReadTmemByte(bitOffset >> 3)),
            (3, 2) => DecodeIntensityAlpha16(ReadTmemUInt16(bitOffset >> 3)),
            (4, 0) => DecodeIntensity4(
                ReadTmemByte(bitOffset >> 3),
                x),
            (4, 1) => DecodeIntensity8(ReadTmemByte(bitOffset >> 3)),
            _ => Vector4.One
        };
    }

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
        if (clamp || mask == 0)
        {
            return Math.Clamp(coordinate, 0, dimension - 1);
        }

        var period = 1 << mask;
        var value = ((coordinate % period) + period) % period;
        if (mirror && ((coordinate / period) & 1) != 0)
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

    private void WriteColorPixel(int x, int y, Vector4 color)
    {
        var bytesPerPixel = _colorImageSize == 2 ? 2u : 4u;
        var destination =
            _colorImageAddress + (((uint)y * (uint)_colorImageWidth + (uint)x) * bytesPerPixel);
        if (destination + bytesPerPixel > N64Memory.RdramSize)
        {
            return;
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
    }

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
