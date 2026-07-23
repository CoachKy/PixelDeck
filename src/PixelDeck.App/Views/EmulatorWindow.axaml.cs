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
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Views;

public partial class EmulatorWindow : Window
{
    private WriteableBitmap? _frameBitmap;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DispatcherTimer _inputTimer;
    private readonly WindowsGamepad _gamepad = new();
    private readonly HashSet<Key> _pressedKeys = [];
    private readonly object _machineLock = new();
    private readonly object _presentationLock = new();
    private GameEntry? _game;
    private Task? _emulationTask;
    private NesMachine? _nesMachine;
    private SnesMachine? _snesMachine;
    private EmulatorAudioOutput? _audioOutput;
    private Stopwatch? _playSession;
    private GamepadButton _previousGamepadButtons;
    private bool _pauseChordHeld;
    private volatile bool _isPaused;
    private bool _screenshotSaved;
    private int _menuIndex;
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
    }

    private string SaveStatePath => Path.ChangeExtension(_game!.ScreenshotCachePath, ".state");

    private Button[] MenuButtons => [ResumeButton, SaveStateButton, LoadStateButton, ResetGameButton, QuitGameButton];

    private void OnOpened(object? sender, EventArgs eventArgs)
    {
        try
        {
            if (_game is null)
            {
                throw new InvalidOperationException("No game was selected.");
            }

            _gamepad.UserIndex = PixelDeckSettingsStore.Current.ControllerIndex;
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
                    nextFrameAt = clock.Elapsed;
                    await Task.Delay(16, cancellationToken);
                    continue;
                }

                var producedFrame = false;
                frameNumber++;
                lock (_machineLock)
                {
                    if (!_isPaused && HasMachine)
                    {
                        lock (_presentationLock)
                        {
                            RunMachineFrame(_presentationPixels
                                ?? throw new InvalidOperationException("The emulator display buffer is not initialized."));
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

                var requestedRate = Volatile.Read(ref _playbackRateMultiplier);
                if (requestedRate != currentRate)
                {
                    currentRate = requestedRate;
                    nextFrameAt = clock.Elapsed;
                }

                nextFrameAt += TimeSpan.FromSeconds(1 / (MachineFramesPerSecond * currentRate));
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
        var bitmap = _frameBitmap ?? throw new InvalidOperationException("The emulator display is not initialized.");
        using (var framebuffer = bitmap.Lock())
        {
            fixed (uint* source = pixels)
            {
                var sourceRowBytes = MachineWidth * sizeof(uint);
                for (var row = 0; row < MachineHeight; row++)
                {
                    var sourceRow = source + (row * MachineWidth);
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
        LoadingOverlay.IsVisible = false;

        if (_game is not null && !_screenshotSaved && frameNumber >= 120 && hasUsefulImage)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_game.ScreenshotCachePath)!);
            bitmap.Save(_game.ScreenshotCachePath, PngBitmapEncoderOptions.Default);
            _screenshotSaved = true;
        }
    }

    private void PollInput(object? sender, EventArgs eventArgs)
    {
        var gamepad = _gamepad.ReadButtons();
        var newPresses = gamepad & ~_previousGamepadButtons;
        var pauseChord = gamepad.HasFlag(GamepadButton.Back) && gamepad.HasFlag(GamepadButton.Start);
        var pauseRequested = newPresses.HasFlag(GamepadButton.Guide) || (pauseChord && !_pauseChordHeld);
        _pauseChordHeld = pauseChord;
        _previousGamepadButtons = gamepad;

        if (pauseRequested)
        {
            SetPaused(!_isPaused);
            return;
        }

        SetFastForward(!_isPaused && gamepad.HasFlag(GamepadButton.RightTrigger));

        if (_isPaused)
        {
            if (newPresses.HasFlag(GamepadButton.DPadUp)) MoveMenuSelection(-1);
            if (newPresses.HasFlag(GamepadButton.DPadDown)) MoveMenuSelection(1);
            if (newPresses.HasFlag(GamepadButton.A)) ExecuteSelectedMenuAction();
            if (newPresses.HasFlag(GamepadButton.B)) SetPaused(false);
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
                var buttons = NesButton.None;
                if (_pressedKeys.Contains(Key.Z) || gamepad.HasFlag(settings.AButton)) buttons |= NesButton.A;
                if (_pressedKeys.Contains(Key.X) || gamepad.HasFlag(settings.BButton)) buttons |= NesButton.B;
                if (_pressedKeys.Contains(Key.Enter) || gamepad.HasFlag(settings.StartButton)) buttons |= NesButton.Start;
                if (_pressedKeys.Contains(Key.LeftShift) || _pressedKeys.Contains(Key.RightShift) || gamepad.HasFlag(settings.SelectButton)) buttons |= NesButton.Select;
                if (_pressedKeys.Contains(Key.Up) || gamepad.HasFlag(GamepadButton.DPadUp)) buttons |= NesButton.Up;
                if (_pressedKeys.Contains(Key.Down) || gamepad.HasFlag(GamepadButton.DPadDown)) buttons |= NesButton.Down;
                if (_pressedKeys.Contains(Key.Left) || gamepad.HasFlag(GamepadButton.DPadLeft)) buttons |= NesButton.Left;
                if (_pressedKeys.Contains(Key.Right) || gamepad.HasFlag(GamepadButton.DPadRight)) buttons |= NesButton.Right;
                _nesMachine.SetControllerState(1, buttons);
            }
            else if (_snesMachine is not null)
            {
                var buttons = SnesButton.None;
                if (_pressedKeys.Contains(Key.Z) || gamepad.HasFlag(settings.SnesAButton)) buttons |= SnesButton.A;
                if (_pressedKeys.Contains(Key.X) || gamepad.HasFlag(settings.SnesBButton)) buttons |= SnesButton.B;
                if (_pressedKeys.Contains(Key.A) || gamepad.HasFlag(settings.SnesXButton)) buttons |= SnesButton.X;
                if (_pressedKeys.Contains(Key.S) || gamepad.HasFlag(settings.SnesYButton)) buttons |= SnesButton.Y;
                if (_pressedKeys.Contains(Key.Q) || gamepad.HasFlag(settings.SnesLButton)) buttons |= SnesButton.L;
                if (_pressedKeys.Contains(Key.W) || gamepad.HasFlag(settings.SnesRButton)) buttons |= SnesButton.R;
                if (_pressedKeys.Contains(Key.Enter) || gamepad.HasFlag(settings.SnesStartButton)) buttons |= SnesButton.Start;
                if (_pressedKeys.Contains(Key.LeftShift) || _pressedKeys.Contains(Key.RightShift) || gamepad.HasFlag(settings.SnesSelectButton)) buttons |= SnesButton.Select;
                if (_pressedKeys.Contains(Key.Up) || gamepad.HasFlag(GamepadButton.DPadUp)) buttons |= SnesButton.Up;
                if (_pressedKeys.Contains(Key.Down) || gamepad.HasFlag(GamepadButton.DPadDown)) buttons |= SnesButton.Down;
                if (_pressedKeys.Contains(Key.Left) || gamepad.HasFlag(GamepadButton.DPadLeft)) buttons |= SnesButton.Left;
                if (_pressedKeys.Contains(Key.Right) || gamepad.HasFlag(GamepadButton.DPadRight)) buttons |= SnesButton.Right;
                _snesMachine.SetControllerState(1, buttons);
            }
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            SetPaused(!_isPaused);
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
            _snesMachine?.SetControllerState(1, SnesButton.None);
            if (paused)
            {
                _nesMachine?.ClearAudioSamples();
                _snesMachine?.ClearAudioSamples();
            }
        }

        if (paused)
        {
            UpdateStateAvailability();
            SetMenuSelection(0);
        }
        else
        {
            Focus();
        }
    }

    private void SetFastForward(bool enabled)
    {
        var multiplier = enabled ? 2 : 1;
        if (Interlocked.Exchange(ref _playbackRateMultiplier, multiplier) == multiplier)
        {
            return;
        }

        if (_audioOutput is not null)
        {
            _audioOutput.IsPaused = _isPaused || enabled;
        }

        lock (_machineLock)
        {
            _nesMachine?.ClearAudioSamples();
            _snesMachine?.ClearAudioSamples();
        }
    }

    private void MoveMenuSelection(int direction)
    {
        var buttons = MenuButtons;
        for (var attempts = 0; attempts < buttons.Length; attempts++)
        {
            _menuIndex = (_menuIndex + direction + buttons.Length) % buttons.Length;
            if (buttons[_menuIndex].IsEffectivelyEnabled)
            {
                buttons[_menuIndex].Focus();
                return;
            }
        }
    }

    private void SetMenuSelection(int index)
    {
        _menuIndex = index;
        if (!MenuButtons[_menuIndex].IsEffectivelyEnabled)
        {
            MoveMenuSelection(1);
            return;
        }

        MenuButtons[_menuIndex].Focus();
    }

    private void ExecuteSelectedMenuAction()
    {
        switch (_menuIndex)
        {
            case 0: Resume(); break;
            case 1: SaveState(); break;
            case 2: LoadState(); break;
            case 3: ResetGame(); break;
            case 4: Close(); break;
        }
    }

    private void Resume() => SetPaused(false);

    private void SaveState()
    {
        try
        {
            byte[] state;
            lock (_machineLock)
            {
                state = SaveMachineState();
            }

            CrashSafeFile.WriteAllBytes(SaveStatePath, state);
            StateStatusText.Text = $"STATE SAVED  ·  {DateTime.Now:h:mm tt}".ToUpperInvariant();
            UpdateStateAvailability(preserveStatus: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StateStatusText.Text = $"SAVE FAILED  ·  {exception.Message}".ToUpperInvariant();
        }
    }

    private void LoadState()
    {
        try
        {
            var candidates = CrashSafeFile.GetReadCandidates(SaveStatePath);
            if (candidates.Count == 0)
            {
                throw new FileNotFoundException("No saved state is available.", SaveStatePath);
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

                    if (!string.Equals(candidate, SaveStatePath, StringComparison.OrdinalIgnoreCase))
                    {
                        CrashSafeFile.CommitTemporary(SaveStatePath);
                    }

                    PresentFrame(frame, 0);
                    StateStatusText.Text = "STATE LOADED";
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
            StateStatusText.Text = $"LOAD FAILED  ·  {exception.Message}".ToUpperInvariant();
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
                }
            }

            LoadingOverlay.IsVisible = true;
            EmulatorStatusText.Text = "POWERING ON";
            Resume();
        }
        catch (Exception exception)
        {
            StateStatusText.Text = $"RESET FAILED  ·  {exception.Message}".ToUpperInvariant();
        }
    }

    private void UpdateStateAvailability(bool preserveStatus = false)
    {
        var exists = _game is not null && CrashSafeFile.Exists(SaveStatePath);
        LoadStateButton.IsEnabled = exists;
        if (!preserveStatus)
        {
            StateStatusText.Text = exists
                ? $"SAVED STATE  ·  {File.GetLastWriteTime(GetAvailableSaveStatePath()):MMM d, h:mm tt}".ToUpperInvariant()
                : "NO SAVED STATE YET";
        }
    }

    private string GetAvailableSaveStatePath() => File.Exists(SaveStatePath)
        ? SaveStatePath
        : CrashSafeFile.GetTemporaryPath(SaveStatePath);

    private bool HasMachine => _nesMachine is not null || _snesMachine is not null;

    private int MachineWidth => _nesMachine?.Width ?? _snesMachine?.Width
        ?? throw new InvalidOperationException("The emulator is not running.");

    private int MachineHeight => _nesMachine?.Height ?? _snesMachine?.Height
        ?? throw new InvalidOperationException("The emulator is not running.");

    private double MachineFramesPerSecond => _snesMachine?.FramesPerSecond ?? 60.0988;

    private void LoadMachine()
    {
        var path = _game?.FullPath ?? throw new InvalidOperationException("No game was selected.");
        var extension = Path.GetExtension(path);
        TryFlushBatterySave();
        _nesMachine = null;
        _snesMachine = null;

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
            return;
        }

        if (string.Equals(extension, ".sfc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".smc", StringComparison.OrdinalIgnoreCase))
        {
            _snesMachine = SnesMachine.Load(path, _game.SaveRamPath);
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
            return;
        }

        if (_snesMachine is not null)
        {
            _snesMachine.RunFrame().CopyTo(destination);
            return;
        }

        throw new InvalidOperationException("The emulator is not running.");
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

        throw new InvalidOperationException("The emulator is not running.");
    }

    private void OnResumeClick(object? sender, RoutedEventArgs eventArgs) => Resume();

    private void OnSaveStateClick(object? sender, RoutedEventArgs eventArgs) => SaveState();

    private void OnLoadStateClick(object? sender, RoutedEventArgs eventArgs) => LoadState();

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
}
