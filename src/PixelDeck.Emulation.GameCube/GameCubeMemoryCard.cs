using System.Text;
using System.Buffers.Binary;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Parser and manager for Nintendo GameCube Memory Card (.gci) save files.
/// </summary>
public sealed class GameCubeMemoryCardFile
{
    public const int HeaderSize = 0x40;

    public string GameCode { get; set; } = string.Empty;
    public string MakerCode { get; set; } = string.Empty;
    public byte ImageFlags { get; set; }
    public string FileName { get; set; } = string.Empty;
    public uint ModificationTime { get; set; }
    public uint ImageOffset { get; set; }
    public ushort IconFormat { get; set; }
    public ushort AnimationSpeed { get; set; }
    public byte Permissions { get; set; }
    public byte CopyCount { get; set; }
    public ushort BlockCount { get; set; }
    public string CommentTitle { get; set; } = string.Empty;
    public string CommentSubtitle { get; set; } = string.Empty;
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public static GameCubeMemoryCardFile Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize)
        {
            throw new ArgumentException("Buffer is too small for a valid GCI header.", nameof(bytes));
        }

        var file = new GameCubeMemoryCardFile
        {
            GameCode = Encoding.ASCII.GetString(bytes.Slice(0, 4)).TrimEnd('\0'),
            MakerCode = Encoding.ASCII.GetString(bytes.Slice(4, 2)).TrimEnd('\0'),
            ImageFlags = bytes[7],
            FileName = Encoding.ASCII.GetString(bytes.Slice(8, 32)).TrimEnd('\0'),
            ModificationTime = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(40, 4)),
            ImageOffset = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(44, 4)),
            IconFormat = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(48, 2)),
            AnimationSpeed = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(50, 2)),
            Permissions = bytes[52],
            CopyCount = bytes[53],
            BlockCount = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(58, 2))
        };

        var payloadSize = bytes.Length - HeaderSize;
        file.Data = new byte[payloadSize];
        bytes.Slice(HeaderSize).CopyTo(file.Data);

        return file;
    }

    public byte[] Export()
    {
        var buffer = new byte[HeaderSize + Data.Length];
        
        Encoding.ASCII.GetBytes(GameCode.PadRight(4, '\0')).AsSpan(0, 4).CopyTo(buffer.AsSpan(0, 4));
        Encoding.ASCII.GetBytes(MakerCode.PadRight(2, '\0')).AsSpan(0, 2).CopyTo(buffer.AsSpan(4, 2));
        buffer[7] = ImageFlags;
        Encoding.ASCII.GetBytes(FileName.PadRight(32, '\0')).AsSpan(0, 32).CopyTo(buffer.AsSpan(8, 32));
        
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(40, 4), ModificationTime);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(44, 4), ImageOffset);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(48, 2), IconFormat);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(50, 2), AnimationSpeed);
        buffer[52] = Permissions;
        buffer[53] = CopyCount;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(58, 2), BlockCount);

        Data.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }
}
