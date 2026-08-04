using System.Buffers.Binary;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// GameCube (GX / Flipper) texture formats and tiled decoder.
/// </summary>
public enum GameCubeTextureFormat : byte
{
    I4 = 0,
    I8 = 1,
    IA4 = 2,
    IA8 = 3,
    RGB565 = 4,
    RGB5A3 = 5,
    RGBA8 = 6,
    C4 = 8,
    C8 = 9,
    C14X2 = 10,
    CMPR = 14
}

/// <summary>
/// Decodes GameCube tiled texture memory into standard 32-bit RGBA/ARGB pixel buffers.
/// </summary>
public static class GameCubeTextureDecoder
{
    /// <summary>
    /// Decodes a tiled GameCube texture image into a 32-bit ARGB pixel array.
    /// </summary>
    public static void Decode(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        GameCubeTextureFormat format,
        Span<uint> destination)
    {
        switch (format)
        {
            case GameCubeTextureFormat.I4:
                DecodeI4(source, width, height, destination);
                break;
            case GameCubeTextureFormat.I8:
                DecodeI8(source, width, height, destination);
                break;
            case GameCubeTextureFormat.IA4:
                DecodeIA4(source, width, height, destination);
                break;
            case GameCubeTextureFormat.IA8:
                DecodeIA8(source, width, height, destination);
                break;
            case GameCubeTextureFormat.RGB565:
                DecodeRgb565(source, width, height, destination);
                break;
            case GameCubeTextureFormat.RGB5A3:
                DecodeRgb5a3(source, width, height, destination);
                break;
            case GameCubeTextureFormat.RGBA8:
                DecodeRgba8(source, width, height, destination);
                break;
            case GameCubeTextureFormat.CMPR:
                DecodeCmpr(source, width, height, destination);
                break;
            default:
                destination.Clear();
                break;
        }
    }

    private static void DecodeI4(ReadOnlySpan<byte> src, int width, int height, Span<uint> dst)
    {
        var srcIdx = 0;
        for (var blockY = 0; blockY < height; blockY += 8)
        {
            for (var blockX = 0; blockX < width; blockX += 8)
            {
                for (var dy = 0; dy < 8; dy++)
                {
                    for (var dx = 0; dx < 8; dx += 2)
                    {
                        if (srcIdx >= src.Length) return;
                        var b = src[srcIdx++];
                        
                        var px1 = (blockX + dx) + ((blockY + dy) * width);
                        var px2 = (blockX + dx + 1) + ((blockY + dy) * width);

                        var i1 = (b >> 4) * 17;
                        var i2 = (b & 0x0F) * 17;

                        if (blockX + dx < width && blockY + dy < height && (uint)px1 < (uint)dst.Length)
                            dst[px1] = PackRgba((byte)i1, (byte)i1, (byte)i1, 255);
                        if (blockX + dx + 1 < width && blockY + dy < height && (uint)px2 < (uint)dst.Length)
                            dst[px2] = PackRgba((byte)i2, (byte)i2, (byte)i2, 255);
                    }
                }
            }
        }
    }

    private static void DecodeI8(ReadOnlySpan<byte> src, int width, int height, Span<uint> dst)
    {
        var srcIdx = 0;
        for (var blockY = 0; blockY < height; blockY += 4)
        {
            for (var blockX = 0; blockX < width; blockX += 8)
            {
                for (var dy = 0; dy < 4; dy++)
                {
                    for (var dx = 0; dx < 8; dx++)
                    {
                        if (srcIdx >= src.Length) return;
                        var i = src[srcIdx++];
                        var px = (blockX + dx) + ((blockY + dy) * width);

                        if (blockX + dx < width && blockY + dy < height)
                            dst[px] = PackRgba(i, i, i, 255);
                    }
                }
            }
        }
    }

