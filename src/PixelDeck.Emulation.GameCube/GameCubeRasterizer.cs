namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// The embedded framebuffer, software texture sampler, and triangle rasteriser.
/// </summary>
public sealed class GameCubeRasterizer
{
    public const int Width = 640;
    public const int Height = 528;

    private const float ViewportBias = 342f;

    private readonly uint[] _colour = new uint[Width * Height];
    private readonly float[] _depth = new float[Width * Height];
    private readonly uint[] _transform = new uint[0x1100];

    // Texture slot 0 state
    private uint[]? _currentTexturePixels;
    private int _textureWidth;
    private int _textureHeight;
    private bool _hasTexture;

    public enum Culling
    {
        None = 0,
        Back = 1,
        Front = 2,
        All = 3
    }

    public Culling Cull { get; set; }

    public bool HasContent { get; private set; }
    public long TrianglesDrawn { get; private set; }

    public GameCubeTevPipeline TevPipeline { get; } = new();

    public GameCubeRasterizer() => Clear(0, 0, 0);

    /// <summary>
    /// Loads a texture into the rasteriser's primary sampler slot.
    /// </summary>
    public void SetTexture(ReadOnlySpan<byte> source, int width, int height, GameCubeTextureFormat format)
    {
        if (width <= 0 || height <= 0 || source.IsEmpty)
        {
            _hasTexture = false;
            _currentTexturePixels = null;
            return;
        }

        _textureWidth = width;
        _textureHeight = height;
        _currentTexturePixels = new uint[width * height];
        GameCubeTextureDecoder.Decode(source, width, height, format, _currentTexturePixels);
        _hasTexture = true;
    }

    /// <summary>Clears bound texture slot.</summary>
    public void ClearTexture()
    {
        _hasTexture = false;
        _currentTexturePixels = null;
    }

    public void SetTransformRegister(uint address, uint value)
    {
        if (address < _transform.Length)
        {
            _transform[address] = value;
        }
    }

    private float TransformFloat(uint address) =>
        address < (uint)_transform.Length ? BitConverter.UInt32BitsToSingle(_transform[address]) : 0f;

    private uint SampleTexture(float u, float v)
    {
        if (!_hasTexture || _currentTexturePixels is null) return 0xFFFFFFFFu;

        // Wrap S/T (repeat)
        u -= MathF.Floor(u);
        v -= MathF.Floor(v);

        var tx = Math.Clamp((int)(u * _textureWidth), 0, _textureWidth - 1);
        var ty = Math.Clamp((int)(v * _textureHeight), 0, _textureHeight - 1);
        var idx = (ty * _textureWidth) + tx;

        return (uint)idx < (uint)_currentTexturePixels.Length ? _currentTexturePixels[idx] : 0xFFFFFFFFu;
    }

    public void Clear(byte red, byte green, byte blue)
    {
        var packed = 0xFF00_0000u | ((uint)red << 16) | ((uint)green << 8) | blue;
        _colour.AsSpan().Fill(packed);
        _depth.AsSpan().Fill(float.MaxValue);
        HasContent = false;
    }

    public uint Pixel(int x, int y) => _colour[(y * Width) + x];

    public void Draw(int primitive, IReadOnlyList<Vertex> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        if (vertices.Count < 3)
        {
            return;
        }

        var screen = new ScreenVertex[vertices.Count];
        for (var index = 0; index < vertices.Count; index++)
        {
            screen[index] = ToScreen(vertices[index]);
        }

        switch (primitive)
        {
            case 0: // quads
            case 1:
                for (var i = 0; i + 3 < screen.Length; i += 4)
                {
                    FillTriangle(screen[i], screen[i + 1], screen[i + 2]);
                    FillTriangle(screen[i], screen[i + 2], screen[i + 3]);
                }
                break;

            case 2: // triangles
                for (var i = 0; i + 2 < screen.Length; i += 3)
                {
                    FillTriangle(screen[i], screen[i + 1], screen[i + 2]);
                }
                break;

            case 3: // triangle strip
                for (var i = 2; i < screen.Length; i++)
                {
                    if ((i & 1) == 0)
                        FillTriangle(screen[i - 2], screen[i - 1], screen[i]);
                    else
                        FillTriangle(screen[i - 1], screen[i - 2], screen[i]);
                }
                break;

            case 4: // triangle fan
                for (var i = 2; i < screen.Length; i++)
                {
                    FillTriangle(screen[0], screen[i - 1], screen[i]);
                }
                break;

            default:
                return;
        }
    }

