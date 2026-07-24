using System.Text.Json;
using System.Text.Json.Serialization;
using PixelDeck.App.Input;
using PixelDeck.Emulation.Nes;

namespace PixelDeck.App.Settings;

public sealed class PixelDeckSettings
{
    public int ControllerIndex { get; set; }

    public GamepadButton AButton { get; set; } = GamepadButton.A;

    public GamepadButton BButton { get; set; } = GamepadButton.X;

    public GamepadButton StartButton { get; set; } = GamepadButton.Start;

    public GamepadButton SelectButton { get; set; } = GamepadButton.Back;

    public bool RemoveNesSpriteLimit { get; set; }

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
                return new PixelDeckSettings();
            }

            var settings = JsonSerializer.Deserialize<PixelDeckSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? new PixelDeckSettings();
            settings.ControllerIndex = Math.Clamp(settings.ControllerIndex, 0, 3);
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

            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            return new PixelDeckSettings();
        }
    }
}
