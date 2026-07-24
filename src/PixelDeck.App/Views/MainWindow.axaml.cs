using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PixelDeck.App.Audio;
using PixelDeck.App.Input;
using PixelDeck.App.Settings;
using PixelDeck.App.ViewModels;

namespace PixelDeck.App.Views;

public partial class MainWindow : Window
{
    private const int LibraryColumnCount = 6;

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _gamepadTimer;
    private readonly DispatcherTimer _libraryRefreshTimer;
    private readonly DashboardSoundPlayer _dashboardSounds = new();
    private readonly WindowsGamepad _gamepad = new();
    private FileSystemWatcher? _watcher;
    private EmulatorWindow? _emulatorWindow;
    private DashboardNavigationRegion _navigationRegion = DashboardNavigationRegion.PageContent;
    private bool _dashboardSoundsEnabled;
    private bool _isQuitConfirmationVisible;
    private bool _isClosingAfterConfirmation;
    private bool _quitConfirmed;
    private int _quitConfirmationIndex;

    public MainWindow()
    {
        InitializeComponent();

        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, UpdateClock);
        _gamepadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input, PollGamepad);
        _libraryRefreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, RefreshLibrary)
        {
            IsEnabled = false
        };

        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        await viewModel.RefreshAsync();
        ConfigureWatcher(viewModel.GamesFolder);
        _gamepad.UserIndex = viewModel.SelectedControllerSlot.Index;
        viewModel.UpdateControllerStatus(_gamepad.IsConnected);
        _clockTimer.Start();
        _gamepadTimer.Start();
        FocusPageContent(viewModel);
        _dashboardSoundsEnabled = true;
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _clockTimer.Stop();
        _gamepadTimer.Stop();
        _libraryRefreshTimer.Stop();
        _watcher?.Dispose();
        _dashboardSoundsEnabled = false;
        _dashboardSounds.Dispose();
        (DataContext as MainViewModel)?.Dispose();
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (_isQuitConfirmationVisible)
        {
            HandleQuitConfirmationKey(eventArgs);
            return;
        }

        switch (eventArgs.Key)
        {
            case Key.F1:
                SelectPage(viewModel, DashboardPage.Home);
                FocusPageContent(viewModel);
                PlayNavigationTone();
                eventArgs.Handled = true;
                break;
            case Key.F2:
                SelectPage(viewModel, DashboardPage.Library);
                FocusPageContent(viewModel);
                PlayNavigationTone();
                eventArgs.Handled = true;
                break;
            case Key.F3:
                SelectPage(viewModel, DashboardPage.Settings);
                FocusPageContent(viewModel);
                PlayNavigationTone();
                eventArgs.Handled = true;
                break;
            case Key.F4:
                SelectPage(viewModel, DashboardPage.Quit);
                FocusPageContent(viewModel);
                PlayNavigationTone();
                eventArgs.Handled = true;
                break;
            case Key.Up when _navigationRegion == DashboardNavigationRegion.PageContent &&
                             viewModel.IsLibraryVisible &&
                             viewModel.SelectLibraryGameInAdjacentRow(-1, LibraryColumnCount):
                ScrollSelectionIntoView(viewModel);
                eventArgs.Handled = true;
                break;
            case Key.Up:
                MoveUp(viewModel);
                eventArgs.Handled = true;
                break;
            case Key.Down when _navigationRegion == DashboardNavigationRegion.PageContent && viewModel.IsLibraryVisible:
                if (viewModel.SelectLibraryGameInAdjacentRow(1, LibraryColumnCount))
                {
                    ScrollSelectionIntoView(viewModel);
                }

                eventArgs.Handled = true;
                break;
            case Key.Down when _navigationRegion != DashboardNavigationRegion.PageContent:
                MoveDown(viewModel);
                eventArgs.Handled = true;
                break;
            case Key.Left when _navigationRegion == DashboardNavigationRegion.PrimaryTabs:
                MovePrimaryTab(viewModel, -1);
                eventArgs.Handled = true;
                break;
            case Key.Right when _navigationRegion == DashboardNavigationRegion.PrimaryTabs:
                MovePrimaryTab(viewModel, 1);
                eventArgs.Handled = true;
                break;
            case Key.Left when _navigationRegion == DashboardNavigationRegion.LibraryTabs:
                MoveLibraryTab(viewModel, -1);
                eventArgs.Handled = true;
                break;
            case Key.Right when _navigationRegion == DashboardNavigationRegion.LibraryTabs:
                MoveLibraryTab(viewModel, 1);
                eventArgs.Handled = true;
                break;
            case Key.Left when _navigationRegion == DashboardNavigationRegion.PageContent && viewModel.IsHomeVisible:
                viewModel.SelectPreviousRecentGame();
                ScrollSelectionIntoView(viewModel);
                eventArgs.Handled = true;
                break;
            case Key.Right when _navigationRegion == DashboardNavigationRegion.PageContent && viewModel.IsHomeVisible:
                viewModel.SelectNextRecentGame();
                ScrollSelectionIntoView(viewModel);
                eventArgs.Handled = true;
                break;
            case Key.Left when _navigationRegion == DashboardNavigationRegion.PageContent && viewModel.IsLibraryVisible:
                viewModel.SelectPreviousLibraryGame();
                ScrollSelectionIntoView(viewModel);
                eventArgs.Handled = true;
                break;
            case Key.Right when _navigationRegion == DashboardNavigationRegion.PageContent && viewModel.IsLibraryVisible:
                viewModel.SelectNextLibraryGame();
                ScrollSelectionIntoView(viewModel);
                eventArgs.Handled = true;
                break;
            case Key.Enter when _navigationRegion == DashboardNavigationRegion.PageContent:
                ActivatePageContent(viewModel);
                eventArgs.Handled = true;
                break;
            case Key.Enter:
                FocusPageContent(viewModel);
                PlayNavigationTone();
                eventArgs.Handled = true;
                break;
            case Key.F5:
                _ = viewModel.RefreshAsync();
                eventArgs.Handled = true;
                break;
        }
    }

    private void UpdateClock(object? sender, EventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.UpdateClock();
        }
    }

    private void PollGamepad(object? sender, EventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        _gamepad.UserIndex = viewModel.SelectedControllerSlot.Index;
        viewModel.UpdateControllerStatus(_gamepad.IsConnected);
        var presses = _gamepad.ReadNewPresses();

        if (_isQuitConfirmationVisible)
        {
            HandleQuitConfirmationGamepad(presses);
            return;
        }

        if (presses.HasFlag(GamepadButton.LeftShoulder))
        {
            QuickSwitchPage(viewModel, -1);
            return;
        }

        if (presses.HasFlag(GamepadButton.RightShoulder))
        {
            QuickSwitchPage(viewModel, 1);
            return;
        }

        if (presses.HasFlag(GamepadButton.DPadUp))
        {
            if (_navigationRegion == DashboardNavigationRegion.PageContent &&
                viewModel.IsLibraryVisible &&
                viewModel.SelectLibraryGameInAdjacentRow(-1, LibraryColumnCount))
            {
                ScrollSelectionIntoView(viewModel);
            }
            else
            {
                MoveUp(viewModel);
            }

            return;
        }

        if (presses.HasFlag(GamepadButton.DPadDown))
        {
            if (_navigationRegion == DashboardNavigationRegion.PageContent && viewModel.IsLibraryVisible)
            {
                if (viewModel.SelectLibraryGameInAdjacentRow(1, LibraryColumnCount))
                {
                    ScrollSelectionIntoView(viewModel);
                }
            }
            else if (_navigationRegion != DashboardNavigationRegion.PageContent)
            {
                MoveDown(viewModel);
            }

            return;
        }

        if (_navigationRegion == DashboardNavigationRegion.PrimaryTabs)
        {
            if (presses.HasFlag(GamepadButton.DPadLeft)) MovePrimaryTab(viewModel, -1);
            if (presses.HasFlag(GamepadButton.DPadRight)) MovePrimaryTab(viewModel, 1);
            if (presses.HasFlag(GamepadButton.A))
            {
                FocusPageContent(viewModel);
                PlayNavigationTone();
            }
            return;
        }

        if (_navigationRegion == DashboardNavigationRegion.LibraryTabs)
        {
            if (presses.HasFlag(GamepadButton.DPadLeft)) MoveLibraryTab(viewModel, -1);
            if (presses.HasFlag(GamepadButton.DPadRight)) MoveLibraryTab(viewModel, 1);
            if (presses.HasFlag(GamepadButton.A))
            {
                FocusPageContent(viewModel);
                PlayNavigationTone();
            }
            return;
        }

        if (presses.HasFlag(GamepadButton.DPadLeft))
        {
            if (viewModel.IsHomeVisible) viewModel.SelectPreviousRecentGame();
            if (viewModel.IsLibraryVisible) viewModel.SelectPreviousLibraryGame();
            ScrollSelectionIntoView(viewModel);
        }

        if (presses.HasFlag(GamepadButton.DPadRight))
        {
            if (viewModel.IsHomeVisible) viewModel.SelectNextRecentGame();
            if (viewModel.IsLibraryVisible) viewModel.SelectNextLibraryGame();
            ScrollSelectionIntoView(viewModel);
        }

        if (presses.HasFlag(GamepadButton.X))
        {
            _ = viewModel.RefreshAsync();
        }

        if (viewModel.IsLibraryVisible &&
            presses.HasFlag(GamepadButton.Y) &&
            viewModel.OpenGamesFolderCommand.CanExecute(null))
        {
            viewModel.OpenGamesFolderCommand.Execute(null);
        }

        if (presses.HasFlag(GamepadButton.A))
        {
            ActivatePageContent(viewModel);
        }
    }

    private void MoveUp(MainViewModel viewModel)
    {
        switch (_navigationRegion)
        {
            case DashboardNavigationRegion.PageContent when viewModel.IsLibraryVisible:
                FocusLibraryTabs(viewModel);
                PlayNavigationTone();
                break;
            case DashboardNavigationRegion.PageContent:
            case DashboardNavigationRegion.LibraryTabs:
                FocusPrimaryTabs(viewModel);
                PlayNavigationTone();
                break;
        }
    }

    private void MoveDown(MainViewModel viewModel)
    {
        if (_navigationRegion == DashboardNavigationRegion.PrimaryTabs && viewModel.IsLibraryVisible)
        {
            FocusLibraryTabs(viewModel);
            PlayNavigationTone();
            return;
        }

        FocusPageContent(viewModel);
        PlayNavigationTone();
    }

    private void QuickSwitchPage(MainViewModel viewModel, int direction)
    {
        var next = Wrap((int)viewModel.SelectedPage + direction, Enum.GetValues<DashboardPage>().Length);
        SelectPage(viewModel, (DashboardPage)next);
        FocusPageContent(viewModel);
        PlayNavigationTone();
    }

    private void MovePrimaryTab(MainViewModel viewModel, int direction)
    {
        var next = Wrap((int)viewModel.SelectedPage + direction, Enum.GetValues<DashboardPage>().Length);
        SelectPage(viewModel, (DashboardPage)next);
        FocusPrimaryTabs(viewModel);
        PlayNavigationTone();
    }

    private void MoveLibraryTab(MainViewModel viewModel, int direction)
    {
        var next = Wrap((int)viewModel.SelectedLibrarySystem + direction, Enum.GetValues<LibrarySystem>().Length);
        viewModel.SelectedLibrarySystem = (LibrarySystem)next;
        FocusLibraryTabs(viewModel);
        PlayNavigationTone();
    }

    private static int Wrap(int value, int count) => (value % count + count) % count;

    private static void SelectPage(MainViewModel viewModel, DashboardPage page) => viewModel.SelectedPage = page;

    private void FocusPrimaryTabs(MainViewModel viewModel)
    {
        _navigationRegion = DashboardNavigationRegion.PrimaryTabs;
        switch (viewModel.SelectedPage)
        {
            case DashboardPage.Home:
                HomeTabButton.Focus();
                break;
            case DashboardPage.Library:
                LibraryTabButton.Focus();
                break;
            case DashboardPage.Settings:
                SettingsTabButton.Focus();
                break;
            case DashboardPage.Quit:
                QuitTabButton.Focus();
                break;
        }
    }

    private void FocusLibraryTabs(MainViewModel viewModel)
    {
        _navigationRegion = DashboardNavigationRegion.LibraryTabs;
        if (viewModel.IsNintendoSelected)
        {
            NintendoSystemTabButton.Focus();
        }
        else
        {
            SuperNintendoSystemTabButton.Focus();
        }
    }

    private void FocusPageContent(MainViewModel viewModel)
    {
        _navigationRegion = DashboardNavigationRegion.PageContent;
        switch (viewModel.SelectedPage)
        {
            case DashboardPage.Home:
                if (viewModel.HasRecentGames) RecentGameList.Focus();
                else HomePage.Focus();
                break;
            case DashboardPage.Library:
                if (viewModel.HasGames) GameList.Focus();
                else LibraryPage.Focus();
                break;
            case DashboardPage.Settings:
                ControllerSlotPicker.Focus();
                break;
            case DashboardPage.Quit:
                QuitButton.Focus();
                break;
        }
    }

    private void ActivatePageContent(MainViewModel viewModel)
    {
        if (viewModel.IsQuitVisible)
        {
            ShowQuitConfirmation();
            return;
        }

        LaunchSelectedGame();
    }

    private void ScrollSelectionIntoView(MainViewModel viewModel)
    {
        if (viewModel.IsHomeVisible && viewModel.SelectedRecentGame is not null)
        {
            RecentGameList.ScrollIntoView(viewModel.SelectedRecentGame);
        }
        else if (viewModel.IsLibraryVisible && viewModel.SelectedGame is not null)
        {
            GameList.ScrollIntoView(viewModel.SelectedGame);
        }
    }

    private void OnPrimaryTabGotFocus(object? sender, FocusChangedEventArgs eventArgs) =>
        _navigationRegion = DashboardNavigationRegion.PrimaryTabs;

    private void OnLibraryTabGotFocus(object? sender, FocusChangedEventArgs eventArgs) =>
        _navigationRegion = DashboardNavigationRegion.LibraryTabs;

    private void OnPageContentGotFocus(object? sender, FocusChangedEventArgs eventArgs) =>
        _navigationRegion = DashboardNavigationRegion.PageContent;

    private void OnDashboardTabClick(object? sender, RoutedEventArgs eventArgs) => PlayNavigationTone();

    private void OnDashboardSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (eventArgs.AddedItems.Count > 0 && DataContext is MainViewModel { IsBusy: false })
        {
            PlayNavigationTone();
        }
    }

    private void PlayNavigationTone()
    {
        if (_dashboardSoundsEnabled)
        {
            _dashboardSounds.PlayNavigation();
        }
    }

    private void OnLaunchClick(object? sender, RoutedEventArgs eventArgs) => LaunchSelectedGame();

    private void OnGameDoubleTapped(object? sender, TappedEventArgs eventArgs) => LaunchSelectedGame();

    private void OnRecentGameDoubleTapped(object? sender, TappedEventArgs eventArgs) => LaunchSelectedGame();

    private void OnQuitClick(object? sender, RoutedEventArgs eventArgs) => ShowQuitConfirmation();

    private void OnQuitCancelClick(object? sender, RoutedEventArgs eventArgs) => DismissQuitConfirmation();

    private void OnQuitConfirmClick(object? sender, RoutedEventArgs eventArgs) => _ = ConfirmQuitAsync();

    private void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_quitConfirmed)
        {
            return;
        }

        eventArgs.Cancel = true;
        ShowQuitConfirmation();
    }

    private void ShowQuitConfirmation()
    {
        if (_isQuitConfirmationVisible || _isClosingAfterConfirmation)
        {
            return;
        }

        _isQuitConfirmationVisible = true;
        _quitConfirmationIndex = 0;
        QuitConfirmationOverlay.IsVisible = true;
        QuitCancelButton.IsEnabled = true;
        QuitConfirmButton.IsEnabled = true;
        QuitCancelButton.Focus();
        PlayNavigationTone();
    }

    private void DismissQuitConfirmation()
    {
        if (!_isQuitConfirmationVisible || _isClosingAfterConfirmation)
        {
            return;
        }

        _isQuitConfirmationVisible = false;
        QuitConfirmationOverlay.IsVisible = false;
        if (DataContext is MainViewModel viewModel)
        {
            FocusPageContent(viewModel);
        }

        PlayNavigationTone();
    }

    private void HandleQuitConfirmationKey(KeyEventArgs eventArgs)
    {
        switch (eventArgs.Key)
        {
            case Key.Left:
            case Key.Up:
                SelectQuitConfirmationChoice(0);
                break;
            case Key.Right:
            case Key.Down:
                SelectQuitConfirmationChoice(1);
                break;
            case Key.Enter:
            case Key.Space:
                ExecuteQuitConfirmationChoice();
                break;
            case Key.Escape:
                DismissQuitConfirmation();
                break;
        }

        eventArgs.Handled = true;
    }

    private void HandleQuitConfirmationGamepad(GamepadButton presses)
    {
        if (presses.HasFlag(GamepadButton.DPadLeft) || presses.HasFlag(GamepadButton.DPadUp))
        {
            SelectQuitConfirmationChoice(0);
        }

        if (presses.HasFlag(GamepadButton.DPadRight) || presses.HasFlag(GamepadButton.DPadDown))
        {
            SelectQuitConfirmationChoice(1);
        }

        if (presses.HasFlag(GamepadButton.A))
        {
            ExecuteQuitConfirmationChoice();
        }
        else if (presses.HasFlag(GamepadButton.B))
        {
            DismissQuitConfirmation();
        }
    }

    private void SelectQuitConfirmationChoice(int index)
    {
        if (_quitConfirmationIndex == index)
        {
            return;
        }

        _quitConfirmationIndex = index;
        if (index == 0)
        {
            QuitCancelButton.Focus();
        }
        else
        {
            QuitConfirmButton.Focus();
        }

        PlayNavigationTone();
    }

    private void ExecuteQuitConfirmationChoice()
    {
        if (_quitConfirmationIndex == 0)
        {
            DismissQuitConfirmation();
        }
        else
        {
            _ = ConfirmQuitAsync();
        }
    }

    private async Task ConfirmQuitAsync()
    {
        if (!_isQuitConfirmationVisible || _isClosingAfterConfirmation)
        {
            return;
        }

        _isClosingAfterConfirmation = true;
        QuitCancelButton.IsEnabled = false;
        QuitConfirmButton.IsEnabled = false;
        _dashboardSounds.PlayConfirm();
        await Task.Delay(170);
        _quitConfirmed = true;
        Close();
    }

    private void LaunchSelectedGame()
    {
        if (_emulatorWindow is not null ||
            DataContext is not MainViewModel viewModel ||
            viewModel.GetSelectedGameForLaunch() is not { CanLaunch: true } game)
        {
            return;
        }

        _gamepadTimer.Stop();
        _clockTimer.Stop();
        _dashboardSounds.PlayConfirm();
        _emulatorWindow = new EmulatorWindow(game);
        _emulatorWindow.Closed += OnEmulatorClosed;
        Hide();
        _emulatorWindow.Show();
    }

    private void OnEmulatorClosed(object? sender, EventArgs eventArgs)
    {
        if (_emulatorWindow is not null)
        {
            _emulatorWindow.Closed -= OnEmulatorClosed;
            _emulatorWindow = null;
        }

        Show();
        Activate();
        if (DataContext is MainViewModel viewModel)
        {
            _dashboardSoundsEnabled = false;
            viewModel.RefreshPlayHistory();
            _dashboardSoundsEnabled = true;
            Dispatcher.UIThread.Post(() => FocusPageContent(viewModel), DispatcherPriority.Input);
        }

        _clockTimer.Start();
        _gamepadTimer.Start();
    }

    private void ConfigureWatcher(string gamesFolder)
    {
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(gamesFolder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
        };

        _watcher.Created += QueueLibraryRefresh;
        _watcher.Changed += QueueLibraryRefresh;
        _watcher.Deleted += QueueLibraryRefresh;
        _watcher.Renamed += QueueLibraryRefresh;
        _watcher.EnableRaisingEvents = true;
    }

    private void QueueLibraryRefresh(object sender, FileSystemEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            _libraryRefreshTimer.Stop();
            _libraryRefreshTimer.Start();
        });

    private async void RefreshLibrary(object? sender, EventArgs eventArgs)
    {
        _libraryRefreshTimer.Stop();
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.RefreshAsync();
        }
    }

    private enum DashboardNavigationRegion
    {
        PageContent,
        LibraryTabs,
        PrimaryTabs
    }
}