    private static void DecodeIA4(ReadOnlySpan<byte> src, int width, int height, Span<uint> dst)
    {
        var srcIdx = 0;
        for (var blockY = 0; blockY < height; blockY += 4)
        {
            for (var blockX = 0; blockX < width; blockX += 8)
            {
                for (var dy = 0; dy < 4; dy++)
                {
                    for (var dx = 0; dx < 8; dx++)
                    {
                        if (srcIdx >= src.Length) return;
                        var b = src[srcIdx++];
                        var a = (byte)((b >> 4) * 17);
                        var i = (byte)((b & 0x0F) * 17);
                        var px = (blockX + dx) + ((blockY + dy) * width);

                        if (blockX + dx < width && blockY + dy < height)
                            dst[px] = PackRgba(i, i, i, a);
                    }
                }
            }
        }
    }

    private static void DecodeIA8(ReadOnlySpan<byte> src, int width, int height, Span<uint> dst)
    {
        var srcIdx = 0;
        for (var blockY = 0; blockY < height; blockY += 4)
        {
            for (var blockX = 0; blockX < width; blockX += 4)
            {
                for (var dy = 0; dy < 4; dy++)
                {
                    for (var dx = 0; dx < 4; dx++)
                    {
                        if (srcIdx + 1 >= src.Length) return;
                        var a = src[srcIdx++];
                        var i = src[srcIdx++];
                        var px = (blockX + dx) + ((blockY + dy) * width);

                        if (blockX + dx < width && blockY + dy < height)
                            dst[px] = PackRgba(i, i, i, a);
                    }
                }
            }
        }
    }

