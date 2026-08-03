using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PixelDeck.App.Audio;
using PixelDeck.App.Input;
using PixelDeck.App.Models;
using PixelDeck.App.Services;
using PixelDeck.App.Settings;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.GameCube;
using PixelDeck.Emulation.N64;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Views;

public partial class EmulatorWindow : Window
{
    private WriteableBitmap? _frameBitmap;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DispatcherTimer _inputTimer;
    private readonly GamepadReader _gamepad = new();
    private readonly GamepadReader _playerTwoGamepad = new();

    /// <summary>Nintendo 64 ports three and four. No other core reads past two players.</summary>
    private readonly GamepadReader _playerThreeGamepad = new();
    private readonly GamepadReader _playerFourGamepad = new();
    private readonly HashSet<Key> _pressedKeys = [];
    private readonly List<Action> _stateMenuActions = [];
    private readonly object _machineLock = new();
    private readonly object _presentationLock = new();
    private readonly AudioBufferSynchronizer _audioBufferSynchronizer = new();
    private GameEntry? _game;
    private SaveStateCatalog? _saveStateCatalog;
    private Task? _emulationTask;
    private NesMachine? _nesMachine;
    private N64Machine? _n64Machine;
    private int _loggedN64Width;
    private int _loggedN64Height;
    private uint _loggedN64Control;
    private uint _loggedN64HorizontalVideo;
    private long _n64FrameCounter;
    private long _coreFrameCounter;
    private GameCubeMachine? _gameCubeMachine;
    private long _gameCubeFrameCounter;
    private bool _gameCubeStopped;
    private GekkoRunResult _gameCubeResult;
    private uint _gameCubeLastPc;
    private long _gameCubeStallFrames;
    private long _gameCubeStallBusiestCount;

    /// <summary>
    /// How many frames of no forward progress count as a stall rather than a
    /// slow patch. Half a second: long enough that ordinary polling for a
    /// device that will answer does not trip it.
    /// </summary>
    private const long GameCubeStallFrames = 30;

    /// <summary>How far the program counter may drift and still count as stuck.</summary>
    private const uint GameCubeStallWindow = 256;

    /// <summary>
    /// How long the Gekko interpreter runs per frame, and how much it does
    /// between checks of that budget. The slice is small enough that a spin
    /// loop cannot hold the dashboard for longer than the budget.
    /// </summary>
    private static readonly TimeSpan GameCubeFrameBudget = TimeSpan.FromMilliseconds(12);

    private const long GameCubeInstructionsPerSlice = 250_000;
    private GamepadReader[]? _rumbleTargets;
    private readonly bool[] _rumbleMotorActive = new bool[GamepadManager.MaximumControllers];
    private SnesMachine? _snesMachine;
    private EmulatorAudioOutput? _audioOutput;
    private Stopwatch? _playSession;
    private readonly N64FrameRateEnforcer _n64FrameRateEnforcer = new();
    private GamepadButton _previousGamepadButtons;
    private GamepadButton _previousPlayerTwoGamepadButtons;
    private bool _pauseChordHeld;
    private bool _playerTwoPauseChordHeld;
    private volatile bool _isPaused;
    private bool _screenshotSaved;
    private int _menuIndex;
    private PauseMenuMode _pauseMenuMode;
    private int _playbackRateMultiplier = 1;
    private uint[]? _presentationPixels;
    private int _presentationFrameNumber;
    private int _presentationScheduled;

    public EmulatorWindow()
    {
        InitializeComponent();
        _inputTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, PollInput);
        Opened += OnOpened;
        Closed += OnClosed;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public EmulatorWindow(GameEntry game)
        : this()
    {
        _game = game;
        _saveStateCatalog = new SaveStateCatalog(game.SaveStatePath);
    }

    private Button[] MainMenuButtons =>
        [ResumeButton, SaveStateButton, LoadStateButton, LibraryImageButton, ResetGameButton, QuitGameButton];

    private Button[] ActiveMenuButtons => _pauseMenuMode == PauseMenuMode.Main
        ? MainMenuButtons
        : StateSlotMenuPanel.Children.OfType<Button>().ToArray();

    private SaveStateCatalog StateCatalog => _saveStateCatalog
        ?? throw new InvalidOperationException("The save-state catalog is not initialized.");

    private void OnOpened(object? sender, EventArgs eventArgs)
    {
        try
        {
            if (_game is null)
            {
                throw new InvalidOperationException("No game was selected.");
            }

            var settings = PixelDeckSettingsStore.Current;

            // Configured slots are preferences; a player pinned to a slot with no
            // controller in it would simply have no input, with nothing in the game
            // to say why. Resolving against what is plugged in means one pad drives
            // player one whichever slot it enumerated into.
            Span<int> assigned = stackalloc int[]
            {
                settings.ControllerIndex,
                settings.PlayerTwoControllerIndex,
                settings.PlayerThreeControllerIndex,
                settings.PlayerFourControllerIndex
            };
            ControllerAssignment.Resolve(assigned, GamepadManager.Shared.ReadConnections());

            _gamepad.UserIndex = assigned[0];
            _playerTwoGamepad.UserIndex = assigned[1];
            _playerThreeGamepad.UserIndex = assigned[2];
            _playerFourGamepad.UserIndex = assigned[3];
            LoadMachine();
            _frameBitmap = new WriteableBitmap(
                new PixelSize(MachineWidth, MachineHeight),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);
            _presentationPixels = new uint[MachineWidth * MachineHeight];
            FrameImage.Source = _frameBitmap;
            _playSession = Stopwatch.StartNew();
            if (_nesMachine is not null)
            {
                _audioOutput = new EmulatorAudioOutput(_nesMachine);
            }
            else if (_snesMachine is not null)
            {
                _audioOutput = new EmulatorAudioOutput(_snesMachine);
            }
            else if (_n64Machine is not null)
            {
                _audioOutput = new EmulatorAudioOutput(_n64Machine);
            }

            UpdateStateAvailability();
            _inputTimer.Start();
            _emulationTask = Task.Run(() => RunEmulationAsync(_cancellation.Token));
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _inputTimer.Stop();
        _cancellation.Cancel();
        lock (_machineLock)
        {
            TryFlushBatterySave();
        }

        _playSession?.Stop();
        if (_game is not null && _playSession is not null)
        {
            PlayHistoryStore.Default.RecordSession(_game, _playSession.Elapsed);
        }

        _audioOutput?.Dispose();
        _audioOutput = null;

        // Disposing the machine writes PixelCube's repeated-key tally and
        // drains its trace file, which is the part of the session worth
        // keeping.
        _gameCubeMachine?.Dispose();
        _gameCubeMachine = null;
        _cancellation.Dispose();
        _frameBitmap?.Dispose();
        _frameBitmap = null;
        _presentationPixels = null;
    }

    private async Task RunEmulationAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var nextFrameAt = TimeSpan.Zero;
        var frameNumber = 0;
        var currentRate = 1;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_isPaused)
                {
                    _audioBufferSynchronizer.Reset();
                    nextFrameAt = clock.Elapsed;
                    await Task.Delay(16, cancellationToken);
                    continue;
                }

                var requestedRate = Volatile.Read(ref _playbackRateMultiplier);
                if (requestedRate != currentRate)
                {
                    currentRate = requestedRate;
                    nextFrameAt = clock.Elapsed;
                }

                var frameInterval = GetFrameInterval(currentRate);
                var producedFrame = false;
                frameNumber++;
                lock (_machineLock)
                {
                    if (!_isPaused && HasMachine)
                    {
                        lock (_presentationLock)
                        {
                            // A Nintendo 64 frame can change resolution while
                            // it runs, so reserve the largest image the video
                            // interface can produce before stepping it.
                            var requiredPixels = _n64Machine is not null
                                ? N64Machine.MaximumWidth * N64Machine.MaximumHeight
                                : MachineWidth * MachineHeight;
                            if (_presentationPixels is null || _presentationPixels.Length < requiredPixels)
                            {
                                _presentationPixels = new uint[requiredPixels];
                            }

                            RunMachineFrame(_presentationPixels);
                            _presentationFrameNumber = frameNumber;
                        }

                        producedFrame = true;
                    }
                }

                if (!producedFrame)
                {
                    frameNumber--;
                    continue;
                }

