using PixelDeck.Emulation.N64;

namespace PixelDeck.App.Tests;

/// <summary>
/// Helpers shared by the Nintendo 64 test classes: locating locally installed
/// cartridges, building synthetic cartridge images, and driving a machine
/// through title screens.
/// </summary>
internal static class N64TestSupport
{
    /// <summary>
    /// Enumerates Nintendo 64 images under the local Games folder. Returns
    /// empty when none are installed, which is the normal case on CI.
    /// </summary>
    public static IEnumerable<string> FindCartridges()
    {
        var configured = Environment.GetEnvironmentVariable("PIXELDECK_GAMES_FOLDER");
        var gamesFolder = string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Games"))
            : Path.GetFullPath(configured);
        var nintendo64Folder = Path.Combine(gamesFolder, "Nintendo64");
        return Directory.Exists(nintendo64Folder)
            ? Directory.EnumerateFiles(nintendo64Folder, "*", SearchOption.AllDirectories)
                .Where(IsCartridgeImage)
            : [];
    }

    public static string? FindSuperMario64() =>
        FindCartridges().FirstOrDefault(path =>
        {
            try
            {
                return N64Cartridge.Inspect(path).IsSuperMario64UsRevision0;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        });

    /// <summary>
    /// A minimal big-endian cartridge image that passes header validation, so
    /// tests can exercise the machine without a commercial ROM.
    /// </summary>
    public static byte[] CreateCartridgeImage(string title = "PIXEL64 TEST")
    {
        var image = new byte[0x2000];
        image[0] = 0x80;
        image[1] = 0x37;
        image[2] = 0x12;
        image[3] = 0x40;
        WriteUInt32(image, 0x08, 0x80000400);
        var padded = title.PadRight(20).AsSpan(0, 20);
        for (var index = 0; index < 20; index++)
        {
            image[0x20 + index] = (byte)padded[index];
        }

        image[0x3B] = (byte)'N';
        image[0x3C] = (byte)'P';
        image[0x3D] = (byte)'X';
        image[0x3E] = (byte)'E';
        return image;
    }

    /// <summary>
    /// Re-orders a canonical big-endian image into one of the other two dump
    /// formats found in the wild.
    /// </summary>
    public static byte[] ConvertByteOrder(byte[] canonical, N64ImageByteOrder byteOrder)
    {
        var converted = canonical.ToArray();
        switch (byteOrder)
        {
            case N64ImageByteOrder.ByteSwapped:
                for (var offset = 0; offset < converted.Length; offset += 2)
                {
                    (converted[offset], converted[offset + 1]) =
                        (converted[offset + 1], converted[offset]);
                }

                break;
            case N64ImageByteOrder.LittleEndian:
                for (var offset = 0; offset < converted.Length; offset += 4)
                {
                    (converted[offset], converted[offset + 3]) =
                        (converted[offset + 3], converted[offset]);
                    (converted[offset + 1], converted[offset + 2]) =
                        (converted[offset + 2], converted[offset + 1]);
                }

                break;
        }

        return converted;
    }

    /// <summary>
    /// Alternating Start and A presses walk title screens, file selects, and
    /// cutscenes without depending on any game's exact frame timings, which
    /// proved far too fragile to hard-code.
    /// </summary>
    public static N64ControllerState WalkTitleScreens(int field) =>
        (field % 200) switch
        {
            >= 20 and < 40 => new N64ControllerState(N64Button.Start, 0, 0),
            >= 120 and < 140 => new N64ControllerState(N64Button.A, 0, 0),
            _ => N64ControllerState.Neutral
        };

    public static void WriteUInt32(byte[] destination, int offset, uint value)
    {
        destination[offset] = (byte)(value >> 24);
        destination[offset + 1] = (byte)(value >> 16);
        destination[offset + 2] = (byte)(value >> 8);
        destination[offset + 3] = (byte)value;
    }

    private static bool IsCartridgeImage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".z64", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".n64", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".v64", StringComparison.OrdinalIgnoreCase);
    }
}
