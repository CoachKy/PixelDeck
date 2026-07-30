using System.Numerics;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// Lowers one already-clipped HLE triangle into the native 44-word RDP
/// shade/texture/depth packet consumed by paraLLEl-RDP. The fixed-point setup
/// follows the MIT-licensed triangle converter and command builder in the
/// pinned paraLLEl-RDP revision.
/// </summary>
internal static class N64RdpTriangleEncoder
{
    private const int SubpixelBits = 2;
    private const int SubpixelScale = 1 << SubpixelBits;
    private const byte ShadeTextureDepthTriangle = 0x0F;

    internal static bool TryEncode(
        N64RdpHleVertex first,
        N64RdpHleVertex second,
        N64RdpHleVertex third,
        int tile,
        int maximumMipLevel,
        out N64RdpCommand? command)
    {
        Span<N64RdpHleVertex> vertices = stackalloc N64RdpHleVertex[3];
        vertices[0] = first;
        vertices[1] = second;
        vertices[2] = third;

        Span<short> xs = stackalloc short[3];
        Span<short> ys = stackalloc short[3];
        for (var index = 0; index < vertices.Length; index++)
        {
            xs[index] = QuantizeCoordinate(vertices[index].X);
            ys[index] = QuantizeCoordinate(vertices[index].Y);
        }

        var indexA = 0;
        var indexB = 1;
        var indexC = 2;
        SortByYThenX(ref indexA, ref indexB, ys, xs);
        SortByYThenX(ref indexB, ref indexC, ys, xs);
        SortByYThenX(ref indexA, ref indexB, ys, xs);

        var yLow = ys[indexA];
        var yMiddle = ys[indexB];
        var yHigh = ys[indexC];
        var xA = xs[indexA];
        var xB = xs[indexB];
        var xC = xs[indexC];

        var originalAbX = xs[1] - xs[0];
        var originalAbY = ys[1] - ys[0];
        var originalBcX = xs[2] - xs[1];
        var originalBcY = ys[2] - ys[1];
        var signedArea =
            (originalAbX * originalBcY) - (originalAbY * originalBcX);
        if (signedArea == 0)
        {
            command = null;
            return false;
        }

        var xMajor = xA << (16 - SubpixelBits);
        var xUpper = xMajor;
        var xLower = xB << (16 - SubpixelBits);
        var dxdyMajor = RoundAwayFromZeroDivide(
            (xC - xA) << 16,
            Math.Max(1, yHigh - yLow)) & ~7;
        var dxdyUpper = RoundAwayFromZeroDivide(
            (xB - xA) << 16,
            Math.Max(1, yMiddle - yLow)) & ~7;
        var dxdyLower = RoundAwayFromZeroDivide(
            (xC - xB) << 16,
            Math.Max(1, yHigh - yMiddle)) & ~7;

        var subpixelY = yLow & (SubpixelScale - 1);
        xMajor -= (dxdyMajor >> SubpixelBits) * subpixelY;
        xUpper -= (dxdyUpper >> SubpixelBits) * subpixelY;
        var rightMajor = dxdyUpper < dxdyMajor;

        var abX = xB - xA;
        var bcX = xC - xB;
        var caX = xA - xC;
        var abY = yMiddle - yLow;
        var bcY = yHigh - yMiddle;
        var caY = yLow - yHigh;
        signedArea = (abX * bcY) - (abY * bcX);
        if (signedArea == 0)
        {
            command = null;
            return false;
        }

        var inverseArea = (double)SubpixelScale / signedArea;
        var majorSlope = (double)dxdyMajor / 0x10000;
        var yFraction = (double)(yLow & (SubpixelScale - 1)) / SubpixelScale;

        Span<int> color = stackalloc int[4];
        Span<int> colorDx = stackalloc int[4];
        Span<int> colorDe = stackalloc int[4];
        Span<int> colorDy = stackalloc int[4];
        for (var component = 0; component < 4; component++)
        {
            var valueA = GetColor(vertices[indexA].Color, component);
            var valueB = GetColor(vertices[indexB].Color, component);
            var valueC = GetColor(vertices[indexC].Color, component);
            var dx = -inverseArea *
                ((abY * valueC) + (caY * valueB) + (bcY * valueA));
            var dy = inverseArea *
                ((abX * valueC) + (caX * valueB) + (bcX * valueA));
            var de = dy + (dx * majorSlope);
            var value = valueA - (yFraction * de);

            color[component] = QuantizeColor(value);
            colorDx[component] = QuantizeColor(dx);
            colorDe[component] = QuantizeColor(de);
            colorDy[component] = QuantizeColor(dy);
        }

        var u = ComputeAttribute(
            vertices[indexA].U,
            vertices[indexB].U,
            vertices[indexC].U,
            abX,
            abY,
            bcX,
            bcY,
            caX,
            caY,
            inverseArea,
            majorSlope,
            yFraction,
            QuantizeUv);
        var v = ComputeAttribute(
            vertices[indexA].V,
            vertices[indexB].V,
            vertices[indexC].V,
            abX,
            abY,
            bcX,
            bcY,
            caX,
            caY,
            inverseArea,
            majorSlope,
            yFraction,
            QuantizeUv);
        var w = ComputeAttribute(
            vertices[indexA].W,
            vertices[indexB].W,
            vertices[indexC].W,
            abX,
            abY,
            bcX,
            bcY,
            caX,
            caY,
            inverseArea,
            majorSlope,
            yFraction,
            QuantizeW);
        var z = ComputeAttribute(
            vertices[indexA].Z,
            vertices[indexB].Z,
            vertices[indexC].Z,
            abX,
            abY,
            bcX,
            bcY,
            caX,
            caY,
            inverseArea,
            majorSlope,
            yFraction,
            QuantizeZ);

        var words = new uint[44];
        words[0] =
            ((uint)ShadeTextureDepthTriangle << 24) |
            ((uint)(maximumMipLevel & 7) << 19) |
            ((uint)(tile & 7) << 16) |
            Mask(yHigh, 14);
        if (!rightMajor)
        {
            words[0] |= 1u << 23;
        }

        words[1] = (Mask(yMiddle, 14) << 16) | Mask(yLow, 14);
        words[2] = Mask(xLower, 28);
        words[3] = Mask(dxdyLower, 30);
        words[4] = Mask(xMajor, 28);
        // The full sign bit is significant for attribute interpolation.
        words[5] = unchecked((uint)dxdyMajor);
        words[6] = Mask(xUpper, 28);
        words[7] = Mask(dxdyUpper, 30);

        PackFourComponentAttribute(
            words,
            8,
            color,
            colorDx,
            colorDe,
            colorDy);
        PackTextureAttribute(words, 24, u, v, w);
        words[40] = unchecked((uint)z.Value);
        words[41] = unchecked((uint)z.Dx);
        words[42] = unchecked((uint)z.De);
        words[43] = unchecked((uint)z.Dy);

        command = new N64RdpCommand(words);
        return true;
    }

