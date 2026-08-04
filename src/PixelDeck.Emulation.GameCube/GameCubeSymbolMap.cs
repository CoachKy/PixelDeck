namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Symbol map parser for GameCube function maps (.map files).
/// </summary>
public sealed class GameCubeSymbolMap
{
    private readonly Dictionary<uint, string> _symbols = [];

    public int SymbolCount => _symbols.Count;

    /// <summary>
    /// Parses a standard Dolphin / CodeWarrior text symbol map.
    /// Format per line: Address (hex) Size (hex) Alignment (hex) Name
    /// Example: 80003100 00000040 00000000 main
    /// </summary>
    public void LoadMap(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 4 &&
                uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var address))
            {
                var name = string.Join(' ', parts[3..]);
                _symbols[address] = name;
            }
        }
    }

    public bool TryGetSymbol(uint address, out string name) => _symbols.TryGetValue(address, out name!);

    public string Resolve(uint address) =>
        _symbols.TryGetValue(address, out var name) ? $"{name} (0x{address:X8})" : $"0x{address:X8}";
}
