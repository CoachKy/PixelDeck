using System.Buffers.Binary;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Parser and block reader for Compact ISO (.ciso) GameCube disc images.
/// </summary>
public sealed class CisoDiscImage
{
    private const uint MagicCiso = 0x4349534F; // 'CISO'
    private const int HeaderSize = 0x8000;
    private const int BlockSize = 0x8000;

    private readonly byte[] _blockMap;
    private readonly Stream _stream;

    public CisoDiscImage(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;

        Span<byte> header = stackalloc byte[0x20];
        if (stream.Read(header) < 0x20)
        {
            throw new InvalidDataException("CISO file is too small for a header.");
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0, 4));
        if (magic != MagicCiso)
        {
            throw new InvalidDataException($"Invalid CISO magic: 0x{magic:X8}");
        }

        _blockMap = new byte[HeaderSize - 0x20];
        stream.ReadExactly(_blockMap);
    }

    /// <summary>
    /// Checks whether an uncompressed disc offset is present in the CISO block map.
    /// </summary>
    public bool IsBlockPresent(long uncompressedOffset)
    {
        var blockIdx = (int)(uncompressedOffset / BlockSize);
        return blockIdx >= 0 && blockIdx < _blockMap.Length && _blockMap[blockIdx] != 0;
    }
}