    private static void PackFourComponentAttribute(
        Span<uint> words,
        int offset,
        ReadOnlySpan<int> value,
        ReadOnlySpan<int> dx,
        ReadOnlySpan<int> de,
        ReadOnlySpan<int> dy)
    {
        PackFourComponents(words, offset, value);
        PackFourComponents(words, offset + 2, dx);
        PackFourComponents(words, offset + 8, de);
        PackFourComponents(words, offset + 10, dy);
    }

    private static void PackFourComponents(
        Span<uint> words,
        int offset,
        ReadOnlySpan<int> values)
    {
        words[offset] =
            (unchecked((uint)values[0]) & 0xFFFF0000u) |
            ((unchecked((uint)values[1]) >> 16) & 0xFFFFu);
        words[offset + 1] =
            (unchecked((uint)values[2]) & 0xFFFF0000u) |
            ((unchecked((uint)values[3]) >> 16) & 0xFFFFu);
        words[offset + 4] =
            ((unchecked((uint)values[0]) << 16) & 0xFFFF0000u) |
            (unchecked((uint)values[1]) & 0xFFFFu);
        words[offset + 5] =
            ((unchecked((uint)values[2]) << 16) & 0xFFFF0000u) |
            (unchecked((uint)values[3]) & 0xFFFFu);
    }