    private ScreenVertex ToScreen(Vertex vertex)
    {
        var x = (TransformFloat(0) * vertex.X) + (TransformFloat(1) * vertex.Y) +
                (TransformFloat(2) * vertex.Z) + TransformFloat(3);
        var y = (TransformFloat(4) * vertex.X) + (TransformFloat(5) * vertex.Y) +
                (TransformFloat(6) * vertex.Z) + TransformFloat(7);
        var z = (TransformFloat(8) * vertex.X) + (TransformFloat(9) * vertex.Y) +
                (TransformFloat(10) * vertex.Z) + TransformFloat(11);

        var a = TransformFloat(0x1020);
        var b = TransformFloat(0x1021);
        var c = TransformFloat(0x1022);
        var d = TransformFloat(0x1023);
        var e = TransformFloat(0x1024);
        var f = TransformFloat(0x1025);
        var orthographic = _transform[0x1026] != 0;

        float clipX, clipY, clipZ, clipW;
        if (orthographic)
        {
            clipX = (a * x) + b;
            clipY = (c * y) + d;
            clipZ = (e * z) + f;
            clipW = 1f;
        }
        else
        {
            clipX = (a * x) + (b * z);
            clipY = (c * y) + (d * z);
            clipZ = (e * z) + f;
            clipW = -z;
        }

        if (MathF.Abs(clipW) < 1e-6f)
        {
            clipW = clipW < 0 ? -1e-6f : 1e-6f;
        }

        var inverse = 1f / clipW;
        var normalisedX = clipX * inverse;
        var normalisedY = clipY * inverse;
        var normalisedZ = clipZ * inverse;

        return new ScreenVertex(
            (normalisedX * TransformFloat(0x101A)) + (TransformFloat(0x101D) - ViewportBias),
            (normalisedY * TransformFloat(0x101B)) + (TransformFloat(0x101E) - ViewportBias),
            (normalisedZ * TransformFloat(0x101C)) + TransformFloat(0x101F),
            inverse,
            vertex.U * inverse,
            vertex.V * inverse,
            vertex.Colour);
    }

    private void FillTriangle(ScreenVertex a, ScreenVertex b, ScreenVertex c)
    {
        var area = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
        if (MathF.Abs(area) < 1e-6f)
        {
            return;
        }

        switch (Cull)
        {
            case Culling.All:
                return;
            case Culling.Back when area < 0:
            case Culling.Front when area > 0:
                return;
        }

        var left = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        var right = Math.Min(Width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        var top = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        var bottom = Math.Min(Height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        if (left > right || top > bottom)
        {
            return;
        }

        var inverseArea = 1f / area;
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var px = x + 0.5f;
                var py = y + 0.5f;

                var w0 = (((b.X - a.X) * (py - a.Y)) - ((b.Y - a.Y) * (px - a.X))) * inverseArea;
                var w1 = (((c.X - b.X) * (py - b.Y)) - ((c.Y - b.Y) * (px - b.X))) * inverseArea;
                var w2 = (((a.X - c.X) * (py - c.Y)) - ((a.Y - c.Y) * (px - c.X))) * inverseArea;
                if (w0 < 0 || w1 < 0 || w2 < 0)
                {
                    continue;
                }

                var depth = (w1 * a.Z) + (w2 * b.Z) + (w0 * c.Z);
                var offset = (y * Width) + x;
                if (depth >= _depth[offset])
                {
                    continue;
                }

                _depth[offset] = depth;

                var vertexColor = BlendColor(a.Colour, b.Colour, c.Colour, w1, w2, w0);
                
                if (_hasTexture && _currentTexturePixels is not null)
                {
                    var invW = (w1 * a.W) + (w2 * b.W) + (w0 * c.W);
                    var w = invW > 0 ? 1f / invW : 1f;
                    var u = ((w1 * a.U) + (w2 * b.U) + (w0 * c.U)) * w;
                    var v = ((w1 * a.V) + (w2 * b.V) + (w0 * c.V)) * w;

                    var texColor = SampleTexture(u, v);
                    _colour[offset] = TevPipeline.Evaluate(texColor, vertexColor);
                }
                else
                {
                    _colour[offset] = TevPipeline.Evaluate(0xFFFFFFFFu, vertexColor);
                }

                HasContent = true;
            }
        }

        TrianglesDrawn++;
    }

    private static uint CombineTev(uint texColor, uint vertexColor)
    {
        // Standard TEV Modulate stage: Color = (Texture * Vertex) / 255
        var tr = (texColor >> 16) & 0xFF;
        var tg = (texColor >> 8) & 0xFF;
        var tb = texColor & 0xFF;
        var ta = (texColor >> 24) & 0xFF;

        var vr = (vertexColor >> 16) & 0xFF;
        var vg = (vertexColor >> 8) & 0xFF;
        var vb = vertexColor & 0xFF;
        var va = (vertexColor >> 24) & 0xFF;

        var r = (byte)((tr * vr) / 255);
        var g = (byte)((tg * vg) / 255);
        var b = (byte)((tb * vb) / 255);
        var a = (byte)((ta * va) / 255);

        return (uint)((a << 24) | (r << 16) | (g << 8) | b);
    }

    private static uint BlendColor(uint a, uint b, uint c, float wa, float wb, float wc)
    {
        var red = (byte)Math.Clamp(
            (((a >> 16) & 0xFF) * wa) + (((b >> 16) & 0xFF) * wb) + (((c >> 16) & 0xFF) * wc),
            0,
            255);
        var green = (byte)Math.Clamp(
            (((a >> 8) & 0xFF) * wa) + (((b >> 8) & 0xFF) * wb) + (((c >> 8) & 0xFF) * wc),
            0,
            255);
        var blue = (byte)Math.Clamp(
            ((a & 0xFF) * wa) + ((b & 0xFF) * wb) + ((c & 0xFF) * wc),
            0,
            255);

        return 0xFF00_0000u | ((uint)red << 16) | ((uint)green << 8) | blue;
    }

    public readonly record struct Vertex(float X, float Y, float Z, uint Colour, float U = 0f, float V = 0f);

    private readonly record struct ScreenVertex(float X, float Y, float Z, float W, float U, float V, uint Colour);
}
