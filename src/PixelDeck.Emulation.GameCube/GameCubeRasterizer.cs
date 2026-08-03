namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// The embedded framebuffer and the triangle rasteriser that fills it.
/// </summary>
/// <remarks>
/// <para>
/// This is the part of the graphics processor that turns geometry into pixels.
/// A game hands the transform unit a matrix, a projection and a viewport, then
/// streams vertices through the command processor; each triangle is transformed
/// into screen space, filled, and depth tested against everything drawn before
/// it. What comes out lives in the embedded framebuffer until a copy moves it
/// to the external one, which is what the video interface actually shows.
/// </para>
/// <para>
/// Deliberately not here yet: texturing and the texture environment stages that
/// combine them, which is the other half of how a GameCube frame gets its
/// colour. Triangles are filled from their vertex colours instead. That is a
/// real picture rather than a placeholder — geometry, depth and shape are all
/// correct — and it is the foundation the rest is built on.
/// </para>
/// </remarks>
public sealed class GameCubeRasterizer
{
    /// <summary>
    /// The embedded framebuffer is 640 by 528 at most, which is what a game
    /// gets to draw into before copying a region of it out.
    /// </summary>
    public const int Width = 640;
    public const int Height = 528;

    /// <summary>
    /// Screen coordinates arrive biased by this much, and the bias has to be
    /// removed. It is a property of the hardware's viewport registers rather
    /// than of any particular game.
    /// </summary>
    private const float ViewportBias = 342f;

    private readonly uint[] _colour = new uint[Width * Height];
    private readonly float[] _depth = new float[Width * Height];

    /// <summary>The transform unit's registers, as raw words.</summary>
    private readonly uint[] _transform = new uint[0x1100];

    /// <summary>Whether anything has been drawn since the last copy.</summary>
    public bool HasContent { get; private set; }

    public long TrianglesDrawn { get; private set; }

    public GameCubeRasterizer() => Clear(0, 0, 0);

    /// <summary>
    /// Which primitives to discard by their winding, and whether anything is
    /// being discarded at all.
    /// </summary>
    /// <remarks>
    /// Drawing both windings shows the inside of every closed object as well as
    /// the outside, and with a depth buffer the inside frequently wins. Culling
    /// is not an optimisation here — it is what makes a solid object look
    /// solid.
    /// </remarks>
    public enum Culling
    {
        None = 0,
        Back = 1,
        Front = 2,
        All = 3
    }

    public Culling Cull { get; set; }

    /// <summary>Stores a word written to the transform unit.</summary>
    public void SetTransformRegister(uint address, uint value)
    {
        if (address < _transform.Length)
        {
            _transform[address] = value;
        }
    }

    private float TransformFloat(uint address) =>
        BitConverter.UInt32BitsToSingle(_transform[address]);

    /// <summary>Fills the whole framebuffer and resets depth.</summary>
    public void Clear(byte red, byte green, byte blue)
    {
        var packed = 0xFF00_0000u | ((uint)red << 16) | ((uint)green << 8) | blue;
        _colour.AsSpan().Fill(packed);
        _depth.AsSpan().Fill(float.MaxValue);
        HasContent = false;
    }

    /// <summary>Reads a pixel back, for the copy to the external framebuffer.</summary>
    public uint Pixel(int x, int y) => _colour[(y * Width) + x];

    /// <summary>
    /// Draws a run of vertices as one of the primitive kinds the command
    /// processor decodes.
    /// </summary>
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

        // The eight primitive kinds reduce to triangles in four ways: a list of
        // separate triangles, a strip sharing two vertices with the last, a fan
        // sharing the first, and quads which are two triangles each.
        switch (primitive)
        {
            case 0: // quads
            case 1: // quads, second form
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
                    // Winding alternates along a strip, so every other triangle
                    // is emitted with two of its vertices swapped.
                    if ((i & 1) == 0)
                    {
                        FillTriangle(screen[i - 2], screen[i - 1], screen[i]);
                    }
                    else
                    {
                        FillTriangle(screen[i - 1], screen[i - 2], screen[i]);
                    }
                }

                break;

            case 4: // triangle fan
                for (var i = 2; i < screen.Length; i++)
                {
                    FillTriangle(screen[0], screen[i - 1], screen[i]);
                }

                break;

            default:
                // Lines and points contribute no filled area.
                return;
        }
    }

    /// <summary>
    /// Puts one vertex through the position matrix, the projection and the
    /// viewport, ending in pixels.
    /// </summary>
    private ScreenVertex ToScreen(Vertex vertex)
    {
        // Position matrix: three rows of four, applied to the model position.
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

        // Six values rather than a full matrix, laid out differently for the
        // two projection kinds: perspective divides by view depth, orthographic
        // does not.
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
            vertex.Colour);
    }

    /// <summary>
    /// Fills a triangle, testing depth per pixel.
    /// </summary>
    /// <remarks>
    /// Half-space edge functions rather than scanline stepping: the sign of
    /// three cross products says whether a point is inside, and the same three
    /// values normalise into the weights used to blend the vertices. Both
    /// windings are accepted, because back-face culling belongs to a register
    /// this does not read yet and dropping half the triangles silently would be
    /// worse than drawing them.
    /// </remarks>
    private void FillTriangle(ScreenVertex a, ScreenVertex b, ScreenVertex c)
    {
        var area = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
        if (MathF.Abs(area) < 1e-6f)
        {
            return;
        }

        // Winding decides which face this is, and the sign of the area is the
        // winding. Discarding the wrong half is worse than discarding neither,
        // so this follows the register rather than a guess.
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
                _colour[offset] = Blend(a.Colour, b.Colour, c.Colour, w1, w2, w0);
                HasContent = true;
            }
        }

        TrianglesDrawn++;
    }

    private static uint Blend(uint a, uint b, uint c, float wa, float wb, float wc)
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

    /// <summary>A vertex as it arrives from the command stream.</summary>
    public readonly record struct Vertex(float X, float Y, float Z, uint Colour);

    private readonly record struct ScreenVertex(float X, float Y, float Z, float W, uint Colour);
}
