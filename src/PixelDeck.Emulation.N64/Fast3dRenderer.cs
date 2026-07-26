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
    private readonly Stack<Matrix4x4> _modelViewStack = new();
    private uint _colorImageAddress;
    private int _colorImageWidth = 320;
    private int _colorImageSize;
    private uint _fillColor;
    private Matrix4x4 _projection = Matrix4x4.Identity;
    private Vector4 _viewportScale = new(160, 120, 511, 0);
    private Vector4 _viewportTranslate = new(160, 120, 0, 0);

    public Fast3dRenderer(N64Memory memory)
    {
        _memory = memory;
    }

    public long CommandsProcessed { get; private set; }

    public long DisplayListsProcessed { get; private set; }

    public long FillRectanglesDrawn { get; private set; }

    public long TrianglesDrawn { get; private set; }

    public long VerticesTransformed { get; private set; }

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
                case 0xFF: // G_SETCIMG
                    _colorImageSize = (int)((word0 >> 19) & 3);
                    _colorImageWidth = (int)(word0 & 0xFFF) + 1;
                    _colorImageAddress = ResolveAddress(word1);
                    break;
                case 0x00:
                case 0xB6:
                case 0xB7:
                case 0xB9:
                case 0xBA:
                case 0xBB:
                case 0xE7:
                case 0xE6:
                case 0xE8:
                case 0xE9:
                case 0xED:
                case 0xF2:
                case 0xF3:
                case 0xF5:
                case 0xFB:
                case 0xFD:
                case 0xFC:
                case 0xFE:
                case 0xB4:
                    break;
                default:
                    UnsupportedCommands++;
                    _unsupportedCommandCounts[opcode] =
                        _unsupportedCommandCounts.GetValueOrDefault(opcode) + 1;
                    break;
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
            var inverseW = Math.Abs(clip.W) > 0.000001f ? 1f / clip.W : 0;
            var screen = new Vector3(
                (clip.X * inverseW * _viewportScale.X) + _viewportTranslate.X,
                (clip.Y * inverseW * _viewportScale.Y) + _viewportTranslate.Y,
                (clip.Z * inverseW * _viewportScale.Z) + _viewportTranslate.Z);
            _vertices[destination + index] = new Fast3dVertex(
                screen,
                new Vector4(
                    _memory.ReadByte(vertexAddress + 12) / 255f,
                    _memory.ReadByte(vertexAddress + 13) / 255f,
                    _memory.ReadByte(vertexAddress + 14) / 255f,
                    _memory.ReadByte(vertexAddress + 15) / 255f),
                clip.W > 0.000001f);
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

        RasterizeTriangle(a, b, c);
    }

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
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var sampleX = x + 0.5f;
                var sampleY = y + 0.5f;
                var weightA = Edge(b.Position, c.Position, sampleX, sampleY) * inverseArea;
                var weightB = Edge(c.Position, a.Position, sampleX, sampleY) * inverseArea;
                var weightC = 1f - weightA - weightB;
                if (weightA < 0 || weightB < 0 || weightC < 0)
                {
                    continue;
                }

                WriteColorPixel(
                    x,
                    y,
                    (a.Color * weightA) + (b.Color * weightB) + (c.Color * weightC));
            }
        }

        TrianglesDrawn++;
    }

    private static float Edge(Vector3 a, Vector3 b, float x, float y) =>
        ((x - a.X) * (b.Y - a.Y)) - ((y - a.Y) * (b.X - a.X));

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
        if (_colorImageSize == 2)
        {
            var rgba5551 = (ushort)(
                ((red >> 3) << 11) |
                ((green >> 3) << 6) |
                ((blue >> 3) << 1) |
                (alpha >= 128 ? 1u : 0u));
            _memory.WriteUInt16(destination, rgba5551);
        }
        else
        {
            _memory.WriteUInt32(destination, (red << 24) | (green << 16) | (blue << 8) | alpha);
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

                if (_colorImageSize == 2)
                {
                    _memory.WriteUInt16(destination, (ushort)_fillColor);
                }
                else
                {
                    _memory.WriteUInt32(destination, _fillColor);
                }
            }
        }

        FillRectanglesDrawn++;
    }

    private readonly record struct Fast3dVertex(Vector3 Position, Vector4 Color, bool Valid);
}
