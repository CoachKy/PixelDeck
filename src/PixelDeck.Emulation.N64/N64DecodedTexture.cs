namespace PixelDeck.Emulation.N64;

/// <summary>
/// One texture as the renderer actually decoded it, with the tile state that
/// produced it.
/// </summary>
/// <remarks>
/// Exists because texture faults are otherwise invisible: a texture that
/// decodes to flat white and a surface that was never textured look identical
/// on screen. Being able to look at the decoded texels turns those into
/// different, obvious pictures.
/// </remarks>
public sealed record N64DecodedTexture(
    int Format,
    int Size,
    int Palette,
    int Width,
    int Height,
    int BitsPerTexel,
    int BaseBitOffset,
    int TextureLutMode,
    long SampleCount,
    byte[] Rgba)
{
    /// <summary>Human-readable texel format, e.g. "CI 8bit".</summary>
    public string FormatName =>
        $"{Format switch { 0 => "RGBA", 1 => "YUV", 2 => "CI", 3 => "IA", 4 => "I", _ => $"fmt{Format}" }} " +
        $"{Size switch { 0 => "4bit", 1 => "8bit", 2 => "16bit", 3 => "32bit", _ => "?" }}";

    /// <summary>
    /// True when every texel is opaque white. That is what an unmodelled
    /// format used to resolve to, and what a failed decode still tends to look
    /// like, so it is worth calling out explicitly.
    /// </summary>
    public bool IsUniformWhite
    {
        get
        {
            for (var index = 0; index < Rgba.Length; index++)
            {
                if (Rgba[index] != 0xFF)
                {
                    return false;
                }
            }

            return Rgba.Length > 0;
        }
    }
}
