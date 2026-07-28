using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixelDeck.App.Input;
using PixelDeck.App.Models;
using PixelDeck.App.Services;
using PixelDeck.App.Settings;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.N64;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    /// <summary>Controller ports offered by every core except the Nintendo 64.</summary>
    private const int TwoPlayerCount = 2;

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
        LibrarySystems =
        [
            new(
                LibrarySystem.Nintendo,
                "Nintendo",
                "Nintendo Entertainment System",
                _library.NintendoFolder,
                "Place NES homebrew in Games/Nintendo. PixelDeck will pick it up automatically.",
                ["NES", "FDS"],
                PixelNesVersionText,
                () => SelectedLibrarySystem = LibrarySystem.Nintendo),
            new(
                LibrarySystem.SuperNintendo,
                "Super Nintendo",
                "Super Nintendo Entertainment System",
                _library.SuperNintendoFolder,
                "Place Super Nintendo homebrew in Games/SuperNintendo. PixelDeck will pick it up automatically.",
                ["SNES"],
                PixelSnesVersionText,
                () => SelectedLibrarySystem = LibrarySystem.SuperNintendo),
            new(
                LibrarySystem.Nintendo64,
                "Nintendo 64",
                "Nintendo 64",
                _library.Nintendo64Folder,
                "Place Nintendo 64 homebrew in Games/Nintendo64. PixelDeck will pick it up automatically.",
                ["N64"],
                PixelN64VersionText,
                () => SelectedLibrarySystem = LibrarySystem.Nintendo64)
        ];
        LibrarySystems[0].IsSelected = true;

        ControllerSlots = Enumerable.Range(0, 4)
            .Select(index => new ControllerSlotOption(index, $"Controller {index + 1}"))
            .ToArray();
        AllControllerSetupPlayers =
        [
            new(ControllerSetupPlayer.PlayerOne, "Player One"),
            new(ControllerSetupPlayer.PlayerTwo, "Player Two"),
            new(ControllerSetupPlayer.PlayerThree, "Player Three"),
            new(ControllerSetupPlayer.PlayerFour, "Player Four")
        ];
        ControllerSetupPlayers = [.. AllControllerSetupPlayers.Take(TwoPlayerCount)];
        ControllerSetupConsoles =
        [
            new(ControllerSetupConsole.Nintendo, "Nintendo"),
            new(ControllerSetupConsole.SuperNintendo, "Super Nintendo"),
            new(ControllerSetupConsole.Nintendo64, "Nintendo 64")
        ];
        selectedControllerSetupPlayer = ControllerSetupPlayers[0];
        selectedControllerSetupConsole = ControllerSetupConsoles[0];
        ControllerButtons =
        [
            new(GamepadButton.A, "South (A / Cross)"),
            new(GamepadButton.B, "East (B / Circle)"),
            new(GamepadButton.X, "West (X / Square)"),
            new(GamepadButton.Y, "North (Y / Triangle)"),
            new(GamepadButton.LeftShoulder, "Left bumper / L1"),
            new(GamepadButton.RightShoulder, "Right bumper / R1"),
            new(GamepadButton.LeftTrigger, "Left trigger"),
            new(GamepadButton.LeftThumb, "Left stick click"),
            new(GamepadButton.RightThumb, "Right stick click"),
            new(GamepadButton.Start, "Start / Menu / Options"),
            new(GamepadButton.Back, "Select / View / Create")
        ];

        // The N64 list adds "unassigned" because the C cluster is already reachable through the
        // right stick, so a player may reasonably want its digital fallbacks cleared.
        N64ControllerButtons =
        [
            new(GamepadButton.None, "Unassigned"),
            .. ControllerButtons
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
        selectedPlayerTwoControllerSlot = ControllerSlots[settings.PlayerTwoControllerIndex];
        selectedPlayerThreeControllerSlot = ControllerSlots[settings.PlayerThreeControllerIndex];
        selectedPlayerFourControllerSlot = ControllerSlots[settings.PlayerFourControllerIndex];
        selectedNintendoAButton = FindButton(settings.AButton, GamepadButton.A);
        selectedNintendoBButton = FindButton(settings.BButton, GamepadButton.X);
        selectedNintendoStartButton = FindButton(settings.StartButton, GamepadButton.Start);
        selectedNintendoSelectButton = FindButton(settings.SelectButton, GamepadButton.Back);
        selectedPlayerTwoNintendoAButton = FindButton(settings.PlayerTwoAButton, GamepadButton.A);
        selectedPlayerTwoNintendoBButton = FindButton(settings.PlayerTwoBButton, GamepadButton.X);
        selectedPlayerTwoNintendoStartButton = FindButton(
            settings.PlayerTwoStartButton,
            GamepadButton.Start);
        selectedPlayerTwoNintendoSelectButton = FindButton(
            settings.PlayerTwoSelectButton,
            GamepadButton.Back);
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
        selectedPlayerTwoSnesAButton = FindButton(settings.PlayerTwoSnesAButton, GamepadButton.B);
        selectedPlayerTwoSnesBButton = FindButton(settings.PlayerTwoSnesBButton, GamepadButton.A);
        selectedPlayerTwoSnesXButton = FindButton(settings.PlayerTwoSnesXButton, GamepadButton.Y);
        selectedPlayerTwoSnesYButton = FindButton(settings.PlayerTwoSnesYButton, GamepadButton.X);
        selectedPlayerTwoSnesLButton = FindButton(
            settings.PlayerTwoSnesLButton,
            GamepadButton.LeftShoulder);
        selectedPlayerTwoSnesRButton = FindButton(
            settings.PlayerTwoSnesRButton,
            GamepadButton.RightShoulder);
        selectedPlayerTwoSnesStartButton = FindButton(
            settings.PlayerTwoSnesStartButton,
            GamepadButton.Start);
        selectedPlayerTwoSnesSelectButton = FindButton(
            settings.PlayerTwoSnesSelectButton,
            GamepadButton.Back);
    }

    public ObservableCollection<GameEntry> Games { get; } = [];

    public ObservableCollection<GameEntry> LibraryGames { get; } = [];

    public ObservableCollection<LibrarySection> LibrarySections { get; } = [];

    public ObservableCollection<RecentGameEntry> RecentGames { get; } = [];

    public string GamesFolder { get; }

    public IReadOnlyList<ControllerSlotOption> ControllerSlots { get; }

    public IReadOnlyList<ControllerButtonOption> ControllerButtons { get; }

    public IReadOnlyList<ControllerButtonOption> N64ControllerButtons { get; }

    private IReadOnlyList<ControllerSetupPlayerOption> AllControllerSetupPlayers { get; }

    /// <summary>
    /// Players offered by the setup page. Only the Nintendo 64 profile exposes three and four,
    /// because the NES and SNES cores have two controller ports.
    /// </summary>
    public ObservableCollection<ControllerSetupPlayerOption> ControllerSetupPlayers { get; }

    public IReadOnlyList<ControllerSetupConsoleOption> ControllerSetupConsoles { get; }

    public IReadOnlyList<Mmc3IrqRevisionOption> Mmc3IrqRevisions { get; }

    public IReadOnlyList<NesPpuRevisionOption> NesPpuRevisions { get; }

    public IReadOnlyList<NesOamCorruptionModeOption> NesOamCorruptionModes { get; }

    public IReadOnlyList<LibrarySystemTab> LibrarySystems { get; }

    public string PixelDeckVersionText { get; } =
        FormatProductVersion("PixelDeck", typeof(MainViewModel).Assembly.GetName().Version);

    public string PixelNesVersionText { get; } =
        FormatProductVersion("PixelNES", typeof(NesMachine).Assembly.GetName().Version);

    public string PixelSnesVersionText { get; } =
        FormatProductVersion("PixelSNES", typeof(SnesMachine).Assembly.GetName().Version);

    public string PixelN64VersionText { get; } =
        FormatProductVersion("Pixel64", typeof(N64Machine).Assembly.GetName().Version);

    public string LibraryEmulatorVersionText => SelectedLibrary.EmulatorVersionText;

    public bool IsHomeVisible => SelectedPage == DashboardPage.Home;

    public bool IsLibraryVisible => SelectedPage == DashboardPage.Library;

    public bool IsSettingsVisible => SelectedPage == DashboardPage.Settings;

    public bool IsQuitVisible => SelectedPage == DashboardPage.Quit;

    public bool HasGames => LibraryGames.Count > 0;

    public bool IsEmpty => !IsBusy && LibraryGames.Count == 0;

    public bool HasRecentGames => RecentGames.Count > 0;

    public bool IsHomeEmpty => !IsBusy && RecentGames.Count == 0;

    public string SelectedLibraryFolder => SelectedLibrary.Folder;

    public string LibrarySystemTitle => SelectedLibrary.SystemTitle;

    public string EmptyLibraryText => SelectedLibrary.EmptyText;

    public string GameCountText => LibraryOrganizer.FormatTitleCount(LibraryGames.Count);

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
    [NotifyPropertyChangedFor(nameof(SelectedLibraryFolder))]
    [NotifyPropertyChangedFor(nameof(LibrarySystemTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyLibraryText))]
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
    private string controllerStatusText = "CHECKING CONTROLLERS";

    [ObservableProperty]
    private string connectedControllerCountText = "CHECKING CONTROLLERS";

    [ObservableProperty]
    private string controllerInputBackendText = "INITIALIZING GAMEPAD INPUT";

    [ObservableProperty]
    private ControllerSlotOption selectedControllerSlot;

    [ObservableProperty]
    private ControllerSlotOption selectedPlayerTwoControllerSlot;

    [ObservableProperty]
    private ControllerSlotOption selectedPlayerThreeControllerSlot;

    [ObservableProperty]
    private ControllerSlotOption selectedPlayerFourControllerSlot;

    [ObservableProperty]
    private ControllerSetupPlayerOption selectedControllerSetupPlayer;

    [ObservableProperty]
    private ControllerSetupConsoleOption selectedControllerSetupConsole;

    [ObservableProperty]
    private ControllerButtonOption selectedNintendoAButton;

    [ObservableProperty]
    private ControllerButtonOption selectedNintendoBButton;

    [ObservableProperty]
    private ControllerButtonOption selectedNintendoStartButton;

    [ObservableProperty]
    private ControllerButtonOption selectedNintendoSelectButton;

    [ObservableProperty]
    private ControllerButtonOption selectedPlayerTwoNintendoAButton;

    [ObservableProperty]
    private ControllerButtonOption selectedPlayerTwoNintendoBButton;

    [ObservableProperty]
    private ControllerButtonOption selectedPlayerTwoNintendoStartButton;

    [ObservableProperty]
    private ControllerButtonOption selectedPlayerTwoNintendoSelectButton;

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

    [ObservableProperty]
    private ControllerButtonOption selectedPlayerTwoSnesAButton;

    [ObservableProperty]
    private ControllerButtonOption selectedPlayerTwoSnesBButton;

    [ObservableProperty]
    private ControllerButtonOption selectedPlayerTwoSnesXButton;

    [ObservableProperty]
    private ControllerButtonOption selectedPlayerTwoSnesYButton;

    [ObservableProperty]
    private ControllerButtonOption selectedPlayerTwoSnesLButton;

    [ObservableProperty]
    private ControllerButtonOption selectedPlayerTwoSnesRButton;

    [ObservableProperty]
    private ControllerButtonOption selectedPlayerTwoSnesStartButton;

    [ObservableProperty]
    private ControllerButtonOption selectedPlayerTwoSnesSelectButton;

    public bool IsNintendoControllerSetup =>
        SelectedControllerSetupConsole.Console == ControllerSetupConsole.Nintendo;

    public bool IsSuperNintendoControllerSetup =>
        SelectedControllerSetupConsole.Console == ControllerSetupConsole.SuperNintendo;

    public bool IsNintendo64ControllerSetup =>
        SelectedControllerSetupConsole.Console == ControllerSetupConsole.Nintendo64;

    /// <summary>Zero-based index of the player whose mappings the setup page is editing.</summary>
    private int SetupPlayerIndex => (int)SelectedControllerSetupPlayer.Player;

    public ControllerSlotOption SelectedControllerSetupSlot
    {
        get => SetupPlayerIndex switch
        {
            0 => SelectedControllerSlot,
            1 => SelectedPlayerTwoControllerSlot,
            2 => SelectedPlayerThreeControllerSlot,
            _ => SelectedPlayerFourControllerSlot
        };
        set
        {
            if (ReferenceEquals(value, SelectedControllerSetupSlot))
            {
                return;
            }

            switch (SetupPlayerIndex)
            {
                case 0: SelectedControllerSlot = value; break;
                case 1: SelectedPlayerTwoControllerSlot = value; break;
                case 2: SelectedPlayerThreeControllerSlot = value; break;
                default: SelectedPlayerFourControllerSlot = value; break;
            }

            OnPropertyChanged();
            NotifyControllerSetupChanged();
        }
    }

    public ControllerButtonOption SelectedSetupAButton
    {
        get => GetSelectedSetupButton(
            SelectedNintendoAButton,
            SelectedPlayerTwoNintendoAButton,
            SelectedSnesAButton,
            SelectedPlayerTwoSnesAButton);
        set => SetSelectedSetupButton(
            value,
            option => SelectedNintendoAButton = option,
            option => SelectedPlayerTwoNintendoAButton = option,
            option => SelectedSnesAButton = option,
            option => SelectedPlayerTwoSnesAButton = option);
    }

    public ControllerButtonOption SelectedSetupBButton
    {
        get => GetSelectedSetupButton(
            SelectedNintendoBButton,
            SelectedPlayerTwoNintendoBButton,
            SelectedSnesBButton,
            SelectedPlayerTwoSnesBButton);
        set => SetSelectedSetupButton(
            value,
            option => SelectedNintendoBButton = option,
            option => SelectedPlayerTwoNintendoBButton = option,
            option => SelectedSnesBButton = option,
            option => SelectedPlayerTwoSnesBButton = option);
    }

    public ControllerButtonOption SelectedSetupXButton
    {
        get => SelectedControllerSetupPlayer.Player == ControllerSetupPlayer.PlayerOne
            ? SelectedSnesXButton
            : SelectedPlayerTwoSnesXButton;
        set
        {
            if (SelectedControllerSetupPlayer.Player == ControllerSetupPlayer.PlayerOne)
            {
                SelectedSnesXButton = value;
            }
            else
            {
                SelectedPlayerTwoSnesXButton = value;
            }
        }
    }

    public ControllerButtonOption SelectedSetupYButton
    {
        get => SelectedControllerSetupPlayer.Player == ControllerSetupPlayer.PlayerOne
            ? SelectedSnesYButton
            : SelectedPlayerTwoSnesYButton;
        set
        {
            if (SelectedControllerSetupPlayer.Player == ControllerSetupPlayer.PlayerOne)
            {
                SelectedSnesYButton = value;
            }
            else
            {
                SelectedPlayerTwoSnesYButton = value;
            }
        }
    }

    public ControllerButtonOption SelectedSetupLButton
    {
        get => SelectedControllerSetupPlayer.Player == ControllerSetupPlayer.PlayerOne
            ? SelectedSnesLButton
            : SelectedPlayerTwoSnesLButton;
        set
        {
            if (SelectedControllerSetupPlayer.Player == ControllerSetupPlayer.PlayerOne)
            {
                SelectedSnesLButton = value;
            }
            else
            {
                SelectedPlayerTwoSnesLButton = value;
            }
        }
    }

    public ControllerButtonOption SelectedSetupRButton
    {
        get => SelectedControllerSetupPlayer.Player == ControllerSetupPlayer.PlayerOne
            ? SelectedSnesRButton
            : SelectedPlayerTwoSnesRButton;
        set
        {
            if (SelectedControllerSetupPlayer.Player == ControllerSetupPlayer.PlayerOne)
            {
                SelectedSnesRButton = value;
            }
            else
            {
                SelectedPlayerTwoSnesRButton = value;
            }
        }
    }

    public ControllerButtonOption SelectedSetupStartButton
    {
        get => GetSelectedSetupButton(
            SelectedNintendoStartButton,
            SelectedPlayerTwoNintendoStartButton,
            SelectedSnesStartButton,
            SelectedPlayerTwoSnesStartButton);
        set => SetSelectedSetupButton(
            value,
            option => SelectedNintendoStartButton = option,
            option => SelectedPlayerTwoNintendoStartButton = option,
            option => SelectedSnesStartButton = option,
            option => SelectedPlayerTwoSnesStartButton = option);
    }

    public ControllerButtonOption SelectedSetupSelectButton
    {
        get => GetSelectedSetupButton(
            SelectedNintendoSelectButton,
            SelectedPlayerTwoNintendoSelectButton,
            SelectedSnesSelectButton,
            SelectedPlayerTwoSnesSelectButton);
        set => SetSelectedSetupButton(
            value,
            option => SelectedNintendoSelectButton = option,
            option => SelectedPlayerTwoNintendoSelectButton = option,
            option => SelectedSnesSelectButton = option,
            option => SelectedPlayerTwoSnesSelectButton = option);
    }

    /// <summary>
    /// The Nintendo 64 map for the port being edited. Unlike the NES and SNES blocks these
    /// mappings are not mirrored into observable fields — four ports times ten buttons would be
    /// forty properties — so they read and write settings directly.
    /// </summary>
    private N64ButtonMap SetupN64Map => PixelDeckSettingsStore.Current.N64Ports[SetupPlayerIndex];

    public ControllerButtonOption SelectedN64AButton
    {
        get => FindN64Button(SetupN64Map.A);
        set => SetN64Button(value, static (map, button) => map.A = button);
    }

    public ControllerButtonOption SelectedN64BButton
    {
        get => FindN64Button(SetupN64Map.B);
        set => SetN64Button(value, static (map, button) => map.B = button);
    }

    public ControllerButtonOption SelectedN64ZButton
    {
        get => FindN64Button(SetupN64Map.Z);
        set => SetN64Button(value, static (map, button) => map.Z = button);
    }

    public ControllerButtonOption SelectedN64LButton
    {
        get => FindN64Button(SetupN64Map.L);
        set => SetN64Button(value, static (map, button) => map.L = button);
    }

    public ControllerButtonOption SelectedN64RButton
    {
        get => FindN64Button(SetupN64Map.R);
        set => SetN64Button(value, static (map, button) => map.R = button);
    }

    public ControllerButtonOption SelectedN64StartButton
    {
        get => FindN64Button(SetupN64Map.Start);
        set => SetN64Button(value, static (map, button) => map.Start = button);
    }

    public ControllerButtonOption SelectedN64CUpButton
    {
        get => FindN64Button(SetupN64Map.CUp);
        set => SetN64Button(value, static (map, button) => map.CUp = button);
    }

    public ControllerButtonOption SelectedN64CDownButton
    {
        get => FindN64Button(SetupN64Map.CDown);
        set => SetN64Button(value, static (map, button) => map.CDown = button);
    }

    public ControllerButtonOption SelectedN64CLeftButton
    {
        get => FindN64Button(SetupN64Map.CLeft);
        set => SetN64Button(value, static (map, button) => map.CLeft = button);
    }

    public ControllerButtonOption SelectedN64CRightButton
    {
        get => FindN64Button(SetupN64Map.CRight);
        set => SetN64Button(value, static (map, button) => map.CRight = button);
    }

    public string PaperDollSouthActionText => FormatPaperDollAction(GamepadButton.A);

    public string PaperDollEastActionText => FormatPaperDollAction(GamepadButton.B);

    public string PaperDollWestActionText => FormatPaperDollAction(GamepadButton.X);

    public string PaperDollNorthActionText => FormatPaperDollAction(GamepadButton.Y);

    public string PaperDollLeftShoulderActionText =>
        FormatPaperDollAction(GamepadButton.LeftShoulder);

    public string PaperDollRightShoulderActionText =>
        FormatPaperDollAction(GamepadButton.RightShoulder);

    public string PaperDollStartActionText => FormatPaperDollAction(GamepadButton.Start);

    public string PaperDollBackActionText => FormatPaperDollAction(GamepadButton.Back);

    public string PaperDollLeftTriggerActionText =>
        FormatPaperDollAction(GamepadButton.LeftTrigger);

    public string SetupFixedControlsText => IsNintendo64ControllerSetup
        ? "D-PAD / STICK: MOVE  ·  RIGHT STICK: C BUTTONS  ·  R2: 2× SPEED"
        : "D-PAD / STICK: MOVE  ·  R2: 2× SPEED";

    partial void OnSelectedControllerSlotChanged(ControllerSlotOption value)
    {
        SaveControllerSlots(0);
        NotifyControllerSetupChanged();
    }

    partial void OnSelectedPlayerTwoControllerSlotChanged(ControllerSlotOption value)
    {
        SaveControllerSlots(1);
        NotifyControllerSetupChanged();
    }

    partial void OnSelectedPlayerThreeControllerSlotChanged(ControllerSlotOption value)
    {
        SaveControllerSlots(2);
        NotifyControllerSetupChanged();
    }

    partial void OnSelectedPlayerFourControllerSlotChanged(ControllerSlotOption value)
    {
        SaveControllerSlots(3);
        NotifyControllerSetupChanged();
    }

    partial void OnSelectedControllerSetupPlayerChanged(ControllerSetupPlayerOption value) =>
        NotifyControllerSetupChanged();

    partial void OnSelectedControllerSetupConsoleChanged(ControllerSetupConsoleOption value)
    {
        SyncControllerSetupPlayers();
        NotifyControllerSetupChanged();
    }

    partial void OnSelectedNintendoAButtonChanged(ControllerButtonOption value)
    {
        SaveNintendoButtonSettings();
        NotifyControllerSetupMappingsChanged();
    }

    partial void OnSelectedNintendoBButtonChanged(ControllerButtonOption value)
    {
        SaveNintendoButtonSettings();
        NotifyControllerSetupMappingsChanged();
    }

    partial void OnSelectedNintendoStartButtonChanged(ControllerButtonOption value)
    {
        SaveNintendoButtonSettings();
        NotifyControllerSetupMappingsChanged();
    }

    partial void OnSelectedNintendoSelectButtonChanged(ControllerButtonOption value)
    {
        SaveNintendoButtonSettings();
        NotifyControllerSetupMappingsChanged();
    }

    partial void OnSelectedPlayerTwoNintendoAButtonChanged(ControllerButtonOption value)
    {
        SavePlayerTwoNintendoButtonSettings();
        NotifyControllerSetupMappingsChanged();
    }

    partial void OnSelectedPlayerTwoNintendoBButtonChanged(ControllerButtonOption value)
    {
        SavePlayerTwoNintendoButtonSettings();
        NotifyControllerSetupMappingsChanged();
    }

    partial void OnSelectedPlayerTwoNintendoStartButtonChanged(ControllerButtonOption value)
    {
        SavePlayerTwoNintendoButtonSettings();
        NotifyControllerSetupMappingsChanged();
    }

    partial void OnSelectedPlayerTwoNintendoSelectButtonChanged(ControllerButtonOption value)
    {
        SavePlayerTwoNintendoButtonSettings();
        NotifyControllerSetupMappingsChanged();
    }

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

    partial void OnSelectedSnesAButtonChanged(ControllerButtonOption value) =>
        SaveSnesButtonAndRefreshSetup();

    partial void OnSelectedSnesBButtonChanged(ControllerButtonOption value) =>
        SaveSnesButtonAndRefreshSetup();

    partial void OnSelectedSnesXButtonChanged(ControllerButtonOption value) =>
        SaveSnesButtonAndRefreshSetup();

    partial void OnSelectedSnesYButtonChanged(ControllerButtonOption value) =>
        SaveSnesButtonAndRefreshSetup();

    partial void OnSelectedSnesLButtonChanged(ControllerButtonOption value) =>
        SaveSnesButtonAndRefreshSetup();

    partial void OnSelectedSnesRButtonChanged(ControllerButtonOption value) =>
        SaveSnesButtonAndRefreshSetup();

    partial void OnSelectedSnesStartButtonChanged(ControllerButtonOption value) =>
        SaveSnesButtonAndRefreshSetup();

    partial void OnSelectedSnesSelectButtonChanged(ControllerButtonOption value) =>
        SaveSnesButtonAndRefreshSetup();

    partial void OnSelectedPlayerTwoSnesAButtonChanged(ControllerButtonOption value) =>
        SavePlayerTwoSnesButtonAndRefreshSetup();

    partial void OnSelectedPlayerTwoSnesBButtonChanged(ControllerButtonOption value) =>
        SavePlayerTwoSnesButtonAndRefreshSetup();

    partial void OnSelectedPlayerTwoSnesXButtonChanged(ControllerButtonOption value) =>
        SavePlayerTwoSnesButtonAndRefreshSetup();

    partial void OnSelectedPlayerTwoSnesYButtonChanged(ControllerButtonOption value) =>
        SavePlayerTwoSnesButtonAndRefreshSetup();

    partial void OnSelectedPlayerTwoSnesLButtonChanged(ControllerButtonOption value) =>
        SavePlayerTwoSnesButtonAndRefreshSetup();

    partial void OnSelectedPlayerTwoSnesRButtonChanged(ControllerButtonOption value) =>
        SavePlayerTwoSnesButtonAndRefreshSetup();

    partial void OnSelectedPlayerTwoSnesStartButtonChanged(ControllerButtonOption value) =>
        SavePlayerTwoSnesButtonAndRefreshSetup();

    partial void OnSelectedPlayerTwoSnesSelectButtonChanged(ControllerButtonOption value) =>
        SavePlayerTwoSnesButtonAndRefreshSetup();

    partial void OnSelectedLibrarySystemChanged(LibrarySystem value)
    {
        foreach (var librarySystem in LibrarySystems)
        {
            librarySystem.IsSelected = librarySystem.System == value;
        }

        RefreshLibraryGames();
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (value >= 0 && value < LibraryGames.Count && SelectedGame != LibraryGames[value])
        {
            SelectedGame = LibraryGames[value];
        }
    }

    partial void OnSelectedGameChanged(GameEntry? value)
    {
        foreach (var game in LibraryGames)
        {
            game.SetLibrarySelected(ReferenceEquals(game, value));
        }

        if (value is null)
        {
            if (SelectedIndex != -1)
            {
                SelectedIndex = -1;
            }

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
            LibrarySections.Clear();
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
    private void ShowHome() => SelectedPage = DashboardPage.Home;

    [RelayCommand]
    private void ShowLibrary() => SelectedPage = DashboardPage.Library;

    [RelayCommand]
    private void ShowSettings() => SelectedPage = DashboardPage.Settings;

    [RelayCommand]
    private void ShowQuit() => SelectedPage = DashboardPage.Quit;

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
        if (!LibraryOrganizer.TryGetAdjacentGame(
                LibrarySections,
                SelectedGame,
                direction,
                columnCount,
                out var adjacentGame) ||
            adjacentGame is null)
        {
            return false;
        }

        SelectedGame = adjacentGame;
        return true;
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

    /// <param name="deviceConnected">Connection state per physical device slot.</param>
    public void UpdateControllerStatus(
        IReadOnlyList<bool> deviceConnected,
        IReadOnlyList<string?> controllerNames,
        string backendName)
    {
        var connectedControllerCount = deviceConnected.Count(connected => connected);
        var slots = new[]
        {
            SelectedControllerSlot,
            SelectedPlayerTwoControllerSlot,
            SelectedPlayerThreeControllerSlot,
            SelectedPlayerFourControllerSlot
        };

        // Only the Nintendo 64 profile has four ports, so the other profiles keep the P1/P2 line.
        var reportedPlayers = IsNintendo64ControllerSetup ? slots.Length : TwoPlayerCount;
        ControllerStatusText = string.Join(
            "  /  ",
            slots.Take(reportedPlayers).Select((slot, player) =>
            {
                var connected = slot.Index < deviceConnected.Count && deviceConnected[slot.Index];
                return $"P{player + 1} C{slot.Index + 1} " +
                    $"{(connected ? "CONNECTED" : "NOT CONNECTED")}";
            }));
        ConnectedControllerCountText = FormatConnectedControllerCount(connectedControllerCount);
        ControllerInputBackendText = $"{backendName.ToUpperInvariant()} GAMEPAD INPUT";

        var controllerLabelsChanged = false;
        for (var index = 0; index < ControllerSlots.Count; index++)
        {
            var name = index < controllerNames.Count ? controllerNames[index] : null;
            var label = string.IsNullOrWhiteSpace(name)
                ? $"Controller {index + 1} — Not connected"
                : $"Controller {index + 1} — {name}";
            if (ControllerSlots[index].Label == label)
            {
                continue;
            }

            ControllerSlots[index].Label = label;
            controllerLabelsChanged = true;
        }

        if (controllerLabelsChanged)
        {
            OnPropertyChanged(nameof(SelectedControllerSetupSlot));
        }
    }

    internal static string FormatConnectedControllerCount(int count) => count switch
    {
        <= 0 => "NO CONTROLLERS CONNECTED",
        1 => "1 CONTROLLER CONNECTED",
        _ => $"{count} CONTROLLERS CONNECTED"
    };

    public void Dispose()
    {
        DisposeGameScreenshots();
        GC.SuppressFinalize(this);
    }

    private void RefreshLibraryGames(string? preferredSelection = null)
    {
        var selection = preferredSelection ?? SelectedGame?.FullPath;
        LibraryGames.Clear();
        LibrarySections.Clear();

        var sections = LibraryOrganizer.CreateSections(
            Games.Where(game =>
                SelectedLibrary.PlatformCodes.Contains(game.PlatformCode, StringComparer.OrdinalIgnoreCase)));
        foreach (var section in sections)
        {
            LibrarySections.Add(section);
            foreach (var game in section.Games)
            {
                LibraryGames.Add(game);
            }
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

    private LibrarySystemTab SelectedLibrary =>
        LibrarySystems.First(librarySystem => librarySystem.System == SelectedLibrarySystem);

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

    private ControllerButtonOption FindN64Button(GamepadButton button) =>
        N64ControllerButtons.FirstOrDefault(option => option.Button == button)
        ?? N64ControllerButtons.First(option => option.Button == GamepadButton.None);

    private void SetN64Button(
        ControllerButtonOption value,
        Action<N64ButtonMap, GamepadButton> assign,
        [CallerMemberName] string? propertyName = null)
    {
        if (value is null)
        {
            return;
        }

        assign(SetupN64Map, value.Button);
        PixelDeckSettingsStore.Save();
        OnPropertyChanged(propertyName);
        NotifyControllerSetupMappingsChanged();
    }

    /// <summary>
    /// Trims the player picker to the ports the selected console actually has, moving the
    /// selection back to Player One first so the bound picker never sees a missing item.
    /// </summary>
    private void SyncControllerSetupPlayers()
    {
        var desired = IsNintendo64ControllerSetup ? AllControllerSetupPlayers.Count : TwoPlayerCount;
        if (SetupPlayerIndex >= desired)
        {
            SelectedControllerSetupPlayer = AllControllerSetupPlayers[0];
        }

        while (ControllerSetupPlayers.Count > desired)
        {
            ControllerSetupPlayers.RemoveAt(ControllerSetupPlayers.Count - 1);
        }

        while (ControllerSetupPlayers.Count < desired)
        {
            ControllerSetupPlayers.Add(AllControllerSetupPlayers[ControllerSetupPlayers.Count]);
        }
    }

    private ControllerButtonOption GetSelectedSetupButton(
        ControllerButtonOption playerOneNintendo,
        ControllerButtonOption playerTwoNintendo,
        ControllerButtonOption playerOneSnes,
        ControllerButtonOption playerTwoSnes)
    {
        var playerTwo = SelectedControllerSetupPlayer.Player == ControllerSetupPlayer.PlayerTwo;
        return SelectedControllerSetupConsole.Console == ControllerSetupConsole.Nintendo
            ? playerTwo ? playerTwoNintendo : playerOneNintendo
            : playerTwo ? playerTwoSnes : playerOneSnes;
    }

    private void SetSelectedSetupButton(
        ControllerButtonOption value,
        Action<ControllerButtonOption> setPlayerOneNintendo,
        Action<ControllerButtonOption> setPlayerTwoNintendo,
        Action<ControllerButtonOption> setPlayerOneSnes,
        Action<ControllerButtonOption> setPlayerTwoSnes)
    {
        var playerTwo = SelectedControllerSetupPlayer.Player == ControllerSetupPlayer.PlayerTwo;
        if (SelectedControllerSetupConsole.Console == ControllerSetupConsole.Nintendo)
        {
            if (playerTwo) setPlayerTwoNintendo(value);
            else setPlayerOneNintendo(value);
        }
        else
        {
            if (playerTwo) setPlayerTwoSnes(value);
            else setPlayerOneSnes(value);
        }
    }

    private void NotifyControllerSetupChanged()
    {
        OnPropertyChanged(nameof(SelectedControllerSetupSlot));
        OnPropertyChanged(nameof(IsNintendoControllerSetup));
        OnPropertyChanged(nameof(IsSuperNintendoControllerSetup));
        OnPropertyChanged(nameof(IsNintendo64ControllerSetup));
        OnPropertyChanged(nameof(SetupFixedControlsText));
        NotifyControllerSetupMappingsChanged();
    }

    private void NotifyControllerSetupMappingsChanged()
    {
        OnPropertyChanged(nameof(SelectedSetupAButton));
        OnPropertyChanged(nameof(SelectedSetupBButton));
        OnPropertyChanged(nameof(SelectedSetupXButton));
        OnPropertyChanged(nameof(SelectedSetupYButton));
        OnPropertyChanged(nameof(SelectedSetupLButton));
        OnPropertyChanged(nameof(SelectedSetupRButton));
        OnPropertyChanged(nameof(SelectedSetupStartButton));
        OnPropertyChanged(nameof(SelectedSetupSelectButton));
        OnPropertyChanged(nameof(SelectedN64AButton));
        OnPropertyChanged(nameof(SelectedN64BButton));
        OnPropertyChanged(nameof(SelectedN64ZButton));
        OnPropertyChanged(nameof(SelectedN64LButton));
        OnPropertyChanged(nameof(SelectedN64RButton));
        OnPropertyChanged(nameof(SelectedN64StartButton));
        OnPropertyChanged(nameof(SelectedN64CUpButton));
        OnPropertyChanged(nameof(SelectedN64CDownButton));
        OnPropertyChanged(nameof(SelectedN64CLeftButton));
        OnPropertyChanged(nameof(SelectedN64CRightButton));
        OnPropertyChanged(nameof(PaperDollSouthActionText));
        OnPropertyChanged(nameof(PaperDollEastActionText));
        OnPropertyChanged(nameof(PaperDollWestActionText));
        OnPropertyChanged(nameof(PaperDollNorthActionText));
        OnPropertyChanged(nameof(PaperDollLeftShoulderActionText));
        OnPropertyChanged(nameof(PaperDollRightShoulderActionText));
        OnPropertyChanged(nameof(PaperDollStartActionText));
        OnPropertyChanged(nameof(PaperDollBackActionText));
        OnPropertyChanged(nameof(PaperDollLeftTriggerActionText));
    }

    private string FormatPaperDollAction(GamepadButton physicalButton)
    {
        if (physicalButton == GamepadButton.None)
        {
            return "—";
        }

        var actions = new List<string>(2);
        if (IsNintendo64ControllerSetup)
        {
            var map = SetupN64Map;
            if (map.A == physicalButton) actions.Add("A");
            if (map.B == physicalButton) actions.Add("B");
            if (map.Z == physicalButton) actions.Add("Z");
            if (map.L == physicalButton) actions.Add("L");
            if (map.R == physicalButton) actions.Add("R");
            if (map.Start == physicalButton) actions.Add("START");
            if (map.CUp == physicalButton) actions.Add("C↑");
            if (map.CDown == physicalButton) actions.Add("C↓");
            if (map.CLeft == physicalButton) actions.Add("C←");
            if (map.CRight == physicalButton) actions.Add("C→");
            return actions.Count == 0 ? "—" : string.Join(" + ", actions);
        }

        if (SelectedSetupAButton.Button == physicalButton) actions.Add("A");
        if (SelectedSetupBButton.Button == physicalButton) actions.Add("B");
        if (IsSuperNintendoControllerSetup)
        {
            if (SelectedSetupXButton.Button == physicalButton) actions.Add("X");
            if (SelectedSetupYButton.Button == physicalButton) actions.Add("Y");
            if (SelectedSetupLButton.Button == physicalButton) actions.Add("L");
            if (SelectedSetupRButton.Button == physicalButton) actions.Add("R");
        }

        if (SelectedSetupStartButton.Button == physicalButton) actions.Add("START");
        if (SelectedSetupSelectButton.Button == physicalButton) actions.Add("SELECT");
        return actions.Count == 0 ? "—" : string.Join(" + ", actions);
    }

    private bool _isUpdatingControllerSlots;

    /// <summary>
    /// Persists the four player-to-device assignments, keeping them distinct. The player that
    /// just changed keeps the device it asked for; any other player holding that device is pushed
    /// onto the first free slot.
    /// </summary>
    private void SaveControllerSlots(int changedPlayerIndex)
    {
        if (_isUpdatingControllerSlots)
        {
            return;
        }

        _isUpdatingControllerSlots = true;
        try
        {
            var setters = new Action<ControllerSlotOption>[]
            {
                option => SelectedControllerSlot = option,
                option => SelectedPlayerTwoControllerSlot = option,
                option => SelectedPlayerThreeControllerSlot = option,
                option => SelectedPlayerFourControllerSlot = option
            };
            var indices = new[]
            {
                SelectedControllerSlot.Index,
                SelectedPlayerTwoControllerSlot.Index,
                SelectedPlayerThreeControllerSlot.Index,
                SelectedPlayerFourControllerSlot.Index
            };

            var taken = new bool[ControllerSlots.Count];
            taken[indices[changedPlayerIndex]] = true;
            for (var player = 0; player < indices.Length; player++)
            {
                if (player == changedPlayerIndex)
                {
                    continue;
                }

                if (taken[indices[player]])
                {
                    indices[player] = Array.IndexOf(taken, false);
                    setters[player](ControllerSlots[indices[player]]);
                }

                taken[indices[player]] = true;
            }

            var settings = PixelDeckSettingsStore.Current;
            settings.ControllerIndex = indices[0];
            settings.PlayerTwoControllerIndex = indices[1];
            settings.PlayerThreeControllerIndex = indices[2];
            settings.PlayerFourControllerIndex = indices[3];
            PixelDeckSettingsStore.Save();
            ControllerStatusText = "CHECKING CONTROLLERS";
        }
        finally
        {
            _isUpdatingControllerSlots = false;
        }
    }

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

    private void SavePlayerTwoNintendoButtonSettings()
    {
        if (SelectedPlayerTwoNintendoAButton is null ||
            SelectedPlayerTwoNintendoBButton is null ||
            SelectedPlayerTwoNintendoStartButton is null ||
            SelectedPlayerTwoNintendoSelectButton is null)
        {
            return;
        }

        var settings = PixelDeckSettingsStore.Current;
        settings.PlayerTwoAButton = SelectedPlayerTwoNintendoAButton.Button;
        settings.PlayerTwoBButton = SelectedPlayerTwoNintendoBButton.Button;
        settings.PlayerTwoStartButton = SelectedPlayerTwoNintendoStartButton.Button;
        settings.PlayerTwoSelectButton = SelectedPlayerTwoNintendoSelectButton.Button;
        PixelDeckSettingsStore.Save();
    }

    private void SaveSnesButtonAndRefreshSetup()
    {
        SaveSnesButtonSettings();
        NotifyControllerSetupMappingsChanged();
    }

    private void SavePlayerTwoSnesButtonAndRefreshSetup()
    {
        SavePlayerTwoSnesButtonSettings();
        NotifyControllerSetupMappingsChanged();
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

    private void SavePlayerTwoSnesButtonSettings()
    {
        if (SelectedPlayerTwoSnesAButton is null ||
            SelectedPlayerTwoSnesBButton is null ||
            SelectedPlayerTwoSnesXButton is null ||
            SelectedPlayerTwoSnesYButton is null ||
            SelectedPlayerTwoSnesLButton is null ||
            SelectedPlayerTwoSnesRButton is null ||
            SelectedPlayerTwoSnesStartButton is null ||
            SelectedPlayerTwoSnesSelectButton is null)
        {
            return;
        }

        var settings = PixelDeckSettingsStore.Current;
        settings.PlayerTwoSnesAButton = SelectedPlayerTwoSnesAButton.Button;
        settings.PlayerTwoSnesBButton = SelectedPlayerTwoSnesBButton.Button;
        settings.PlayerTwoSnesXButton = SelectedPlayerTwoSnesXButton.Button;
        settings.PlayerTwoSnesYButton = SelectedPlayerTwoSnesYButton.Button;
        settings.PlayerTwoSnesLButton = SelectedPlayerTwoSnesLButton.Button;
        settings.PlayerTwoSnesRButton = SelectedPlayerTwoSnesRButton.Button;
        settings.PlayerTwoSnesStartButton = SelectedPlayerTwoSnesStartButton.Button;
        settings.PlayerTwoSnesSelectButton = SelectedPlayerTwoSnesSelectButton.Button;
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
    SuperNintendo,
    Nintendo64
}

public enum ControllerSetupPlayer
{
    PlayerOne,
    PlayerTwo,

    /// <summary>Nintendo 64 only; the NES and SNES cores are two-player.</summary>
    PlayerThree,
    PlayerFour
}

public enum ControllerSetupConsole
{
    Nintendo,
    SuperNintendo,
    Nintendo64
}

public sealed record ControllerSetupPlayerOption(ControllerSetupPlayer Player, string Label);

public sealed record ControllerSetupConsoleOption(ControllerSetupConsole Console, string Label);

public sealed partial class ControllerSlotOption : ObservableObject
{
    public ControllerSlotOption(int index, string label)
    {
        Index = index;
        this.label = label;
    }

    public int Index { get; }

    [ObservableProperty]
    private string label;
}

public sealed record ControllerButtonOption(GamepadButton Button, string Label);

public sealed record Mmc3IrqRevisionOption(Mmc3IrqRevision Revision, string Label);

public sealed record NesPpuRevisionOption(NesPpuRevision Revision, string Label);

public sealed record NesOamCorruptionModeOption(NesOamCorruptionMode Mode, string Label);
