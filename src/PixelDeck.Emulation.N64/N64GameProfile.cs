namespace PixelDeck.Emulation.N64;

/// <summary>
/// Per-game cartridge profile containing save hardware configuration and CIC override.
/// All games run dynamically on the 100% LLE SIMD Hardware Engine.
/// </summary>
public sealed record N64GameProfile(
    string GameCode,
    string Title,
    N64SaveType SaveType = N64SaveType.None,
    N64Cic CicOverride = N64Cic.Unknown,
    int CountPerOp = 2);

/// <summary>
/// Registry of verified per-title save hardware profiles keyed by 4-character Game Code.
/// </summary>
public static class N64GameProfileRegistry
{
    private static readonly Dictionary<string, N64GameProfile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NSME"] = new("NSME", "Super Mario 64", N64SaveType.Eeprom4Kbit, N64Cic.Cic6102),
        ["NPXE"] = new("NPXE", "Pixel64 Test", N64SaveType.Eeprom4Kbit, N64Cic.Cic6102),
        ["NKTE"] = new("NKTE", "Mario Kart 64", N64SaveType.Eeprom4Kbit, N64Cic.Cic6102),
        ["CZLE"] = new("CZLE", "The Legend of Zelda: Ocarina of Time", N64SaveType.Sram256Kbit, N64Cic.Cic6105, CountPerOp: 1),
        ["NDOE"] = new("NDOE", "Donkey Kong 64", N64SaveType.Eeprom16Kbit, N64Cic.Cic6105, CountPerOp: 1),
        ["NRSE"] = new("NRSE", "Star Wars: Rogue Squadron", N64SaveType.Eeprom4Kbit, N64Cic.Cic6102),
        ["NSWE"] = new("NSWE", "Star Wars: Shadows of the Empire", N64SaveType.Eeprom4Kbit, N64Cic.Cic6102),
        ["NKGE"] = new("NKGE", "Major League Baseball featuring Ken Griffey Jr.", N64SaveType.Eeprom4Kbit, N64Cic.Cic6102),
        ["NGXE"] = new("NGXE", "Gauntlet Legends", N64SaveType.Eeprom4Kbit, N64Cic.Cic6102, CountPerOp: 1),
        ["CLBE"] = new("CLBE", "Mario Party", N64SaveType.Eeprom4Kbit, N64Cic.Cic6102),
        ["NMFE"] = new("NMFE", "Mario Golf", N64SaveType.Sram256Kbit, N64Cic.Cic6102),
        ["NM8E"] = new("NM8E", "Mario Tennis", N64SaveType.Eeprom16Kbit, N64Cic.Cic6102),
        ["NFXE"] = new("NFXE", "Star Fox 64", N64SaveType.Eeprom4Kbit, N64Cic.Cic6102),
        ["NPWE"] = new("NPWE", "Pilotwings 64", N64SaveType.Eeprom4Kbit, N64Cic.Cic6102),
        ["NETE"] = new("NETE", "Quest 64", N64SaveType.Eeprom4Kbit, N64Cic.Cic6102),
        ["NWXE"] = new("NWXE", "WWF WrestleMania 2000", N64SaveType.Sram256Kbit, N64Cic.Cic6102),
        ["NGME"] = new("NGME", "Goemon's Great Adventure", N64SaveType.Eeprom4Kbit, N64Cic.Cic6102),
        ["NG5E"] = new("NG5E", "Mystical Ninja Starring Goemon", N64SaveType.Eeprom4Kbit, N64Cic.Cic6102),
        ["NGEE"] = new("NGEE", "GoldenEye 007", N64SaveType.Eeprom4Kbit, N64Cic.Cic6102),
        ["NPDE"] = new("NPDE", "Perfect Dark", N64SaveType.Eeprom16Kbit, N64Cic.Cic6105, CountPerOp: 1),
        ["NMQE"] = new("NMQE", "Paper Mario", N64SaveType.FlashRam1Mbit, N64Cic.Cic6102, CountPerOp: 1),
        ["NZSE"] = new("NZSE", "The Legend of Zelda: Majora's Mask", N64SaveType.FlashRam1Mbit, N64Cic.Cic6105, CountPerOp: 1),
    };

    /// <summary>
    /// Looks up a verified compatibility profile for the given cartridge, or returns a default profile.
    /// </summary>
    public static N64GameProfile LookupProfile(N64Cartridge cartridge)
    {
        if (Profiles.TryGetValue(cartridge.GameCode, out var profile))
        {
            return profile;
        }

        return new N64GameProfile(cartridge.GameCode, cartridge.Title, cartridge.SaveType, cartridge.Cic);
    }
}
