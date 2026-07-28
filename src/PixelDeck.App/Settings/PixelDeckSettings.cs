using System.Text.Json;
using System.Text.Json.Serialization;
using PixelDeck.App.Input;
using PixelDeck.Emulation.Nes;

namespace PixelDeck.App.Settings;

public sealed class PixelDeckSettings
{
    public int ControllerIndex { get; set; }

    public int PlayerTwoControllerIndex { get; set; } = 1;

    /// <summary>Nintendo 64 port 3. Only the N64 core reads ports beyond two.</summary>
    public int PlayerThreeControllerIndex { get; set; } = 2;

    /// <summary>Nintendo 64 port 4.</summary>
    public int PlayerFourControllerIndex { get; set; } = 3;

    public GamepadButton AButton { get; set; } = GamepadButton.A;

    public GamepadButton BButton { get; set; } = GamepadButton.X;

    public GamepadButton StartButton { get; set; } = GamepadButton.Start;

    public GamepadButton SelectButton { get; set; } = GamepadButton.Back;

    public GamepadButton PlayerTwoAButton { get; set; } = GamepadButton.A;

    public GamepadButton PlayerTwoBButton { get; set; } = GamepadButton.X;

    public GamepadButton PlayerTwoStartButton { get; set; } = GamepadButton.Start;

    public GamepadButton PlayerTwoSelectButton { get; set; } = GamepadButton.Back;

    public bool RemoveNesSpriteLimit { get; set; } = true;

    public bool HideNesHorizontalOverscan { get; set; } = true;

    public Mmc3IrqRevision Mmc3IrqRevision { get; set; } = Mmc3IrqRevision.Auto;

    public NesPpuRevision NesPpuRevision { get; set; } = NesPpuRevision.Rp2C02G;

    public bool EnableNesOamDecay { get; set; }

    public NesOamCorruptionMode NesOamCorruptionMode { get; set; } =
        NesOamCorruptionMode.StableCpuPpuAlignment;

    public GamepadButton SnesAButton { get; set; } = GamepadButton.B;

    public GamepadButton SnesBButton { get; set; } = GamepadButton.A;

    public GamepadButton SnesXButton { get; set; } = GamepadButton.Y;

    public GamepadButton SnesYButton { get; set; } = GamepadButton.X;

    public GamepadButton SnesLButton { get; set; } = GamepadButton.LeftShoulder;

    public GamepadButton SnesRButton { get; set; } = GamepadButton.RightShoulder;

    public GamepadButton SnesStartButton { get; set; } = GamepadButton.Start;

    public GamepadButton SnesSelectButton { get; set; } = GamepadButton.Back;

    public GamepadButton PlayerTwoSnesAButton { get; set; } = GamepadButton.B;

    public GamepadButton PlayerTwoSnesBButton { get; set; } = GamepadButton.A;

    public GamepadButton PlayerTwoSnesXButton { get; set; } = GamepadButton.Y;

    public GamepadButton PlayerTwoSnesYButton { get; set; } = GamepadButton.X;

    public GamepadButton PlayerTwoSnesLButton { get; set; } = GamepadButton.LeftShoulder;

    public GamepadButton PlayerTwoSnesRButton { get; set; } = GamepadButton.RightShoulder;

    public GamepadButton PlayerTwoSnesStartButton { get; set; } = GamepadButton.Start;

    public GamepadButton PlayerTwoSnesSelectButton { get; set; } = GamepadButton.Back;

    /// <summary>
    /// Nintendo 64 mappings, one entry per controller port. Unlike the two-player NES and SNES
    /// blocks above this is indexed rather than flattened, because four ports times ten buttons
    /// would be forty properties.
    /// </summary>
    /// <remarks>
    /// Settable rather than get-only: System.Text.Json replaces a settable collection but appends
    /// to a get-only one, which would double up these seeded defaults. Seeding here keeps a
    /// directly constructed instance immediately usable; <see cref="PixelDeckSettingsStore"/>
    /// still repairs a stored list of the wrong length.
    /// </remarks>
    public List<N64ButtonMap> N64Ports { get; set; } = CreateDefaultN64Ports();