    private static void PackTextureAttribute(
        Span<uint> words,
        int offset,
        FixedAttribute u,
        FixedAttribute v,
        FixedAttribute w)
    {
        PackTextureComponents(words, offset, u.Value, v.Value, w.Value);
        PackTextureComponents(words, offset + 2, u.Dx, v.Dx, w.Dx);
        PackTextureComponents(words, offset + 8, u.De, v.De, w.De);
        PackTextureComponents(words, offset + 10, u.Dy, v.Dy, w.Dy);
    }

    private static void PackTextureComponents(
        Span<uint> words,
        int offset,
        int first,
        int second,
        int third)
    {
        words[offset] =
            (unchecked((uint)first) & 0xFFFF0000u) |
            ((unchecked((uint)second) >> 16) & 0xFFFFu);
        words[offset + 1] = unchecked((uint)third) & 0xFFFF0000u;
        words[offset + 4] =
            ((unchecked((uint)first) << 16) & 0xFFFF0000u) |
            (unchecked((uint)second) & 0xFFFFu);
        words[offset + 5] =
            (unchecked((uint)third) << 16) & 0xFFFF0000u;
    }

    private static FixedAttribute ComputeAttribute(
        double valueA,
        double valueB,
        double valueC,
        int abX,
        int abY,
        int bcX,
        int bcY,
        int caX,
        int caY,
        double inverseArea,
        double majorSlope,
        double yFraction,
        Func<double, int> quantize)
    {
        var dx = -inverseArea *
            ((abY * valueC) + (caY * valueB) + (bcY * valueA));
        var dy = inverseArea *
            ((abX * valueC) + (caX * valueB) + (bcX * valueA));
        var de = dy + (dx * majorSlope);
        var value = valueA - (yFraction * de);
        return new FixedAttribute(
            quantize(value),
            quantize(dx),
            quantize(de),
            quantize(dy));
    }

    private static void SortByYThenX(
        ref int first,
        ref int second,
        ReadOnlySpan<short> ys,
        ReadOnlySpan<short> xs)
    {
        if (ys[second] < ys[first] ||
            (ys[second] == ys[first] && xs[second] < xs[first]))
        {
            (first, second) = (second, first);
        }
    }

    private static short QuantizeCoordinate(float value)
    {
        var scaled = Math.Round(
            value * SubpixelScale,
            MidpointRounding.AwayFromZero);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    private static int QuantizeColor(double value) =>
        QuantizeInt32(value * 255.0 * 65536.0);

    private static int QuantizeUv(double value) =>
        QuantizeInt32(value * 64.0 * 65536.0);

    private static int QuantizeW(double value) =>
        QuantizeInt32(value * 4294967296.0);

    private static int QuantizeZ(double value) =>
        QuantizeInt32(value * ((1 << 18) - 1) * (1 << 13));

    private static int QuantizeInt32(double value)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded <= int.MinValue)
        {
            return int.MinValue;
        }

        return rounded >= int.MaxValue ? int.MaxValue : (int)rounded;
    }

    private static int RoundAwayFromZeroDivide(int numerator, int denominator)
    {
        var rounding = denominator - 1;
        if (numerator < 0)
        {
            numerator -= rounding;
        }
        else if (numerator > 0)
        {
            numerator += rounding;
        }

        return numerator / denominator;
    }

    private static float GetColor(Vector4 color, int component) =>
        component switch
        {
            0 => color.X,
            1 => color.Y,
            2 => color.Z,
            3 => color.W,
            _ => throw new ArgumentOutOfRangeException(nameof(component))
        };

    private static uint Mask(int value, int bits) =>
        unchecked((uint)value) & ((1u << bits) - 1u);

    private readonly record struct FixedAttribute(
        int Value,
        int Dx,
        int De,
        int Dy);
}

internal readonly record struct N64RdpHleVertex(
    float X,
    float Y,
    float Z,
    float U,
    float V,
    float W,
    Vector4 Color);