    private static void DecodeRgb565(ReadOnlySpan<byte> src, int width, int height, Span<uint> dst)
    {
        var srcIdx = 0;
        for (var blockY = 0; blockY < height; blockY += 4)
        {
            for (var blockX = 0; blockX < width; blockX += 4)
            {
                for (var dy = 0; dy < 4; dy++)
                {
                    for (var dx = 0; dx < 4; dx++)
                    {
                        if (srcIdx + 1 >= src.Length) return;
                        ushort value = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(srcIdx));
                        srcIdx += 2;

                        var r = (byte)(((value >> 11) & 0x1F) * 255 / 31);
                        var g = (byte)(((value >> 5) & 0x3F) * 255 / 63);
                        var b = (byte)((value & 0x1F) * 255 / 31);
                        var px = (blockX + dx) + ((blockY + dy) * width);

                        if (blockX + dx < width && blockY + dy < height)
                            dst[px] = PackRgba(r, g, b, 255);
                    }
                }
            }
        }
    }

    private static void DecodeRgb5a3(ReadOnlySpan<byte> src, int width, int height, Span<uint> dst)
    {
        var srcIdx = 0;
        for (var blockY = 0; blockY < height; blockY += 4)
        {
            for (var blockX = 0; blockX < width; blockX += 4)
            {
                for (var dy = 0; dy < 4; dy++)
                {
                    for (var dx = 0; dx < 4; dx++)
                    {
                        if (srcIdx + 1 >= src.Length) return;
                        ushort val = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(srcIdx));
                        srcIdx += 2;

                        byte r, g, b, a;
                        if ((val & 0x8000) != 0)
                        {
                            // RGB555 (opaque)
                            a = 255;
                            r = (byte)(((val >> 10) & 0x1F) * 255 / 31);
                            g = (byte)(((val >> 5) & 0x1F) * 255 / 31);
                            b = (byte)((val & 0x1F) * 255 / 31);
                        }
                        else
                        {
                            // ARGB3444
                            a = (byte)(((val >> 12) & 0x07) * 255 / 7);
                            r = (byte)(((val >> 8) & 0x0F) * 255 / 15);
                            g = (byte)(((val >> 4) & 0x0F) * 255 / 15);
                            b = (byte)((val & 0x0F) * 255 / 15);
                        }

                        var px = (blockX + dx) + ((blockY + dy) * width);
                        if (blockX + dx < width && blockY + dy < height)
                            dst[px] = PackRgba(r, g, b, a);
                    }
                }
            }
        }
    }

    private static void DecodeRgba8(ReadOnlySpan<byte> src, int width, int height, Span<uint> dst)
    {
        // RGBA8 uses two 32-byte sub-blocks per 4x4 tile: AR tile first, GB tile second.
        var srcIdx = 0;
        for (var blockY = 0; blockY < height; blockY += 4)
        {
            for (var blockX = 0; blockX < width; blockX += 4)
            {
                if (srcIdx + 63 >= src.Length) return;

                var arBlock = src.Slice(srcIdx, 32);
                var gbBlock = src.Slice(srcIdx + 32, 32);
                srcIdx += 64;

                var arIdx = 0;
                var gbIdx = 0;

                for (var dy = 0; dy < 4; dy++)
                {
                    for (var dx = 0; dx < 4; dx++)
                    {
                        var a = arBlock[arIdx++];
                        var r = arBlock[arIdx++];
                        var g = gbBlock[gbIdx++];
                        var b = gbBlock[gbIdx++];

                        var px = (blockX + dx) + ((blockY + dy) * width);
                        if (blockX + dx < width && blockY + dy < height)
                            dst[px] = PackRgba(r, g, b, a);
                    }
                }
            }
        }
    }

    private static void DecodeCmpr(ReadOnlySpan<byte> src, int width, int height, Span<uint> dst)
    {
        // CMPR (DXT1 compressed) consists of 8x8 blocks, divided into four 4x4 sub-blocks in Z-order.
        var srcIdx = 0;
        var palette = new uint[4];

        for (var blockY = 0; blockY < height; blockY += 8)
        {
            for (var blockX = 0; blockX < width; blockX += 8)
            {
                // Sub-block 0: top-left (0..3, 0..3)
                // Sub-block 1: top-right (4..7, 0..3)
                // Sub-block 2: bottom-left (0..3, 4..7)
                // Sub-block 3: bottom-right (4..7, 4..7)
                for (var sub = 0; sub < 4; sub++)
                {
                    if (srcIdx + 7 >= src.Length) return;

                    var subX = blockX + ((sub & 1) != 0 ? 4 : 0);
                    var subY = blockY + ((sub & 2) != 0 ? 4 : 0);

                    ushort c0 = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(srcIdx));
                    ushort c1 = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(srcIdx + 2));
                    srcIdx += 4;

                    var r0 = (byte)(((c0 >> 11) & 0x1F) * 255 / 31);
                    var g0 = (byte)(((c0 >> 5) & 0x3F) * 255 / 63);
                    var b0 = (byte)((c0 & 0x1F) * 255 / 31);

                    var r1 = (byte)(((c1 >> 11) & 0x1F) * 255 / 31);
                    var g1 = (byte)(((c1 >> 5) & 0x3F) * 255 / 63);
                    var b1 = (byte)((c1 & 0x1F) * 255 / 31);

                    palette[0] = PackRgba(r0, g0, b0, 255);
                    palette[1] = PackRgba(r1, g1, b1, 255);

                    if (c0 > c1)
                    {
                        palette[2] = PackRgba((byte)((2 * r0 + r1) / 3), (byte)((2 * g0 + g1) / 3), (byte)((2 * b0 + b1) / 3), 255);
                        palette[3] = PackRgba((byte)((r0 + 2 * r1) / 3), (byte)((g0 + 2 * g1) / 3), (byte)((b0 + 2 * b1) / 3), 255);
                    }
                    else
                    {
                        palette[2] = PackRgba((byte)((r0 + r1) / 2), (byte)((g0 + g1) / 2), (byte)((b0 + b1) / 2), 255);
                        palette[3] = PackRgba(0, 0, 0, 0); // Transparent
                    }

                    uint bits = BinaryPrimitives.ReadUInt32BigEndian(src.Slice(srcIdx));
                    srcIdx += 4;

                    for (var dy = 0; dy < 4; dy++)
                    {
                        for (var dx = 0; dx < 4; dx++)
                        {
                            var shift = 30 - ((dy * 4 + dx) * 2);
                            var colorIdx = (int)((bits >> shift) & 0x03);

                            var px = (subX + dx) + ((subY + dy) * width);
                            if (subX + dx < width && subY + dy < height)
                                dst[px] = palette[colorIdx];
                        }
                    }
                }
            }
        }
    }

    private static uint PackRgba(byte r, byte g, byte b, byte a)
    {
        return (uint)((a << 24) | (r << 16) | (g << 8) | b);
    }
}
