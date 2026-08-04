using System.Buffers.Binary;

namespace PixelDeck.Emulation.GameCube;

public enum VertexAttributeFormat : byte
{
    None = 0,
    Direct = 1,
    Index8 = 2,
    Index16 = 3
}

public enum ComponentType : byte
{
    UByte = 0,
    SByte = 1,
    UShort = 2,
    SShort = 3,
    Float = 4
}

/// <summary>
/// Decodes vertex attributes (Positions, Normals, Colors, TexCoords) from GameCube GX command FIFO buffers.
/// </summary>
public static class GameCubeVertexDecoder
{
    public static int GetComponentByteSize(ComponentType type) => type switch
    {
        ComponentType.UByte => 1,
        ComponentType.SByte => 1,
        ComponentType.UShort => 2,
        ComponentType.SShort => 2,
        ComponentType.Float => 4,
        _ => 1
    };

    public static float ReadComponent(ReadOnlySpan<byte> data, ref int offset, ComponentType type, int scale = 0)
    {
        float value = 0f;
        switch (type)
        {
            case ComponentType.UByte:
                value = data[offset++];
                break;
            case ComponentType.SByte:
                value = (sbyte)data[offset++];
                break;
            case ComponentType.UShort:
                value = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
                offset += 2;
                break;
            case ComponentType.SShort:
                value = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2));
                offset += 2;
                break;
            case ComponentType.Float:
                value = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4)));
                offset += 4;
                return value;
        }

        return scale > 0 ? value / (1 << scale) : value;
    }
}