    internal static List<N64ButtonMap> CreateDefaultN64Ports() =>
        [.. Enumerable.Range(0, N64ButtonMap.PortCount).Select(static _ => new N64ButtonMap())];
}

/// <summary>
/// Per-port Nintendo 64 button mapping. The analog stick and D-pad are fixed, as is the right
/// stick's role as an analog stand-in for the C buttons.
/// </summary>
public sealed class N64ButtonMap
{
    public const int PortCount = 4;

    public GamepadButton A { get; set; } = GamepadButton.A;

    public GamepadButton B { get; set; } = GamepadButton.X;

    public GamepadButton Start { get; set; } = GamepadButton.Start;

    public GamepadButton Z { get; set; } = GamepadButton.LeftTrigger;

    public GamepadButton L { get; set; } = GamepadButton.LeftShoulder;

    public GamepadButton R { get; set; } = GamepadButton.RightShoulder;

    public GamepadButton CUp { get; set; } = GamepadButton.Y;

    public GamepadButton CDown { get; set; } = GamepadButton.B;

    public GamepadButton CLeft { get; set; } = GamepadButton.LeftThumb;

    public GamepadButton CRight { get; set; } = GamepadButton.RightThumb;
}

public static class PixelDeckSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixelDeck",
        "settings.json");

    public static PixelDeckSettings Current { get; } = Load();

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    private static PixelDeckSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return Normalize(new PixelDeckSettings());
            }

            var settings = JsonSerializer.Deserialize<PixelDeckSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? new PixelDeckSettings();
            AssignDistinctControllerIndices(settings);

            if (!Enum.IsDefined(settings.Mmc3IrqRevision))
            {
                settings.Mmc3IrqRevision = Mmc3IrqRevision.Auto;
            }

            if (!Enum.IsDefined(settings.NesPpuRevision))
            {
                settings.NesPpuRevision = NesPpuRevision.Rp2C02G;
            }

            if (!Enum.IsDefined(settings.NesOamCorruptionMode))
            {
                settings.NesOamCorruptionMode = NesOamCorruptionMode.StableCpuPpuAlignment;
            }

            return Normalize(settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            return Normalize(new PixelDeckSettings());
        }
    }

    private static PixelDeckSettings Normalize(PixelDeckSettings settings)
    {
        settings.N64Ports ??= PixelDeckSettings.CreateDefaultN64Ports();
        while (settings.N64Ports.Count < N64ButtonMap.PortCount)
        {
            settings.N64Ports.Add(new N64ButtonMap());
        }

        settings.N64Ports.RemoveRange(
            N64ButtonMap.PortCount,
            settings.N64Ports.Count - N64ButtonMap.PortCount);
        return settings;
    }

    /// <summary>
    /// Keeps every player on a different physical device, preferring earlier players' choices and
    /// filling the rest from whatever slots are still free.
    /// </summary>
    private static void AssignDistinctControllerIndices(PixelDeckSettings settings)
    {
        var requested = new[]
        {
            settings.ControllerIndex,
            settings.PlayerTwoControllerIndex,
            settings.PlayerThreeControllerIndex,
            settings.PlayerFourControllerIndex
        };

        var taken = new bool[N64ButtonMap.PortCount];
        var resolved = new int[requested.Length];
        for (var player = 0; player < requested.Length; player++)
        {
            var index = Math.Clamp(requested[player], 0, N64ButtonMap.PortCount - 1);
            if (taken[index])
            {
                index = Array.IndexOf(taken, false);
            }

            taken[index] = true;
            resolved[player] = index;
        }

        settings.ControllerIndex = resolved[0];
        settings.PlayerTwoControllerIndex = resolved[1];
        settings.PlayerThreeControllerIndex = resolved[2];
        settings.PlayerFourControllerIndex = resolved[3];
    }
}
