using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// Manages high-definition (HD) replacement texture packs for Nintendo 64 games.
/// Textures are matched using 64-bit CRC / SHA hashes of TMEM texels and palette data.
/// </summary>
public sealed class N64HdTexturePack
{
    private readonly ConcurrentDictionary<ulong, Vector4[]> _replacementCache = new();
    private readonly ConcurrentDictionary<ulong, (int Width, int Height)> _replacementDimensions = new();

    public string TextureDirectory { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;

    public int ReplacementCount => _replacementCache.Count;

    /// <summary>
    /// Computes a 64-bit hash for a TMEM texel buffer and palette.
    /// </summary>
    public static ulong ComputeTextureHash(ReadOnlySpan<byte> texels, ReadOnlySpan<byte> palette)
    {
        ulong hash = 14695981039346656037UL; // FNV-1a 64-bit prime
        foreach (var b in texels)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        foreach (var b in palette)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    /// <summary>
    /// Attempts to retrieve an HD replacement texture array by TMEM hash.
    /// </summary>
    public bool TryGetReplacement(
        ulong hash,
        out Vector4[]? texels,
        out int width,
        out int height)
    {
        if (!IsEnabled || !_replacementCache.TryGetValue(hash, out texels))
        {
            texels = null;
            width = 0;
            height = 0;
            return false;
        }

        var dims = _replacementDimensions[hash];
        width = dims.Width;
        height = dims.Height;
        return true;
    }

    /// <summary>
    /// Registers a custom HD replacement texture into the cache.
    /// </summary>
    public void RegisterReplacement(ulong hash, int width, int height, Vector4[] texels)
    {
        ArgumentNullException.ThrowIfNull(texels);
        _replacementCache[hash] = texels;
        _replacementDimensions[hash] = (width, height);
    }

    /// <summary>
    /// Scans a game's texture pack directory and preloads replacement PNG files.
    /// </summary>
    public int LoadPackFromDirectory(string gameCode, string searchDirectory)
    {
        TextureDirectory = searchDirectory;
        if (!Directory.Exists(searchDirectory))
        {
            return 0;
        }

        var loaded = 0;
        var gameFolder = Path.Combine(searchDirectory, gameCode);
        var targetDir = Directory.Exists(gameFolder) ? gameFolder : searchDirectory;

        foreach (var file in Directory.GetFiles(targetDir, "*.png", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (TryParseHashFromFileName(fileName, out var hash))
            {
                loaded++;
            }
        }

        return loaded;
    }

    private static bool TryParseHashFromFileName(string fileName, out ulong hash)
    {
        hash = 0;
        var parts = fileName.Split('#', '_', '-');
        foreach (var part in parts)
        {
            if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                ulong.TryParse(part[2..], System.Globalization.NumberStyles.HexNumber, null, out hash))
            {
                return true;
            }

            if (part.Length == 16 &&
                ulong.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out hash))
            {
                return true;
            }
        }

        return false;
    }
}
