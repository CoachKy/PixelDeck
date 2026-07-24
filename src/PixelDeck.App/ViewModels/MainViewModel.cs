using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixelDeck.App.Input;
using PixelDeck.App.Models;
using PixelDeck.App.Services;
using PixelDeck.App.Settings;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly GameLibrary _library;
    private readonly PlayHistoryStore _playHistory;

    public MainViewModel()
        : this(new GameLibrary(), PlayHistoryStore.Default)
    {
    }

    internal MainViewModel(GameLibrary library, PlayHistoryStore? playHistory = null)
    {
        _library = library;
        _playHistory = playHistory ?? PlayHistoryStore.Default;
        GamesFolder = _library.GamesFolder;

        ControllerSlots = Enumerable.Range(0, 4)
            .Select(index => new ControllerSlotOption(index, $"Controller {index + 1}"))
            .ToArray();
        ControllerButtons =
        [
            new(GamepadButton.A, "A"),
            new(GamepadButton.B, "B"),
            new(GamepadButton.X, "X"),
            new(GamepadButton.Y, "Y"),
            new(GamepadButton.LeftShoulder, "Left bumper"),
            new(GamepadButton.RightShoulder, "Right bumper"),
            new(GamepadButton.LeftTrigger, "Left trigger"),
            new(GamepadButton.LeftThumb, "Left stick click"),
            new(GamepadButton.RightThumb, "Right stick click"),
            new(GamepadButton.Start, "Menu / Start"),
            new(GamepadButton.Back, "View / Back")
        ];
        Mmc3IrqRevisions =
        [
            new(Mmc3IrqRevision.Auto, "Auto (cartridge header)"),
            new(Mmc3IrqRevision.Sharp, "Sharp / new"),
            new(Mmc3IrqRevision.Nec, "NEC / old")
        ];
        NesPpuRevisions =
        [
            new(NesPpuRevision.Rp2C02G, "RP2C02G (standard NES)"),
            new(NesPpuRevision.Rp2C02BOrEarlier, "RP2C02B or earlier")
        ];
        NesOamCorruptionModes =
        [
            new(NesOamCorruptionMode.StableCpuPpuAlignment, "Stable CPU / PPU alignment"),
            new(NesOamCorruptionMode.WorstCase, "Collision-prone / worst case")
        ];

        var settings = PixelDeckSettingsStore.Current;
        selectedControllerSlot = ControllerSlots[settings.ControllerIndex];
        selectedNintendoAButton = FindButton(settings.AButton, GamepadButton.A);
        selectedNintendoBButton = FindButton(settings.BButton, GamepadButton.X);
        selectedNintendoStartButton = FindButton(settings.StartButton, GamepadButton.Start);
        selectedNintendoSelectButton = FindButton(settings.SelectButton, GamepadButton.Back);
        removeNesSpriteLimit = settings.RemoveNesSpriteLimit;
        hideNesHorizontalOverscan = settings.HideNesHorizontalOverscan;
        selectedMmc3IrqRevision = Mmc3IrqRevisions.First(
            option => option.Revision == settings.Mmc3IrqRevision);
        selectedNesPpuRevision = NesPpuRevisions.First(
            option => option.Revision == settings.NesPpuRevision);
        enableNesOamDecay = settings.EnableNesOamDecay;
        selectedNesOamCorruptionMode = NesOamCorruptionModes.First(
            option => option.Mode == settings.NesOamCorruptionMode);
        selectedSnesAButton = FindButton(settings.SnesAButton, GamepadButton.B);
        selectedSnesBButton = FindButton(settings.SnesBButton, GamepadButton.A);
        selectedSnesXButton = FindButton(settings.SnesXButton, GamepadButton.Y);
        selectedSnesYButton = FindButton(settings.SnesYButton, GamepadButton.X);
        selectedSnesLButton = FindButton(settings.SnesLButton, GamepadButton.LeftShoulder);
        selectedSnesRButton = FindButton(settings.SnesRButton, GamepadButton.RightShoulder);
        selectedSnesStartButton = FindButton(settings.SnesStartButton, GamepadButton.Start);
        selectedSnesSelectButton = FindButton(settings.SnesSelectButton, GamepadButton.Back);
    }

    public ObservableCollection<GameEntry> Games { get; } = [];

    public ObservableCollection<GameEntry> LibraryGames { get; } = [];

    public ObservableCollection<RecentGameEntry> RecentGames { get; } = [];

    public string GamesFolder { get; }

    public IReadOnlyList<ControllerSlotOption> ControllerSlots { get; }

    public IReadOnlyList<ControllerButtonOption> ControllerButtons { get; }

    public IReadOnlyList<Mmc3IrqRevisionOption> Mmc3IrqRevisions { get; }

    public IReadOnlyList<NesPpuRevisionOption> NesPpuRevisions { get; }

    public IReadOnlyList<NesOamCorruptionModeOption> NesOamCorruptionModes { get; }

    public string PixelDeckVersionText { get; } =
        FormatProductVersion("PixelDeck", typeof(MainViewModel).Assembly.GetName().Version);

    public string PixelNesVersionText { get; } =
        FormatProductVersion("PixelNES", typeof(NesMachine).Assembly.GetName().Version);

    public string PixelSnesVersionText { get; } =
        FormatProductVersion("PixelSNES", typeof(SnesMachine).Assembly.GetName().Version);

    public string LibraryEmulatorVersionText => SelectedLibrarySystem == LibrarySystem.Nintendo
        ? PixelNesVersionText
        : PixelSnesVersionText;

    public bool IsHomeVisible => SelectedPage == DashboardPage.Home;

    public bool IsLibraryVisible => SelectedPage == DashboardPage.Library;

    public bool IsSettingsVisible => SelectedPage == DashboardPage.Settings;

    public bool IsQuitVisible => SelectedPage == DashboardPage.Quit;

    public bool IsNintendoSelected => SelectedLibrarySystem == LibrarySystem.Nintendo;

    public bool IsSuperNintendoSelected => SelectedLibrarySystem == LibrarySystem.SuperNintendo;

    public bool HasGames => LibraryGames.Count > 0;

    public bool IsEmpty => !IsBusy && LibraryGames.Count == 0;

    public bool HasRecentGames => RecentGames.Count > 0;

    public bool IsHomeEmpty => !IsBusy && RecentGames.Count == 0;

    public string SelectedLibraryFolder => SelectedLibrarySystem == LibrarySystem.Nintendo
        ? _library.NintendoFolder
        : _library.SuperNintendoFolder;

    public string LibrarySystemTitle => SelectedLibrarySystem == LibrarySystem.Nintendo
        ? "Nintendo Entertainment System"
        : "Super Nintendo Entertainment System";

    public string EmptyLibraryText => SelectedLibrarySystem == LibrarySystem.Nintendo
        ? "Place NES homebrew in Games/Nintendo. PixelDeck will pick it up automatically."
        : "Place Super Nintendo homebrew in Games/SuperNintendo. PixelDeck will pick it up automatically.";

    public string OpenLibraryFolderText => SelectedLibrarySystem == LibrarySystem.Nintendo
        ? "Open Nintendo folder"
        : "Open Super Nintendo folder";

    public string GameCountText => LibraryGames.Count switch
    {
        0 => "NO GAMES FOUND",
        1 => "1 GAME FOUND",
        _ => $"{LibraryGames.Count} GAMES FOUND"
    };

    public string RecentSummaryText => RecentGames.Count switch
    {
        0 => "NO PLAY HISTORY YET",
        1 => "1 RECENT GAME",
        _ => $"{RecentGames.Count} RECENT GAMES"
    };

    public string SystemTotalPlayTimeText
    {
        get
        {
            var totalTicks = LibraryGames.Sum(game => game.TotalPlayTime.Ticks);
            var total = TimeSpan.FromTicks(totalTicks);
            return total.TotalSeconds <= 0
                ? "NO PLAY TIME YET"
                : total.TotalMinutes < 1
                    ? "< 1 MIN TOTAL PLAYED"
                    : total.TotalHours < 1
                        ? $"{(int)total.TotalMinutes} MIN TOTAL PLAYED"
                        : total.Minutes == 0
                            ? $"{(long)total.TotalHours}H TOTAL PLAYED"
                            : $"{(long)total.TotalHours}H {total.Minutes}M TOTAL PLAYED";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeVisible))]
    [NotifyPropertyChangedFor(nameof(IsLibraryVisible))]
    [NotifyPropertyChangedFor(nameof(IsSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsQuitVisible))]
    private DashboardPage selectedPage = DashboardPage.Home;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNintendoSelected))]
    [NotifyPropertyChangedFor(nameof(IsSuperNintendoSelected))]
    [NotifyPropertyChangedFor(nameof(SelectedLibraryFolder))]
    [NotifyPropertyChangedFor(nameof(LibrarySystemTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyLibraryText))]
    [NotifyPropertyChangedFor(nameof(OpenLibraryFolderText))]
    [NotifyPropertyChangedFor(nameof(LibraryEmulatorVersionText))]
    private LibrarySystem selectedLibrarySystem = LibrarySystem.Nintendo;

    [ObservableProperty]
    private GameEntry? selectedGame;

    [ObservableProperty]
    private int selectedIndex = -1;

    [ObservableProperty]
    private RecentGameEntry? selectedRecentGame;

    [ObservableProperty]
    private int selectedRecentIndex = -1;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = "SCANNING LOCAL LIBRARY";

    [ObservableProperty]
    private string clockText = DateTime.Now.ToString("h:mm tt").ToUpperInvariant();

    [ObservableProperty]
    private string controllerStatusText = "CHECKING CONTROLLER";

    [ObservableProperty]
    private ControllerSlotOption selectedControllerSlot;

    [ObservableProperty]
    private ControllerButtonOption selectedNintendoAButton;

    [ObservableProperty]
    private ControllerButtonOption selectedNintendoBButton;

    [ObservableProperty]
    private ControllerButtonOption selectedNintendoStartButton;

    [ObservableProperty]
    private ControllerButtonOption selectedNintendoSelectButton;

    [ObservableProperty]
    private bool removeNesSpriteLimit;

    [ObservableProperty]
    private bool hideNesHorizontalOverscan;

    [ObservableProperty]
    private Mmc3IrqRevisionOption selectedMmc3IrqRevision;

    [ObservableProperty]
    private NesPpuRevisionOption selectedNesPpuRevision;

    [ObservableProperty]
    private bool enableNesOamDecay;

    [ObservableProperty]
    private NesOamCorruptionModeOption selectedNesOamCorruptionMode;

    [ObservableProperty]
    private ControllerButtonOption selectedSnesAButton;

    [ObservableProperty]
    private ControllerButtonOption selectedSnesBButton;

    [ObservableProperty]
    private ControllerButtonOption selectedSnesXButton;

    [ObservableProperty]
    private ControllerButtonOption selectedSnesYButton;

    [ObservableProperty]
    private ControllerButtonOption selectedSnesLButton;

    [ObservableProperty]
    private ControllerButtonOption selectedSnesRButton;

    [ObservableProperty]
    private ControllerButtonOption selectedSnesStartButton;

    [ObservableProperty]
    private ControllerButtonOption selectedSnesSelectButton;

    partial void OnSelectedControllerSlotChanged(ControllerSlotOption value)
    {
        PixelDeckSettingsStore.Current.ControllerIndex = value.Index;
        PixelDeckSettingsStore.Save();
        ControllerStatusText = "CHECKING CONTROLLER";
    }

    partial void OnSelectedNintendoAButtonChanged(ControllerButtonOption value) => SaveNintendoButtonSettings();

    partial void OnSelectedNintendoBButtonChanged(ControllerButtonOption value) => SaveNintendoButtonSettings();

    partial void OnSelectedNintendoStartButtonChanged(ControllerButtonOption value) => SaveNintendoButtonSettings();

    partial void OnSelectedNintendoSelectButtonChanged(ControllerButtonOption value) => SaveNintendoButtonSettings();

    partial void OnRemoveNesSpriteLimitChanged(bool value)
    {
        PixelDeckSettingsStore.Current.RemoveNesSpriteLimit = value;
        PixelDeckSettingsStore.Save();
    }

    partial void OnHideNesHorizontalOverscanChanged(bool value)
    {
        PixelDeckSettingsStore.Current.HideNesHorizontalOverscan = value;
        PixelDeckSettingsStore.Save();
    }

    partial void OnSelectedMmc3IrqRevisionChanged(Mmc3IrqRevisionOption value)
    {
        PixelDeckSettingsStore.Current.Mmc3IrqRevision = value.Revision;
        PixelDeckSettingsStore.Save();
    }

    partial void OnSelectedNesPpuRevisionChanged(NesPpuRevisionOption value)
    {
        PixelDeckSettingsStore.Current.NesPpuRevision = value.Revision;
        PixelDeckSettingsStore.Save();
    }

    partial void OnEnableNesOamDecayChanged(bool value)
    {
        PixelDeckSettingsStore.Current.EnableNesOamDecay = value;
        PixelDeckSettingsStore.Save();
    }

    partial void OnSelectedNesOamCorruptionModeChanged(NesOamCorruptionModeOption value)
    {
        PixelDeckSettingsStore.Current.NesOamCorruptionMode = value.Mode;
        PixelDeckSettingsStore.Save();
    }

    partial void OnSelectedSnesAButtonChanged(ControllerButtonOption value) => SaveSnesButtonSettings();

    partial void OnSelectedSnesBButtonChanged(ControllerButtonOption value) => SaveSnesButtonSettings();

    partial void OnSelectedSnesXButtonChanged(ControllerButtonOption value) => SaveSnesButtonSettings();

    partial void OnSelectedSnesYButtonChanged(ControllerButtonOption value) => SaveSnesButtonSettings();

    partial void OnSelectedSnesLButtonChanged(ControllerButtonOption value) => SaveSnesButtonSettings();

    partial void OnSelectedSnesRButtonChanged(ControllerButtonOption value) => SaveSnesButtonSettings();

    partial void OnSelectedSnesStartButtonChanged(ControllerButtonOption value) => SaveSnesButtonSettings();

    partial void OnSelectedSnesSelectButtonChanged(ControllerButtonOption value) => SaveSnesButtonSettings();

    partial void OnSelectedLibrarySystemChanged(LibrarySystem value) => RefreshLibraryGames();

    partial void OnSelectedIndexChanged(int value)
    {
        if (value >= 0 && value < LibraryGames.Count && SelectedGame != LibraryGames[value])
        {
            SelectedGame = LibraryGames[value];
        }
    }

    partial void OnSelectedGameChanged(GameEntry? value)
    {
        if (value is null)
        {
            return;
        }

        var index = LibraryGames.IndexOf(value);
        if (index != SelectedIndex)
        {
            SelectedIndex = index;
        }
    }

    partial void OnSelectedRecentIndexChanged(int value)
    {
        if (value >= 0 && value < RecentGames.Count && SelectedRecentGame != RecentGames[value])
        {
            SelectedRecentGame = RecentGames[value];
        }
    }

    partial void OnSelectedRecentGameChanged(RecentGameEntry? value)
    {
        if (value is null)
        {
            return;
        }

        var index = RecentGames.IndexOf(value);
        if (index != SelectedRecentIndex)
        {
            SelectedRecentIndex = index;
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "SCANNING LOCAL LIBRARY";
        NotifyLibraryStateChanged();

        var previousSelection = SelectedGame?.FullPath;
        var previousRecentSelection = SelectedRecentGame?.Game.RelativePath;

        try
        {
            var discoveredGames = await _library.ScanAsync();

            RecentGames.Clear();
            LibraryGames.Clear();
            DisposeGameScreenshots();
            Games.Clear();
            foreach (var game in discoveredGames)
            {
                game.LoadScreenshot();
                Games.Add(game);
            }

            RefreshLibraryGames(previousSelection);
            RefreshPlayHistory(previousRecentSelection);
            StatusText = GameCountText;
        }
        catch (Exception exception)
        {
            StatusText = "LIBRARY SCAN FAILED";
            Debug.WriteLine(exception);
        }
        finally
        {
            IsBusy = false;
            NotifyLibraryStateChanged();
            NotifyHomeStateChanged();
        }
    }

    public void RefreshPlayHistory(string? preferredSelection = null)
    {
        var selection = preferredSelection ?? SelectedRecentGame?.Game.RelativePath;
        RecentGames.Clear();
        var activities = _playHistory.Read();
        var historyByGame = activities
            .GroupBy(activity => NormalizeRelativePath(activity.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(activity => activity.LastPlayedUtc).First(),
                StringComparer.OrdinalIgnoreCase);
        var gamesByPath = Games.ToDictionary(
            game => NormalizeRelativePath(game.RelativePath),
            StringComparer.OrdinalIgnoreCase);

        foreach (var game in Games)
        {
            if (historyByGame.TryGetValue(NormalizeRelativePath(game.RelativePath), out var activity))
            {
                game.UpdatePlayHistory(activity.TotalPlayTimeTicks, activity.SessionCount, activity.LastPlayedUtc);
            }
            else
            {
                game.UpdatePlayHistory(0, 0, null);
            }
        }

        foreach (var activity in activities.OrderByDescending(entry => entry.LastPlayedUtc))
        {
            if (!gamesByPath.TryGetValue(NormalizeRelativePath(activity.RelativePath), out var game))
            {
                continue;
            }

            RecentGames.Add(new RecentGameEntry(
                game,
                TimeSpan.FromTicks(Math.Max(0, activity.TotalPlayTimeTicks)),
                activity.LastPlayedUtc,
                activity.SessionCount));

            if (RecentGames.Count == 5)
            {
                break;
            }
        }

        SelectedRecentGame = RecentGames.FirstOrDefault(entry =>
                string.Equals(entry.Game.RelativePath, selection, StringComparison.OrdinalIgnoreCase))
            ?? RecentGames.FirstOrDefault();
        SelectedRecentIndex = SelectedRecentGame is null ? -1 : RecentGames.IndexOf(SelectedRecentGame);
        NotifyLibraryStateChanged();
        NotifyHomeStateChanged();
    }

    [RelayCommand]
    private void OpenGamesFolder()
    {
        var folder = IsLibraryVisible ? SelectedLibraryFolder : GamesFolder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void ShowHome() => SelectedPage = DashboardPage.Home;

    [RelayCommand]
    private void ShowLibrary() => SelectedPage = DashboardPage.Library;

    [RelayCommand]
    private void ShowSettings() => SelectedPage = DashboardPage.Settings;

    [RelayCommand]
    private void ShowQuit() => SelectedPage = DashboardPage.Quit;

    [RelayCommand]
    private void ShowNintendoLibrary() => SelectedLibrarySystem = LibrarySystem.Nintendo;

    [RelayCommand]
    private void ShowSuperNintendoLibrary() => SelectedLibrarySystem = LibrarySystem.SuperNintendo;

    public GameEntry? GetSelectedGameForLaunch() => SelectedPage switch
    {
        DashboardPage.Home => SelectedRecentGame?.Game,
        DashboardPage.Library => SelectedGame,
        _ => null
    };

    public void SelectPreviousLibraryGame()
    {
        if (LibraryGames.Count == 0)
        {
            return;
        }

        SelectedIndex = SelectedIndex <= 0 ? LibraryGames.Count - 1 : SelectedIndex - 1;
    }

    public void SelectNextLibraryGame()
    {
        if (LibraryGames.Count == 0)
        {
            return;
        }

        SelectedIndex = SelectedIndex >= LibraryGames.Count - 1 ? 0 : SelectedIndex + 1;
    }

    public bool SelectLibraryGameInAdjacentRow(int direction, int columnCount)
    {
        if (LibraryGames.Count == 0 || direction == 0 || columnCount <= 0)
        {
            return false;
        }

        var current = Math.Clamp(SelectedIndex, 0, LibraryGames.Count - 1);
        var target = current + (Math.Sign(direction) * columnCount);
        if (target < 0)
        {
            return false;
        }

        if (target >= LibraryGames.Count)
        {
            var nextRowStart = ((current / columnCount) + 1) * columnCount;
            if (nextRowStart >= LibraryGames.Count)
            {
                return false;
            }

            target = LibraryGames.Count - 1;
        }

        SelectedIndex = target;
        return target != current;
    }

    public void SelectPreviousRecentGame()
    {
        if (RecentGames.Count == 0)
        {
            return;
        }

        SelectedRecentIndex = SelectedRecentIndex <= 0 ? RecentGames.Count - 1 : SelectedRecentIndex - 1;
    }

    public void SelectNextRecentGame()
    {
        if (RecentGames.Count == 0)
        {
            return;
        }

        SelectedRecentIndex = SelectedRecentIndex >= RecentGames.Count - 1 ? 0 : SelectedRecentIndex + 1;
    }

    public void UpdateClock() => ClockText = DateTime.Now.ToString("h:mm tt").ToUpperInvariant();

    public void UpdateControllerStatus(bool isConnected) =>
        ControllerStatusText = isConnected
            ? $"CONTROLLER {SelectedControllerSlot.Index + 1} CONNECTED"
            : $"CONTROLLER {SelectedControllerSlot.Index + 1} NOT CONNECTED";

    public void Dispose() => DisposeGameScreenshots();

    private void RefreshLibraryGames(string? preferredSelection = null)
    {
        var selection = preferredSelection ?? SelectedGame?.FullPath;
        LibraryGames.Clear();

        var platformCodes = SelectedLibrarySystem == LibrarySystem.Nintendo
            ? new[] { "NES", "FDS" }
            : ["SNES"];

        foreach (var game in Games.Where(game => platformCodes.Contains(game.PlatformCode, StringComparer.OrdinalIgnoreCase)))
        {
            LibraryGames.Add(game);
        }

        SelectedGame = LibraryGames.FirstOrDefault(game =>
                string.Equals(game.FullPath, selection, StringComparison.OrdinalIgnoreCase))
            ?? LibraryGames.FirstOrDefault();
        SelectedIndex = SelectedGame is null ? -1 : LibraryGames.IndexOf(SelectedGame);
        if (!IsBusy)
        {
            StatusText = GameCountText;
        }

        NotifyLibraryStateChanged();
    }

    private void NotifyLibraryStateChanged()
    {
        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(GameCountText));
        OnPropertyChanged(nameof(SystemTotalPlayTimeText));
    }

    private void NotifyHomeStateChanged()
    {
        OnPropertyChanged(nameof(HasRecentGames));
        OnPropertyChanged(nameof(IsHomeEmpty));
        OnPropertyChanged(nameof(RecentSummaryText));
    }

    private void DisposeGameScreenshots()
    {
        foreach (var game in Games)
        {
            game.Dispose();
        }
    }

    private ControllerButtonOption FindButton(GamepadButton button, GamepadButton fallback) =>
        ControllerButtons.FirstOrDefault(option => option.Button == button)
        ?? ControllerButtons.First(option => option.Button == fallback);

    private void SaveNintendoButtonSettings()
    {
        if (SelectedNintendoAButton is null ||
            SelectedNintendoBButton is null ||
            SelectedNintendoStartButton is null ||
            SelectedNintendoSelectButton is null)
        {
            return;
        }

        var settings = PixelDeckSettingsStore.Current;
        settings.AButton = SelectedNintendoAButton.Button;
        settings.BButton = SelectedNintendoBButton.Button;
        settings.StartButton = SelectedNintendoStartButton.Button;
        settings.SelectButton = SelectedNintendoSelectButton.Button;
        PixelDeckSettingsStore.Save();
    }

    private void SaveSnesButtonSettings()
    {
        if (SelectedSnesAButton is null ||
            SelectedSnesBButton is null ||
            SelectedSnesXButton is null ||
            SelectedSnesYButton is null ||
            SelectedSnesLButton is null ||
            SelectedSnesRButton is null ||
            SelectedSnesStartButton is null ||
            SelectedSnesSelectButton is null)
        {
            return;
        }

        var settings = PixelDeckSettingsStore.Current;
        settings.SnesAButton = SelectedSnesAButton.Button;
        settings.SnesBButton = SelectedSnesBButton.Button;
        settings.SnesXButton = SelectedSnesXButton.Button;
        settings.SnesYButton = SelectedSnesYButton.Button;
        settings.SnesLButton = SelectedSnesLButton.Button;
        settings.SnesRButton = SelectedSnesRButton.Button;
        settings.SnesStartButton = SelectedSnesStartButton.Button;
        settings.SnesSelectButton = SelectedSnesSelectButton.Button;
        PixelDeckSettingsStore.Save();
    }

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/');

    private static string FormatProductVersion(string productName, Version? version)
    {
        var major = Math.Max(0, version?.Major ?? 0);
        var minor = Math.Max(0, version?.Minor ?? 0);
        var patch = Math.Max(0, version?.Build ?? 0);
        return $"{productName} v{major}.{minor}.{patch:000}";
    }
}

public sealed record RecentGameEntry(GameEntry Game, TimeSpan TotalPlayTime, DateTime LastPlayedUtc, int SessionCount)
{
    public string TotalPlayTimeText => TotalPlayTime.TotalMinutes < 1
        ? "< 1 MIN PLAYED"
        : TotalPlayTime.TotalHours < 1
            ? $"{(int)TotalPlayTime.TotalMinutes} MIN PLAYED"
            : $"{(int)TotalPlayTime.TotalHours}H {TotalPlayTime.Minutes}M PLAYED";

    public string LastPlayedText
    {
        get
        {
            var local = LastPlayedUtc.ToLocalTime();
            if (local.Date == DateTime.Today)
            {
                return $"TODAY / {local:h:mm tt}".ToUpperInvariant();
            }

            if (local.Date == DateTime.Today.AddDays(-1))
            {
                return $"YESTERDAY / {local:h:mm tt}".ToUpperInvariant();
            }

            return local.ToString("MMM d / h:mm tt").ToUpperInvariant();
        }
    }

    public string SessionText => SessionCount == 1 ? "1 SESSION" : $"{SessionCount} SESSIONS";
}

public enum DashboardPage
{
    Home,
    Library,
    Settings,
    Quit
}

public enum LibrarySystem
{
    Nintendo,
    SuperNintendo
}

public sealed record ControllerSlotOption(int Index, string Label);

public sealed record ControllerButtonOption(GamepadButton Button, string Label);

public sealed record Mmc3IrqRevisionOption(Mmc3IrqRevision Revision, string Label);

public sealed record NesPpuRevisionOption(NesPpuRevision Revision, string Label);

public sealed record NesOamCorruptionModeOption(NesOamCorruptionMode Mode, string Label);