                if (frameNumber % 300 == 0)
                {
                    lock (_machineLock)
                    {
                        TryFlushBatterySave();
                    }
                }

                ScheduleFramePresentation();

                nextFrameAt += frameInterval;
                if (_n64Machine is not null && currentRate == 1)
                {
                    nextFrameAt = _n64FrameRateEnforcer.BoundCatchUp(
                        clock.Elapsed,
                        nextFrameAt,
                        frameInterval);
                }
                var remaining = nextFrameAt - clock.Elapsed;
                if (remaining > TimeSpan.FromMilliseconds(1))
                {
                    await Task.Delay(remaining, cancellationToken);
                }
                else if (remaining < TimeSpan.FromMilliseconds(-250))
                {
                    nextFrameAt = clock.Elapsed;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ShowError(exception));
        }
    }

    private void ScheduleFramePresentation()
    {
        if (Interlocked.CompareExchange(ref _presentationScheduled, 1, 0) == 0)
        {
            Dispatcher.UIThread.Post(PresentLatestFrame, DispatcherPriority.Render);
        }
    }

    private void PresentLatestFrame()
    {
        var presentedFrameNumber = 0;
        var hasUsefulImage = false;

        try
        {
            if (_frameBitmap is null || _presentationPixels is null)
            {
                return;
            }

            lock (_presentationLock)
            {
                presentedFrameNumber = _presentationFrameNumber;
                CopyFrameToBitmap(_presentationPixels);
                hasUsefulImage = HasUsefulImage(_presentationPixels);
            }

            CompleteFramePresentation(presentedFrameNumber, hasUsefulImage);
        }
        finally
        {
            Volatile.Write(ref _presentationScheduled, 0);
            if (!_cancellation.IsCancellationRequested &&
                Volatile.Read(ref _presentationFrameNumber) > presentedFrameNumber)
            {
                ScheduleFramePresentation();
            }
        }
    }

    private unsafe void PresentFrame(uint[] pixels, int frameNumber)
    {
        CopyFrameToBitmap(pixels);
        CompleteFramePresentation(frameNumber, HasUsefulImage(pixels));
    }

    private unsafe void CopyFrameToBitmap(uint[] pixels)
    {
        if (_nesMachine is not null && PixelDeckSettingsStore.Current.HideNesHorizontalOverscan)
        {
            NesFramePresentation.MaskHorizontalOverscan(pixels, MachineWidth, MachineHeight);
        }

        // The Nintendo 64 video interface can be reprogrammed at any time, so
        // the target bitmap has to follow the machine's live output size.
        var width = MachineWidth;
        var height = MachineHeight;
        if (_frameBitmap is null ||
            _frameBitmap.PixelSize.Width != width ||
            _frameBitmap.PixelSize.Height != height)
        {
            _frameBitmap?.Dispose();
            _frameBitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);
            FrameImage.Source = _frameBitmap;
        }

        var bitmap = _frameBitmap;
        using (var framebuffer = bitmap.Lock())
        {
            fixed (uint* source = pixels)
            {
                var sourceRowBytes = width * sizeof(uint);
                var rows = Math.Min(height, pixels.Length / Math.Max(width, 1));
                for (var row = 0; row < rows; row++)
                {
                    var sourceRow = source + (row * width);
                    var destinationRow = (byte*)framebuffer.Address + (row * framebuffer.RowBytes);
                    Buffer.MemoryCopy(sourceRow, destinationRow, framebuffer.RowBytes, sourceRowBytes);
                }
            }
        }
    }

    private void CompleteFramePresentation(int frameNumber, bool hasUsefulImage)
    {
        var bitmap = _frameBitmap ?? throw new InvalidOperationException("The emulator display is not initialized.");
        FrameImage.InvalidateVisual();

        if (_n64Machine is not null && !hasUsefulImage)
        {
            EmulatorStatusText.Text = "PIXEL64 BOOTING - WAITING FOR VIDEO";
            LoadingOverlay.IsVisible = true;
            return;
        }

        LoadingOverlay.IsVisible = false;

        if (_gameCubeMachine is not null)
        {
            // No video hardware, so the session panel is the whole picture.
            // Refreshed here rather than every frame: its numbers move slowly.
            if (frameNumber % 30 == 1)
            {
                UpdatePixelCubeOverlay();
            }

            return;
        }

        // A picture the player chose themselves outranks the automatic one, so
        // games that already have a library image are left alone.
        if (_game is not null &&
            !_game.HasChosenLibraryImage &&
            !_screenshotSaved &&
            frameNumber >= 120 &&
            hasUsefulImage)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_game.ScreenshotCachePath)!);
            bitmap.Save(_game.ScreenshotCachePath, PngBitmapEncoderOptions.Default);
            _screenshotSaved = true;
        }
    }

    private void PollInput(object? sender, EventArgs eventArgs)
    {
        var playerOneState = _gamepad.ReadState();
        var playerTwoState = _playerTwoGamepad.ReadState();
        var playerOneGamepad = playerOneState.Buttons;
        var playerTwoGamepad = playerTwoState.Buttons;
        var playerOneNewPresses = playerOneGamepad & ~_previousGamepadButtons;
        var playerTwoNewPresses = playerTwoGamepad & ~_previousPlayerTwoGamepadButtons;
        var playerOnePauseChord =
            playerOneGamepad.HasFlag(GamepadButton.Back) &&
            playerOneGamepad.HasFlag(GamepadButton.Start);
        var playerTwoPauseChord =
            playerTwoGamepad.HasFlag(GamepadButton.Back) &&
            playerTwoGamepad.HasFlag(GamepadButton.Start);
        var pauseRequested =
            playerOneNewPresses.HasFlag(GamepadButton.Guide) ||
            playerTwoNewPresses.HasFlag(GamepadButton.Guide) ||
            (playerOnePauseChord && !_pauseChordHeld) ||
            (playerTwoPauseChord && !_playerTwoPauseChordHeld);
        _pauseChordHeld = playerOnePauseChord;
        _playerTwoPauseChordHeld = playerTwoPauseChord;
        _previousGamepadButtons = playerOneGamepad;
        _previousPlayerTwoGamepadButtons = playerTwoGamepad;

        if (pauseRequested)
        {
            SetPaused(!_isPaused);
            return;
        }

        SetFastForward(
            !_isPaused &&
            SupportsFastForward &&
            (playerOneGamepad.HasFlag(GamepadButton.RightTrigger) ||
             playerTwoGamepad.HasFlag(GamepadButton.RightTrigger)));

        if (_isPaused)
        {
            var newPresses = playerOneNewPresses | playerTwoNewPresses;
            if (newPresses.HasFlag(GamepadButton.DPadUp)) MoveMenuSelection(-1);
            if (newPresses.HasFlag(GamepadButton.DPadDown)) MoveMenuSelection(1);
            if (newPresses.HasFlag(GamepadButton.A)) ExecuteSelectedMenuAction();
            if (newPresses.HasFlag(GamepadButton.B)) HandlePauseBack();
            return;
        }

        lock (_machineLock)
        {
            if (!HasMachine)
            {
                return;
            }

            var settings = PixelDeckSettingsStore.Current;
            if (_nesMachine is not null)
            {
                var playerOneButtons =
                    GamepadInputMapper.ToNesButtons(playerOneGamepad, settings) |
                    ReadNesKeyboardButtons();
                var playerTwoButtons = GamepadInputMapper.ToNesButtons(
                    playerTwoGamepad,
                    settings,
                    playerTwo: true);
                _nesMachine.SetControllerState(1, playerOneButtons);
                _nesMachine.SetControllerState(2, playerTwoButtons);
            }
            else if (_snesMachine is not null)
            {
                var playerOneButtons =
                    GamepadInputMapper.ToSnesButtons(playerOneGamepad, settings) |
                    ReadSnesKeyboardButtons();
                var playerTwoButtons = GamepadInputMapper.ToSnesButtons(
                    playerTwoGamepad,
                    settings,
                    playerTwo: true);
                _snesMachine.SetControllerState(1, playerOneButtons);
                _snesMachine.SetControllerState(2, playerTwoButtons);
            }
            else if (_n64Machine is not null)
            {
                UpdateN64Controllers(settings, playerOneState, playerTwoState);
            }
        }
    }

    /// <summary>
    /// Drives all four Nintendo 64 ports from their assigned pads. Ports without a pad are held
    /// neutral rather than reported empty: the PIF can report an empty port, but doing so stalls
    /// Super Mario 64, so presence reporting stays off until that is resolved.
    /// </summary>
    private void UpdateN64Controllers(
        PixelDeckSettings settings,
        GamepadState playerOneState,
        GamepadState playerTwoState)
    {
        if (_n64Machine is null)
        {
            return;
        }

        var playerOneController = GamepadInputMapper.ToN64Controller(
            playerOneState,
            GamepadInputMapper.N64MapForPort(settings, 1));
        var keyboardController = ReadN64KeyboardController();
        _n64Machine.SetControllerState(
            1,
            new N64ControllerState(
                playerOneController.Buttons | keyboardController.Buttons,
                keyboardController.StickX != 0 ? keyboardController.StickX : playerOneController.StickX,
                keyboardController.StickY != 0 ? keyboardController.StickY : playerOneController.StickY));

        var readers = new[] { _playerTwoGamepad, _playerThreeGamepad, _playerFourGamepad };
        var states = new[]
        {
            playerTwoState,
            _playerThreeGamepad.ReadState(),
            _playerFourGamepad.ReadState()
        };

        for (var offset = 0; offset < readers.Length; offset++)
        {
            _n64Machine.SetControllerState(
                offset + 2,
                readers[offset].IsConnected
                    ? GamepadInputMapper.ToN64Controller(
                        states[offset],
                        GamepadInputMapper.N64MapForPort(settings, offset + 2))
                    : N64ControllerState.Neutral);
        }
    }

    private N64ControllerState ReadN64KeyboardController()
    {
        var buttons = N64Button.None;
        if (_pressedKeys.Contains(Key.Z)) buttons |= N64Button.A;
        if (_pressedKeys.Contains(Key.X)) buttons |= N64Button.B;
        if (_pressedKeys.Contains(Key.Enter)) buttons |= N64Button.Start;
        if (_pressedKeys.Contains(Key.A)) buttons |= N64Button.Z;
        if (_pressedKeys.Contains(Key.Q)) buttons |= N64Button.L;
        if (_pressedKeys.Contains(Key.W)) buttons |= N64Button.R;
        var stickX = _pressedKeys.Contains(Key.Left)
            ? (sbyte)-80
            : _pressedKeys.Contains(Key.Right) ? (sbyte)80 : (sbyte)0;
        var stickY = _pressedKeys.Contains(Key.Down)
            ? (sbyte)-80
            : _pressedKeys.Contains(Key.Up) ? (sbyte)80 : (sbyte)0;
        return new N64ControllerState(buttons, stickX, stickY);
    }

    private NesButton ReadNesKeyboardButtons()
    {
        var buttons = NesButton.None;
        if (_pressedKeys.Contains(Key.Z)) buttons |= NesButton.A;
        if (_pressedKeys.Contains(Key.X)) buttons |= NesButton.B;
        if (_pressedKeys.Contains(Key.Enter)) buttons |= NesButton.Start;
        if (_pressedKeys.Contains(Key.LeftShift) || _pressedKeys.Contains(Key.RightShift)) buttons |= NesButton.Select;
        if (_pressedKeys.Contains(Key.Up)) buttons |= NesButton.Up;
        if (_pressedKeys.Contains(Key.Down)) buttons |= NesButton.Down;
        if (_pressedKeys.Contains(Key.Left)) buttons |= NesButton.Left;
        if (_pressedKeys.Contains(Key.Right)) buttons |= NesButton.Right;
        return buttons;
    }

    private SnesButton ReadSnesKeyboardButtons()
    {
        var buttons = SnesButton.None;
        if (_pressedKeys.Contains(Key.Z)) buttons |= SnesButton.A;
        if (_pressedKeys.Contains(Key.X)) buttons |= SnesButton.B;
        if (_pressedKeys.Contains(Key.A)) buttons |= SnesButton.X;
        if (_pressedKeys.Contains(Key.S)) buttons |= SnesButton.Y;
        if (_pressedKeys.Contains(Key.Q)) buttons |= SnesButton.L;
        if (_pressedKeys.Contains(Key.W)) buttons |= SnesButton.R;
        if (_pressedKeys.Contains(Key.Enter)) buttons |= SnesButton.Start;
        if (_pressedKeys.Contains(Key.LeftShift) || _pressedKeys.Contains(Key.RightShift)) buttons |= SnesButton.Select;
        if (_pressedKeys.Contains(Key.Up)) buttons |= SnesButton.Up;
        if (_pressedKeys.Contains(Key.Down)) buttons |= SnesButton.Down;
        if (_pressedKeys.Contains(Key.Left)) buttons |= SnesButton.Left;
        if (_pressedKeys.Contains(Key.Right)) buttons |= SnesButton.Right;
        return buttons;
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            if (_isPaused)
            {
                HandlePauseBack();
            }
            else
            {
                SetPaused(true);
            }

            eventArgs.Handled = true;
            return;
        }

        if (_isPaused)
        {
            switch (eventArgs.Key)
            {
                case Key.Up:
                    MoveMenuSelection(-1);
                    break;
                case Key.Down:
                    MoveMenuSelection(1);
                    break;
                case Key.Enter:
                case Key.Space:
                    ExecuteSelectedMenuAction();
                    break;
            }

            eventArgs.Handled = true;
            return;
        }

        _pressedKeys.Add(eventArgs.Key);
    }

    private void OnKeyUp(object? sender, KeyEventArgs eventArgs) => _pressedKeys.Remove(eventArgs.Key);

    private void SetPaused(bool paused)
    {
        _isPaused = paused;
        SetFastForward(false);
        if (paused)
        {
            _playSession?.Stop();
        }
        else
        {
            _playSession?.Start();
        }

        PauseOverlay.IsVisible = paused;
        _pressedKeys.Clear();
        if (_audioOutput is not null)
        {
            _audioOutput.IsPaused = paused;
        }

        lock (_machineLock)
        {
            _nesMachine?.SetControllerState(1, NesButton.None);
            _nesMachine?.SetControllerState(2, NesButton.None);
            _snesMachine?.SetControllerState(1, SnesButton.None);
            _snesMachine?.SetControllerState(2, SnesButton.None);
            _n64Machine?.SetControllerState(1, N64ControllerState.Neutral);
            _n64Machine?.SetControllerState(2, N64ControllerState.Neutral);
            if (paused)
            {
                _nesMachine?.ClearAudioSamples();
                _snesMachine?.ClearAudioSamples();
                _n64Machine?.ClearAudioSamples();
            }
        }

        if (paused)
        {
            ShowMainPauseMenu();
        }
        else
        {
            _pauseMenuMode = PauseMenuMode.Main;
            MainPauseMenuPanel.IsVisible = true;
            StateSlotMenuScroll.IsVisible = false;
            Focus();
        }
    }

    /// <summary>
    /// Whether the right trigger runs the game at double speed. GameCube
    /// sessions are excluded deliberately: PixelCube is nowhere near real
    /// time, so a rate multiplier would not speed anything up, and holding the
    /// trigger would change nothing except the pacing the trace is measured
    /// against.
    /// </summary>
    private bool SupportsFastForward => _gameCubeMachine is null;

    private void SetFastForward(bool enabled)
    {
        var multiplier = enabled ? 2 : 1;
        if (Interlocked.Exchange(ref _playbackRateMultiplier, multiplier) == multiplier)
        {
            return;
        }

        if (_audioOutput is not null)
        {
            _audioOutput.PlaybackRate = multiplier;
            _audioOutput.IsPaused = _isPaused;
        }
    }

    private void MoveMenuSelection(int direction)
    {
        var buttons = ActiveMenuButtons;
        if (buttons.Length == 0)
        {
            return;
        }

        for (var attempts = 0; attempts < buttons.Length; attempts++)
        {
            _menuIndex = (_menuIndex + direction + buttons.Length) % buttons.Length;
            if (buttons[_menuIndex].IsEffectivelyEnabled)
            {
                buttons[_menuIndex].Focus();
                buttons[_menuIndex].BringIntoView();
                return;
            }
        }
    }

    private void SetMenuSelection(int index)
    {
        var buttons = ActiveMenuButtons;
        if (buttons.Length == 0)
        {
            _menuIndex = -1;
            return;
        }

        _menuIndex = Math.Clamp(index, 0, buttons.Length - 1);
        if (!buttons[_menuIndex].IsEffectivelyEnabled)
        {
            MoveMenuSelection(1);
            return;
        }

        buttons[_menuIndex].Focus();
        buttons[_menuIndex].BringIntoView();
    }

    private void ExecuteSelectedMenuAction()
    {
        if (_pauseMenuMode != PauseMenuMode.Main)
        {
            if (_menuIndex >= 0 && _menuIndex < _stateMenuActions.Count)
            {
                _stateMenuActions[_menuIndex]();
            }

            return;
        }

        switch (_menuIndex)
        {
            case 0: Resume(); break;
            case 1: OpenSaveStateMenu(); break;
            case 2: OpenLoadStateMenu(); break;
            case 3: CaptureLibraryImage(); break;
            case 4: ResetGame(); break;
            case 5: Close(); break;
        }
    }

    private void Resume() => SetPaused(false);

    /// <summary>
    /// Stores the frame the player paused on as this game's library image.
    /// </summary>
    /// <remarks>
    /// The pause menu is a separate control composited over the video, so the
    /// emulator's own bitmap already excludes it â€” no need to hide the overlay
    /// or re-run the machine to get a clean capture.
    /// </remarks>
    private void CaptureLibraryImage()
    {
        if (_game is null || _frameBitmap is null || _game.LibraryImagePath.Length == 0)
        {
            StateStatusText.Text = "NO FRAME TO CAPTURE YET";
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_game.LibraryImagePath)!);
            _frameBitmap.Save(_game.LibraryImagePath, PngBitmapEncoderOptions.Default);
            _game.AdoptCapturedLibraryImage();

            // The chosen picture now wins, so the automatic screenshot must not
            // overwrite it later in this session either.
            _screenshotSaved = true;
            StateStatusText.Text = "LIBRARY IMAGE UPDATED";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StateStatusText.Text = $"LIBRARY IMAGE FAILED  Â·  {exception.Message}".ToUpperInvariant();
        }
    }

    private void HandlePauseBack()
    {
        if (_pauseMenuMode == PauseMenuMode.Main)
        {
            Resume();
            return;
        }

        if (_pauseMenuMode == PauseMenuMode.ConfirmOverwrite)
        {
            OpenSaveStateMenu();
        }
        else
        {
            ShowMainPauseMenu();
        }
    }

    private void ShowMainPauseMenu(bool preserveStatus = false)
    {
        _pauseMenuMode = PauseMenuMode.Main;
        PauseHeadingText.Text = "Paused";
        MainPauseMenuPanel.IsVisible = true;
        StateSlotMenuScroll.IsVisible = false;
        StateSlotMenuPanel.Children.Clear();
        _stateMenuActions.Clear();
        UpdateStateAvailability(preserveStatus);
        SetMenuSelection(0);
    }

    private void OpenSaveStateMenu()
    {
        try
        {
            var slots = StateCatalog.GetSlots();
            BeginStateSlotMenu(PauseMenuMode.SaveSlots, "Save state");
            AddStateSlotMenuChoice(
                slots.Count == 0 ? "Save new state slot" : "New state slot",
                SaveNewState);
            foreach (var slot in slots)
            {
                var capturedSlot = slot;
                AddStateSlotMenuChoice(
                    capturedSlot.MenuText,
                    () => OpenOverwriteConfirmation(capturedSlot));
            }

            StateStatusText.Text = slots.Count == 0
                ? "NO SAVED STATES YET"
                : "SELECT A SLOT TO OVERWRITE, OR CREATE A NEW ONE";
            SetMenuSelection(0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StateStatusText.Text = $"STATE LIST FAILED  Â·  {exception.Message}".ToUpperInvariant();
        }
    }

    private void OpenLoadStateMenu()
    {
        try
        {
            var slots = StateCatalog.GetSlots();
            if (slots.Count == 0)
            {
                UpdateStateAvailability();
                return;
            }

            BeginStateSlotMenu(PauseMenuMode.LoadSlots, "Load state");
            foreach (var slot in slots)
            {
                var capturedSlot = slot;
                AddStateSlotMenuChoice(capturedSlot.MenuText, () => LoadState(capturedSlot));
            }

            StateStatusText.Text = "SELECT A STATE TO LOAD";
            SetMenuSelection(0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StateStatusText.Text = $"STATE LIST FAILED  Â·  {exception.Message}".ToUpperInvariant();
        }
    }

    private void OpenOverwriteConfirmation(SaveStateSlot slot)
    {
        BeginStateSlotMenu(PauseMenuMode.ConfirmOverwrite, $"Overwrite slot {slot.Number}?");
        AddStateSlotMenuChoice("Cancel", OpenSaveStateMenu);
        AddStateSlotMenuChoice(
            $"Overwrite slot {slot.Number}",
            () => SaveState(slot),
            danger: true);
        StateStatusText.Text = "THE PREVIOUS STATE IN THIS SLOT WILL BE REPLACED";
        SetMenuSelection(0);
    }

    private void BeginStateSlotMenu(PauseMenuMode mode, string heading)
    {
        _pauseMenuMode = mode;
        PauseHeadingText.Text = heading;
        MainPauseMenuPanel.IsVisible = false;
        StateSlotMenuScroll.IsVisible = true;
        StateSlotMenuScroll.Offset = Vector.Zero;
        StateSlotMenuPanel.Children.Clear();
        _stateMenuActions.Clear();
    }

    private void AddStateSlotMenuChoice(string label, Action action, bool danger = false)
    {
        var button = new Button
        {
            Content = label,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        button.Classes.Add("guide-item");
        if (danger)
        {
            button.Classes.Add("danger");
        }

        button.Click += (_, _) => action();
        StateSlotMenuPanel.Children.Add(button);
        _stateMenuActions.Add(action);
    }

    private void SaveNewState()
    {
        try
        {
            SaveState(StateCatalog.CreateNextSlot());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StateStatusText.Text = $"SAVE FAILED  Â·  {exception.Message}".ToUpperInvariant();
        }
    }

    private void SaveState(SaveStateSlot slot)
    {
        try
        {
            byte[] state;
            lock (_machineLock)
            {
                state = SaveMachineState();
            }

            CrashSafeFile.WriteAllBytes(slot.Path, state);
            StateStatusText.Text =
                $"SLOT {slot.Number} SAVED  Â·  {DateTime.Now:h:mm tt}".ToUpperInvariant();
            ShowMainPauseMenu(preserveStatus: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StateStatusText.Text = $"SAVE FAILED  Â·  {exception.Message}".ToUpperInvariant();
        }
    }

    private void LoadState(SaveStateSlot slot)
    {
        try
        {
            var candidates = CrashSafeFile.GetReadCandidates(slot.Path);
            if (candidates.Count == 0)
            {
                throw new FileNotFoundException("No saved state is available.", slot.Path);
            }

            byte[] rollbackState;
            lock (_machineLock)
            {
                rollbackState = SaveMachineState();
            }

            Exception? loadFailure = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    var state = File.ReadAllBytes(candidate);
                    uint[] frame;
                    lock (_machineLock)
                    {
                        LoadMachineState(state);
                        frame = GetCurrentMachineFrame();
                    }

                    if (!string.Equals(candidate, slot.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        CrashSafeFile.CommitTemporary(slot.Path);
                    }

                    PresentFrame(frame, 0);
                    StateStatusText.Text = $"SLOT {slot.Number} LOADED";
                    ShowMainPauseMenu(preserveStatus: true);
                    return;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                {
                    loadFailure = exception;
                    lock (_machineLock)
                    {
                        LoadMachineState(rollbackState);
                    }
                }
            }

            throw new InvalidDataException("No valid saved-state copy could be recovered.", loadFailure);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            StateStatusText.Text = $"LOAD FAILED  Â·  {exception.Message}".ToUpperInvariant();
        }
    }

    private void ResetGame()
    {
        try
        {
            lock (_machineLock)
            {
                LoadMachine();
                if (_audioOutput is not null)
                {
                    if (_nesMachine is not null)
                    {
                        _audioOutput.SetMachine(_nesMachine);
                    }
                    else if (_snesMachine is not null)
                    {
                        _audioOutput.SetMachine(_snesMachine);
                    }
                    else if (_n64Machine is not null)
                    {
                        _audioOutput.SetMachine(_n64Machine);
                    }
                }
            }

            LoadingOverlay.IsVisible = true;
            EmulatorStatusText.Text = "POWERING ON";
            Resume();
        }
        catch (Exception exception)
        {
            StateStatusText.Text = $"RESET FAILED  Â·  {exception.Message}".ToUpperInvariant();
        }
    }

    private void UpdateStateAvailability(bool preserveStatus = false)
    {
        try
        {
            if (_gameCubeMachine is not null)
            {
                // Nothing executes yet, so there is no state to keep.
                SaveStateButton.IsEnabled = false;
                LoadStateButton.IsEnabled = false;
                LibraryImageButton.IsEnabled = false;
                if (!preserveStatus)
                {
                    StateStatusText.Text = "PIXELCUBE HAS NO EXECUTION STATE TO SAVE YET";
                }

                return;
            }

            IReadOnlyList<SaveStateSlot> slots = _game is null ? [] : StateCatalog.GetSlots();
            LoadStateButton.IsEnabled = slots.Count > 0;
            if (!preserveStatus)
            {
                StateStatusText.Text = slots.Count == 0
                    ? "NO SAVED STATES YET"
                    : $"{slots.Count} SAVED {(slots.Count == 1 ? "STATE" : "STATES")}  Â·  " +
                      $"LAST {slots.Max(slot => slot.LastWriteTime):MMM d, h:mm tt}".ToUpperInvariant();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LoadStateButton.IsEnabled = false;
            if (!preserveStatus)
            {
                StateStatusText.Text = $"STATE LIST FAILED  Â·  {exception.Message}".ToUpperInvariant();
            }
        }
    }

    private bool HasMachine =>
        _nesMachine is not null ||
        _snesMachine is not null ||
        _n64Machine is not null ||
        _gameCubeMachine is not null;

    private int MachineWidth =>
        _nesMachine?.Width ?? _snesMachine?.Width ?? _n64Machine?.Width ?? _gameCubeMachine?.Width
        ?? throw new InvalidOperationException("The emulator is not running.");

    private int MachineHeight =>
        _nesMachine?.Height ?? _snesMachine?.Height ?? _n64Machine?.Height ?? _gameCubeMachine?.Height
        ?? throw new InvalidOperationException("The emulator is not running.");

    private double MachineFramesPerSecond =>
        _snesMachine?.FramesPerSecond ??
        _n64Machine?.FramesPerSecond ??
        _gameCubeMachine?.FramesPerSecond ??
        60.0988;

    private TimeSpan GetFrameInterval(int playbackRate)
    {
        var useAudioClock = HasMachine && _audioOutput?.IsAvailable == true;
        var bufferedSampleValues =
            _nesMachine?.BufferedAudioSampleCount ??
            _snesMachine?.BufferedAudioSampleCount ??
            _n64Machine?.BufferedAudioSampleCount ??
            0;
        var sampleRate = _snesMachine is not null
            ? SnesMachine.AudioSampleRate
            : _n64Machine is not null
                ? N64Machine.AudioSampleRate
                : NesMachine.AudioSampleRate;
        var channels = _snesMachine is not null || _n64Machine is not null ? 2 : 1;
        return _audioBufferSynchronizer.GetFrameInterval(
            MachineFramesPerSecond,
            playbackRate,
            useAudioClock,
            bufferedSampleValues,
            sampleRate,
            channels);
    }

    private void LoadMachine()
    {
        var path = _game?.FullPath ?? throw new InvalidOperationException("No game was selected.");
        var extension = Path.GetExtension(path);
        TryFlushBatterySave();
        _nesMachine = null;
        _snesMachine = null;
        _n64Machine = null;
        _gameCubeMachine?.Dispose();
        _gameCubeMachine = null;
        _gameCubeFrameCounter = 0;
        _gameCubeStopped = false;
        _gameCubeResult = default;
        _gameCubeLastPc = 0;
        _gameCubeStallFrames = 0;
        _loggedN64Width = 0;
        _loggedN64Height = 0;
        _loggedN64Control = 0;
        _loggedN64HorizontalVideo = uint.MaxValue;
        _n64FrameCounter = 0;
        StopAllRumble();

        // GameCube discs are chosen by platform rather than by extension: an
        // .iso only means GameCube because the player filed it under the
        // GameCube folder, and the library has already settled that question.
        if (string.Equals(_game.PlatformCode, "GC", StringComparison.Ordinal))
        {
            _gameCubeMachine = GameCubeMachine.Load(path, PixelCubeDiagnostics.Log);
            _gameCubeMachine.TraceStartupReport();

            var header = _gameCubeMachine.Disc.Header;
            EmulatorDiagnostics.Write(
                $"GameCube disc: title=\"{header.Title}\" id={header.GameId} " +
                $"region={header.RegionText} container={_gameCubeMachine.Disc.ContainerName} " +
                $"entry=0x{_gameCubeMachine.EntryPoint:X8}");
            Title = $"{Title} - PixelCube trace only";
            return;
        }

        if (string.Equals(extension, ".nes", StringComparison.OrdinalIgnoreCase))
        {
            _nesMachine = NesMachine.Load(
                path,
                _game.SaveRamPath,
                new NesEmulationOptions
                {
                    RemoveSpriteLimit = PixelDeckSettingsStore.Current.RemoveNesSpriteLimit,
                    Mmc3IrqRevision = PixelDeckSettingsStore.Current.Mmc3IrqRevision,
                    PpuRevision = PixelDeckSettingsStore.Current.NesPpuRevision,
                    EnableOamDecay = PixelDeckSettingsStore.Current.EnableNesOamDecay,
                    OamCorruptionMode = PixelDeckSettingsStore.Current.NesOamCorruptionMode
                });
            EmulatorDiagnostics.Write(
                $"NES cartridge: mapper={_nesMachine.MapperNumber}/{_nesMachine.SubmapperNumber} " +
                $"battery={_nesMachine.Cartridge.HasBatteryBackedRam} " +
                $"input={_nesMachine.Cartridge.DefaultInputDevice}");
            return;
        }

        if (string.Equals(extension, ".sfc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".smc", StringComparison.OrdinalIgnoreCase))
        {
            _snesMachine = SnesMachine.Load(path, _game.SaveRamPath);
            EmulatorDiagnostics.Write(
                $"SNES cartridge: sa1={_snesMachine.HasSa1} superfx={_snesMachine.HasSuperFx} " +
                $"sdd1={_snesMachine.HasSdd1} size={_snesMachine.Width}x{_snesMachine.Height}");
            return;
        }

        if (string.Equals(extension, ".z64", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".v64", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".n64", StringComparison.OrdinalIgnoreCase))
        {
            _n64Machine = N64Machine.Load(path, _game.SaveRamPath);

            EmulatorDiagnostics.Write(
                $"N64 cartridge: title=\"{_n64Machine.Cartridge.Title}\" " +
                $"code={_n64Machine.Cartridge.GameCode} revision={_n64Machine.Cartridge.Revision} " +
                $"cic={_n64Machine.Cartridge.Cic} " +
                $"entry=0x{_n64Machine.Cartridge.EffectiveEntryPoint:X8} " +
                $"region={_n64Machine.Cartridge.VideoRegion} save={_n64Machine.Cartridge.SaveType}");

            // The backend choice is made silently inside N64Machine, which falls
            // back to the software renderer whenever paraLLEl-RDP cannot be
            // loaded or the Vulkan device fails preflight. Surfacing the reason
            // is the difference between knowing which renderer is running and
            // inferring it from the frame rate.
            EmulatorDiagnostics.Write(
                $"N64 graphics backend: {_n64Machine.GraphicsBackendStatus}");
            Title = $"{Title} - {_n64Machine.GraphicsBackendStatus}";
            return;
        }

        throw new NotSupportedException($"PixelDeck cannot emulate {extension} games yet.");
    }

    private void TryFlushBatterySave()
    {
        try
        {
            _nesMachine?.FlushBatterySave();
            _snesMachine?.FlushBatterySave();
            _n64Machine?.FlushBatterySave();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not save cartridge battery RAM: {exception.Message}");
        }
    }

    private void RunMachineFrame(uint[] destination)
    {
        if (_nesMachine is not null)
        {
            _nesMachine.RunFrame().CopyTo(destination);
            LogNesStatisticsPeriodically(destination);
            return;
        }

        if (_snesMachine is not null)
        {
            _snesMachine.RunFrame().CopyTo(destination);
            LogSnesStatisticsPeriodically(destination);
            return;
        }

        if (_n64Machine is not null)
        {
            _n64Machine.RunFrame().CopyTo(destination);
            LogN64VideoStateWhenItChanges();
            LogN64RenderStatisticsPeriodically();
            ApplyN64RumbleState();
            return;
        }

        if (_gameCubeMachine is not null)
        {
            RunGameCubeFrame(destination);
            return;
        }

        throw new InvalidOperationException("The emulator is not running.");
    }

    /// <summary>
    /// A GameCube frame: run the interpreter until it stops, then hold. There
    /// is nothing to draw — no graphics hardware exists — so the session
    /// panel and the trace file are the whole output.
    /// </summary>
    /// <remarks>
    /// Execution is capped per frame rather than run to completion, so a game
    /// that spins forever still leaves the dashboard responsive and still
    /// updates its counters. Once the CPU stops on something it cannot do,
    /// nothing further is attempted: repeating a failed instruction sixty
    /// times a second would bury the trace under one obstacle.
    /// </remarks>
    private void RunGameCubeFrame(uint[] destination)
    {
        if (_gameCubeMachine is null)
        {
            return;
        }

        _gameCubeFrameCounter++;
        _gameCubeMachine.Trace.Frame = _gameCubeFrameCounter;
        destination.AsSpan(0, MachineWidth * MachineHeight).Clear();

        if (_gameCubeStopped)
        {
            return;
        }

        // Run for a slice of wall-clock time rather than a fixed instruction
        // count. Nothing is drawn, so the only thing a frame is really for is
        // keeping the dashboard responsive — and a fixed count left the core
        // running at a fraction of the speed the headless harness manages,
        // which meant minutes of waiting to reach a state that takes seconds.
        var deadline = Stopwatch.GetTimestamp() +
            (long)(Stopwatch.Frequency * GameCubeFrameBudget.TotalSeconds);
        do
        {
            _gameCubeResult = _gameCubeMachine.Run(GameCubeInstructionsPerSlice);
            if (_gameCubeResult.Outcome != GekkoOutcome.Completed)
            {
                _gameCubeStopped = true;
                EmulatorDiagnostics.Write(
                    $"PixelCube stopped: {_gameCubeResult.Outcome} at 0x{_gameCubeResult.Pc:X8} " +
                    $"after {_gameCubeMachine.Cpu.InstructionsExecuted:N0} instructions " +
                    $"({GekkoDisassembler.Describe(_gameCubeResult.Instruction, _gameCubeResult.Pc)})");
                return;
            }
        }
        while (Stopwatch.GetTimestamp() < deadline);

        TrackGameCubeProgress();

        // One line a second while it is still running, so a long session shows
        // progress without the log becoming the session.
        _gameCubeMachine.Trace.WriteEvery(
            GameCubeTraceChannel.Performance,
            GameCubeTraceLevel.Information,
            "session/heartbeat",
            (long)Math.Round(_gameCubeMachine.FramesPerSecond),
            $"{(IsGameCubeStalled ? "spinning" : "running")}: frame={_gameCubeFrameCounter} " +
            $"instructions={_gameCubeMachine.Cpu.InstructionsExecuted} " +
            $"pc=0x{_gameCubeMachine.Cpu.Pc:X8} " +
            $"collapsed={_gameCubeMachine.Trace.SuppressedCount}");
    }

    /// <summary>
    /// Notices when the program counter stops moving.
    /// </summary>
    /// <remarks>
    /// Without this the session reports itself as "running" while it burns
    /// millions of instructions inside a four-instruction poll, which is the
    /// most misleading thing the panel could say — a stall and healthy
    /// progress look identical from a frame counter alone. The address is
    /// already known; only the comparison was missing.
    /// </remarks>
    private void TrackGameCubeProgress()
    {
        if (_gameCubeMachine is null)
        {
            return;
        }

        // Compared within a window rather than exactly. A spin loop is a
        // handful of instructions, so the frame boundary lands on a different
        // one each time and an exact comparison never fires — the very case
        // this exists to catch.
        var pc = _gameCubeMachine.Cpu.Pc;
        if (pc < _gameCubeLastPc - GameCubeStallWindow ||
            pc > _gameCubeLastPc + GameCubeStallWindow)
        {
            _gameCubeLastPc = pc;
            _gameCubeStallFrames = 0;
            return;
        }

        _gameCubeStallFrames++;
        if (_gameCubeStallFrames != GameCubeStallFrames)
        {
            return;
        }

        // Remembered so the panel can tell the two kinds of stall apart. A
        // program counter that stops advancing means either the game is
        // waiting on hardware or it is deep in a long loop, and only whether
        // something is being polled distinguishes them — a cache flush over
        // sixty-four megabytes looks exactly like a hang from outside.
        var busiest = _gameCubeMachine.Trace.BusiestCounter();
        _gameCubeStallBusiestCount = busiest.Count;
        _gameCubeMachine.Trace.Write(
            GameCubeTraceChannel.Performance,
            GameCubeTraceLevel.Warning,
            $"execution has made no progress for {GameCubeStallFrames} frames at " +
            $"0x{pc:X8}; busiest key is {busiest.Key} at {busiest.Count:N0}");

        // The code it is stuck in, for the same reason a stop dumps it: a
        // spin is diagnosed by reading the loop, and a stall never reaches the
        // stop path that was already printing this.
        _gameCubeMachine.TraceDisassemblyAround(pc);
        EmulatorDiagnostics.Write(
            $"PixelCube stalled at 0x{pc:X8} after " +
            $"{_gameCubeMachine.Cpu.InstructionsExecuted:N0} instructions; " +
            $"busiest key {busiest.Key} at {busiest.Count:N0}");
    }

    /// <summary>Whether the program counter has stopped advancing.</summary>
    private bool IsGameCubeStalled => _gameCubeStallFrames >= GameCubeStallFrames;

    /// <summary>
    /// Fills the GameCube session panel and shows it. Called once per second
    /// from the input timer, because the numbers on it change slowly and the
    /// panel is the only thing a player can see.
    /// </summary>
    private void UpdatePixelCubeOverlay()
    {
        string title;
        string summary;
        string traceText;

        // Held while reading the machine because a reset disposes and replaces
        // it, and the disc image behind these properties would be closed
        // underneath this thread.
        lock (_machineLock)
        {
            if (_gameCubeMachine is null)
            {
                return;
            }

            var machine = _gameCubeMachine;
            var header = machine.Disc.Header;
            var trace = machine.Trace;

            title = header.Title;
            summary =
                $"{header.GameId}  ·  {header.RegionText}  ·  {machine.Disc.ContainerName}  ·  " +
                $"{machine.Disc.Length / (1024.0 * 1024.0):F0} MB\n" +
                $"{machine.Disc.FileSystem.Files.Count} files  ·  " +
                $"boot image {machine.BootExecutable?.TotalSectionBytes ?? 0:N0} bytes in " +
                $"{machine.BootExecutable?.Sections.Count ?? 0} sections";
            var busiest = trace.BusiestCounter();
            var state = _gameCubeStopped
                ? $"stopped · {_gameCubeResult.Outcome} at 0x{_gameCubeResult.Pc:X8}  " +
                  GekkoDisassembler.Describe(_gameCubeResult.Instruction, _gameCubeResult.Pc)
                : !IsGameCubeStalled
                    ? "running"
                    : busiest.Count > _gameCubeStallBusiestCount
                        ? $"WAITING on hardware at 0x{machine.Cpu.Pc:X8} — " +
                          $"{_gameCubeStallFrames:N0} frames"
                        : $"in a long loop at 0x{machine.Cpu.Pc:X8} — " +
                          $"{_gameCubeStallFrames:N0} frames, nothing being polled";

            traceText =
                $"entry point   0x{machine.EntryPoint:X8}\n" +
                $"instructions  {machine.Cpu.InstructionsExecuted:N0}\n" +
                $"state         {state}\n" +
                $"waiting on    {(busiest.Count == 0 ? "-" : $"{busiest.Key}  ×{busiest.Count:N0}")}\n" +
                $"trace         {trace.KeptCount:N0} kept / {trace.SuppressedCount:N0} collapsed\n" +
                $"level         {trace.Level} · {trace.Channels}\n" +
                $"file          {trace.Settings.FilePath}";
        }

        PixelCubeOverlay.IsVisible = true;
        PixelCubeTitleText.Text = title;
        PixelCubeSummaryText.Text = summary;
        PixelCubeTraceText.Text = traceText;
        PixelCubeHintText.Text =
            "PixelCube runs the Gekko CPU, the disc drive, and enough of the DSP, ARAM and " +
            "serial interfaces for a game to get through startup. There is no graphics " +
            "hardware, so nothing can be drawn however far the code gets — the trace is the " +
            "output. \"WAITING on hardware\" means something unimplemented is being polled and " +
            "the line below names it; \"in a long loop\" means the game is busy rather than " +
            "stuck. Set PIXELCUBE_TRACE before launching (\"verbose:cpu\" for a disassembling " +
            "instruction trace) or use the cubetrace tool for a ranked list of what a run hit.";
    }

    /// <summary>
    /// The Nintendo core's counterpart to the N64 line. A blank or wrong frame
    /// is ambiguous on its own: the mapper number names the code path, and the
    /// PPU mask says whether the game asked for nothing or the emulator lost
    /// the write. Counts are cumulative so a frozen core shows up as identical
    /// consecutive samples.
    /// </summary>
    private void LogNesStatisticsPeriodically(uint[] frame)
    {
        if (_nesMachine is null)
        {
            return;
        }

        _coreFrameCounter++;
        if (_coreFrameCounter != 60 && _coreFrameCounter % 300 != 0)
        {
            return;
        }

        EmulatorDiagnostics.Write(
            $"NES @{_coreFrameCounter}: mapper={_nesMachine.MapperNumber}" +
            $"/{_nesMachine.SubmapperNumber} " +
            $"rendering={_nesMachine.IsRenderingEnabled} " +
            $"ppuctrl=0x{_nesMachine.PpuControl:X2} ppumask=0x{_nesMachine.PpuMask:X2} " +
            $"scanline={_nesMachine.Scanline} " +
            $"colors={CountDistinctFrameColors(frame)} " +
            $"cycles={_nesMachine.CpuCycles:N0} pc=0x{_nesMachine.ProgramCounter:X4} " +
            $"audio={_nesMachine.BufferedAudioSampleCount:N0}" +
            $"/{_nesMachine.DroppedAudioSampleCount:N0} dropped");
    }

    /// <summary>
    /// The Super Nintendo core reports which enhancement chip is live, because
    /// an unimplemented or stalled coprocessor and a PPU fault produce the same
    /// blank screen.
    /// </summary>
    private void LogSnesStatisticsPeriodically(uint[] frame)
    {
        if (_snesMachine is null)
        {
            return;
        }

        _coreFrameCounter++;
        if (_coreFrameCounter != 60 && _coreFrameCounter % 300 != 0)
        {
            return;
        }

        var chip = _snesMachine switch
        {
            { HasSa1: true } => "SA-1",
            { HasSuperFx: true } => "SuperFX",
            { HasSdd1: true } => "S-DD1",
            _ => "none"
        };
        var coprocessor = _snesMachine switch
        {
            { HasSa1: true } =>
                $" sa1={_snesMachine.Sa1ExecutedInstructions:N0} " +
                $"pc=0x{_snesMachine.Sa1ProgramAddress:X6} " +
                $"ctrl=0x{_snesMachine.Sa1ControlRegister:X2} dma={_snesMachine.Sa1DmaCount:N0}",
            { HasSuperFx: true } =>
                $" gsu={_snesMachine.SuperFxExecutedInstructions:N0} " +
                $"running={_snesMachine.SuperFxRunning}",
            { HasSdd1: true } => $" sdd1={_snesMachine.Sdd1DecompressionCount:N0}",
            _ => string.Empty
        };

        EmulatorDiagnostics.Write(
            $"SNES @{_coreFrameCounter}: chip={chip} " +
            $"bgmode={_snesMachine.BackgroundMode} mainscreen=0x{_snesMachine.MainScreenLayers:X2} " +
            $"blanked={_snesMachine.IsDisplayBlanked} brightness={_snesMachine.DisplayBrightness} " +
            $"colors={CountDistinctFrameColors(frame)} " +
            $"ppuwrites={_snesMachine.PpuRegisterWriteCount:N0} " +
            $"dma={_snesMachine.DmaTransferCount:N0} hdma={_snesMachine.HdmaEnableWrites:N0} " +
            $"joyreads={_snesMachine.HvbJoyReads:N0}" +
            $"/{_snesMachine.HvbJoyAutoReadBusyReads:N0} busy " +
            $"spriteover={_snesMachine.SpriteRangeOverLines:N0}" +
            $"/{_snesMachine.SpriteTimeOverTiles:N0} " +
            $"cycles={_snesMachine.CpuCycles:N0} pc=0x{_snesMachine.ProgramAddress:X6}" +
            coprocessor);
    }

    /// <summary>
    /// A count of unique pixel values in the presented frame. One colour means
    /// a flat screen, which is the single most common way both of these cores
    /// fail, and it cannot be inferred from any register.
    /// </summary>
    private static int CountDistinctFrameColors(uint[] pixels)
    {
        if (pixels.Length == 0)
        {
            return 0;
        }

        var seen = new HashSet<uint>();
        // Sampling every fourth pixel keeps this off the per-frame cost curve
        // while still separating a flat screen from a rendered one.
        for (var index = 0; index < pixels.Length && seen.Count <= 64; index += 4)
        {
            seen.Add(pixels[index]);
        }

        return seen.Count;
    }

    /// <summary>
    /// Records what the renderer actually drew. A frame buffer full of stale
    /// data looks identical to a video interface fault from the outside; the
    /// primitive counts are what separate "we presented the wrong memory" from
    /// "nothing was ever drawn into the right memory".
    /// </summary>
    private void LogN64RenderStatisticsPeriodically()
    {
        if (_n64Machine is null)
        {
            return;
        }

        _n64FrameCounter++;
        if (_n64FrameCounter != 60 && _n64FrameCounter % 300 != 0)
        {
            return;
        }

        var renderer = _n64Machine.Renderer;
        var performance = _n64Machine.Performance;
        var cachedInstructions = _n64Machine.Cpu.CachedInstructionsExecuted;
        var cachedBlocks = _n64Machine.Cpu.CachedBlocksExecuted;
        var cachedCoverage = _n64Machine.Cpu.InstructionsExecuted == 0
            ? 0d
            : cachedInstructions * 100d / _n64Machine.Cpu.InstructionsExecuted;
        var averageBlockLength = cachedBlocks == 0
            ? 0d
            : cachedInstructions / (double)cachedBlocks;
        EmulatorDiagnostics.Write(
            $"N64 render @{_n64FrameCounter}: microcode={renderer.DetectedMicrocodeName} " +
            $"crc=0x{renderer.MicrocodeCrc32:X8} " +
            $"commands={renderer.CommandsProcessed:N0} unsupported={renderer.UnsupportedCommands:N0} " +
            $"tris={renderer.TrianglesDrawn:N0} fillrects={renderer.FillRectanglesDrawn:N0} " +
            $"texrects={renderer.TextureRectanglesDrawn:N0} texpixels={renderer.TexturedPixelsDrawn:N0} " +
            $"texel1={renderer.SecondaryTexturePixelsSampled:N0} " +
            $"texcache={renderer.FilteredTextureCacheHits:N0}/{renderer.FilteredTextureCacheMisses:N0}/" +
            $"{renderer.FilteredTextureTexelsDecoded:N0} " +
            $"depthreject={renderer.DepthPixelsRejected:N0} " +
            $"alphareject={renderer.AlphaPixelsRejected:N0} blended={renderer.FramebufferPixelsBlended:N0} " +
            $"centrepinned={renderer.CentrePinnedVertices:N0} offscreen={renderer.OffscreenProjectedVertices:N0}; " +
            $"blocks={cachedBlocks:N0}/{cachedInstructions:N0} instructions " +
            $"({cachedCoverage:F1}% coverage, {averageBlockLength:F1} avg); " +
            $"idle-skipped={_n64Machine.Cpu.IdleInstructionsSkipped:N0}; " +
            $"pc=0x{_n64Machine.Cpu.ProgramCounter:X8} " +
            $"tasks={_n64Machine.GraphicsTasksSubmitted:N0}/{_n64Machine.AudioTasksSubmitted:N0} " +
            $"audio-ucode={_n64Machine.AudioProcessor.DetectedMicrocodeName} " +
            $"audio-cmds={_n64Machine.AudioProcessor.CommandsProcessed:N0}/" +
            $"{_n64Machine.AudioProcessor.UnsupportedCommands:N0} unsupported " +
            $"audio-queue={_n64Machine.BufferedAudioSampleCount:N0}/" +
            $"{_n64Machine.DroppedAudioSampleCount:N0} dropped " +
            $"audio-underruns={_audioOutput?.UnderrunSampleCount ?? 0:N0} " +
            $"ai={_n64Machine.Memory.CurrentAudioSampleRate:N0}Hz/" +
            $"{_n64Machine.Memory.AudioDmasCompleted:N0} dmas " +
            $"vi={_n64Machine.Memory.VerticalInterruptsRaised:N0} " +
            $"polls={_n64Machine.Memory.ControllerPolls:N0}/" +
            $"{_n64Machine.Memory.NonNeutralControllerPolls:N0} " +
            $"exceptions={_n64Machine.Cpu.InterruptExceptionsRaised:N0}/" +
            $"{_n64Machine.Cpu.NonInterruptExceptionsRaised:N0} " +
            $"last={_n64Machine.Cpu.LastExceptionCode}@0x{_n64Machine.Cpu.LastExceptionAddress:X8}; " +
            $"timing={performance.AverageMillisecondsPerField:F2} ms/field, " +
            $"graphics={performance.GraphicsPercentage:F1}%, " +
            $"cpu+scheduler={performance.CpuAndSchedulingPercentage:F1}%, " +
            $"audio={performance.AudioPercentage:F1}%, vi={performance.VideoInterfacePercentage:F1}%");
    }

    /// <summary>
    /// Silences every motor. A pad keeps rumbling until it is told to stop, so
    /// this has to run when a game unloads rather than relying on the effect
    /// timing out on its own.
    /// </summary>
    private void StopAllRumble()
    {
        if (_rumbleTargets is null)
        {
            return;
        }

        for (var port = 0; port < _rumbleTargets.Length; port++)
        {
            if (_rumbleMotorActive[port])
            {
                _rumbleMotorActive[port] = false;
                _rumbleTargets[port].SetRumble(false);
            }
        }
    }

    /// <summary>
    /// Mirrors each port's Rumble Pak motor onto the pad driving it. The motor
    /// is held on by the game rather than pulsed, so this only talks to the
    /// device when the state actually changes.
    /// </summary>
    private void ApplyN64RumbleState()
    {
        if (_n64Machine is null)
        {
            return;
        }

        _rumbleTargets ??= [_gamepad, _playerTwoGamepad, _playerThreeGamepad, _playerFourGamepad];
        for (var port = 0; port < _rumbleTargets.Length; port++)
        {
            var active = _n64Machine.IsRumbleMotorActive(port + 1);
            if (active == _rumbleMotorActive[port])
            {
                continue;
            }

            _rumbleMotorActive[port] = active;
            _rumbleTargets[port].SetRumble(active);
        }
    }

    /// <summary>
    /// The video interface registers and the renderer's skipped texel formats
    /// are tracked continuously but were never reported anywhere. Recording
    /// them when the programmed output changes is what distinguishes a
    /// mis-sized frame from a mis-decoded one without guessing.
    /// </summary>
    private void LogN64VideoStateWhenItChanges()
    {
        if (_n64Machine is null)
        {
            return;
        }

        var memory = _n64Machine.Memory;
        if (_n64Machine.Width == _loggedN64Width &&
            _n64Machine.Height == _loggedN64Height &&
            memory.ViControl == _loggedN64Control &&
            memory.ViHorizontalVideo == _loggedN64HorizontalVideo)
        {
            return;
        }

        _loggedN64Width = _n64Machine.Width;
        _loggedN64Height = _n64Machine.Height;
        _loggedN64Control = memory.ViControl;
        _loggedN64HorizontalVideo = memory.ViHorizontalVideo;

        var unsupported = _n64Machine.Renderer.UnsupportedTextureFormatCounts;
        var formats = unsupported.Count == 0
            ? "none"
            : string.Join(", ", unsupported.Select(entry => $"{entry.Key}={entry.Value:N0}"));
        EmulatorDiagnostics.Write(
            $"N64 video: {_loggedN64Width}x{_loggedN64Height} " +
            $"origin=0x{memory.ViOrigin:X6} stride={memory.ViWidth} " +
            $"control=0x{memory.ViControl:X2} active={_n64Machine.IsVideoOutputActive} " +
            $"h=0x{memory.ViHorizontalVideo:X8} v=0x{memory.ViVerticalVideo:X8} " +
            $"xscale=0x{memory.ViXScale:X} yscale=0x{memory.ViYScale:X}; " +
            $"unsupported texel formats: {formats}");
    }

    private uint[] GetCurrentMachineFrame()
    {
        if (_nesMachine is not null)
        {
            return _nesMachine.CurrentFrame.ToArray();
        }

        if (_snesMachine is not null)
        {
            return _snesMachine.CurrentFrame.ToArray();
        }

        if (_n64Machine is not null)
        {
            return _n64Machine.CurrentFrame.ToArray();
        }

        if (_gameCubeMachine is not null)
        {
            return new uint[_gameCubeMachine.Width * _gameCubeMachine.Height];
        }

        throw new InvalidOperationException("The emulator is not running.");
    }

    private byte[] SaveMachineState()
    {
        if (_nesMachine is not null)
        {
            return _nesMachine.SaveState();
        }

        if (_snesMachine is not null)
        {
            return _snesMachine.SaveState();
        }

        if (_n64Machine is not null)
        {
            return _n64Machine.SaveState();
        }

        if (_gameCubeMachine is not null)
        {
            throw new NotSupportedException(
                "PixelCube cannot save a state yet: there is no execution state to save.");
        }

        throw new InvalidOperationException("The emulator is not running.");
    }

    private void LoadMachineState(byte[] state)
    {
        if (_nesMachine is not null)
        {
            _nesMachine.LoadState(state);
            return;
        }

        if (_snesMachine is not null)
        {
            _snesMachine.LoadState(state);
            return;
        }

        if (_n64Machine is not null)
        {
            _n64Machine.LoadState(state);
            return;
        }

        throw new InvalidOperationException("The emulator is not running.");
    }

    private void OnResumeClick(object? sender, RoutedEventArgs eventArgs) => Resume();

    private void OnLibraryImageClick(object? sender, RoutedEventArgs eventArgs) => CaptureLibraryImage();

    private void OnSaveStateClick(object? sender, RoutedEventArgs eventArgs) => OpenSaveStateMenu();

    private void OnLoadStateClick(object? sender, RoutedEventArgs eventArgs) => OpenLoadStateMenu();

    private void OnResetGameClick(object? sender, RoutedEventArgs eventArgs) => ResetGame();

    private void OnQuitGameClick(object? sender, RoutedEventArgs eventArgs) => Close();

    private void ShowError(Exception exception)
    {
        EmulatorStatusText.Text = exception.Message.ToUpperInvariant();
        EmulatorStatusText.Foreground = Avalonia.Media.Brushes.IndianRed;
        LoadingOverlay.IsVisible = true;
    }

    private static bool HasUsefulImage(uint[] pixels)
    {
        var first = pixels[0];
        var differentColors = 0;
        for (var index = 0; index < pixels.Length; index += 97)
        {
            if (pixels[index] != first && ++differentColors >= 6)
            {
                return true;
            }
        }

        return false;
    }

    private enum PauseMenuMode
    {
        Main,
        SaveSlots,
        LoadSlots,
        ConfirmOverwrite
    }
}
