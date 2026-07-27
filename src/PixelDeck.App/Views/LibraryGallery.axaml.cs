using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PixelDeck.App.Models;

namespace PixelDeck.App.Views;

public partial class LibraryGallery : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<LibrarySection>?> SectionsProperty =
        AvaloniaProperty.Register<LibraryGallery, IReadOnlyList<LibrarySection>?>(nameof(Sections));

    public static readonly StyledProperty<GameEntry?> SelectedGameProperty =
        AvaloniaProperty.Register<LibraryGallery, GameEntry?>(
            nameof(SelectedGame),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<LibrarySection?> SelectedSectionProperty =
        AvaloniaProperty.Register<LibraryGallery, LibrarySection?>(
            nameof(SelectedSection),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsIndexNavigationActiveProperty =
        AvaloniaProperty.Register<LibraryGallery, bool>(nameof(IsIndexNavigationActive));

    public LibraryGallery()
    {
        InitializeComponent();
    }

    public event EventHandler? SelectionChanged;

    public event EventHandler? GameInvoked;

    public IReadOnlyList<LibrarySection>? Sections
    {
        get => GetValue(SectionsProperty);
        set => SetValue(SectionsProperty, value);
    }

    public GameEntry? SelectedGame
    {
        get => GetValue(SelectedGameProperty);
        set => SetValue(SelectedGameProperty, value);
    }

    public LibrarySection? SelectedSection
    {
        get => GetValue(SelectedSectionProperty);
        set => SetValue(SelectedSectionProperty, value);
    }

    public bool IsIndexNavigationActive
    {
        get => GetValue(IsIndexNavigationActiveProperty);
        set => SetValue(IsIndexNavigationActiveProperty, value);
    }

    public bool EnterIndexNavigation()
    {
        if (Sections is not { Count: > 0 } sections)
        {
            return false;
        }

        SelectedSection = FindSection(sections, SelectedGame) ?? sections[0];
        IsIndexNavigationActive = true;
        SectionIndex.ScrollIntoView(SelectedSection);
        Focus();
        return true;
    }

    public void ExitIndexNavigation()
    {
        IsIndexNavigationActive = false;
    }

    public bool MoveIndexSelection(int direction)
    {
        if (!IsIndexNavigationActive ||
            direction == 0 ||
            Sections is not { Count: > 0 } sections)
        {
            return false;
        }

        var currentIndex = SelectedSection is null ? 0 : IndexOfSection(sections, SelectedSection);
        var nextIndex = LibraryOrganizer.MoveSectionIndex(currentIndex, direction, sections.Count);
        if (nextIndex == currentIndex)
        {
            return false;
        }

        SelectedSection = sections[nextIndex];
        SectionIndex.ScrollIntoView(SelectedSection);
        return true;
    }

    public bool ActivateSelectedSection()
    {
        if (SelectedSection is not { Games.Count: > 0 } section)
        {
            return false;
        }

        var game = section.Games[0];

        SetCurrentValue(SelectedGameProperty, game);
        IsIndexNavigationActive = false;
        Focus();
        ScrollIntoView(game);
        return true;
    }

    public bool IsSelectedGameInFirstColumn(int columnCount)
    {
        return Sections is { Count: > 0 } sections &&
               LibraryOrganizer.IsGameInFirstColumn(sections, SelectedGame, columnCount);
    }

    public void ScrollIntoView(GameEntry? game)
    {
        if (game is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                var card = this
                    .GetVisualDescendants()
                    .OfType<Border>()
                    .FirstOrDefault(border =>
                        border.Classes.Contains("game-card") &&
                        ReferenceEquals(border.DataContext, game));
                card?.BringIntoView();
            },
            DispatcherPriority.Loaded);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedGameProperty)
        {
            if (!IsIndexNavigationActive &&
                Sections is { Count: > 0 } sections)
            {
                SetCurrentValue(
                    SelectedSectionProperty,
                    FindSection(sections, change.NewValue as GameEntry) ?? sections[0]);
            }

            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (change.Property == SectionsProperty &&
                 change.NewValue is IReadOnlyList<LibrarySection> { Count: > 0 } sections)
        {
            SetCurrentValue(
                SelectedSectionProperty,
                FindSection(sections, SelectedGame) ?? sections[0]);
        }
    }

    private void OnGameCardTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (sender is Border { DataContext: GameEntry game })
        {
            IsIndexNavigationActive = false;
            SetCurrentValue(SelectedGameProperty, game);
            Focus();
            eventArgs.Handled = true;
        }
    }

    private void OnGameCardDoubleTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (sender is Border { DataContext: GameEntry game })
        {
            IsIndexNavigationActive = false;
            SetCurrentValue(SelectedGameProperty, game);
            Focus();
            GameInvoked?.Invoke(this, EventArgs.Empty);
            eventArgs.Handled = true;
        }
    }

    private void OnIndexEntryTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (sender is Border { DataContext: LibrarySection section })
        {
            SetCurrentValue(SelectedSectionProperty, section);
            IsIndexNavigationActive = true;
            ActivateSelectedSection();
            eventArgs.Handled = true;
        }
    }

    private static LibrarySection? FindSection(
        IReadOnlyList<LibrarySection> sections,
        GameEntry? game)
    {
        var sectionIndex = LibraryOrganizer.GetSectionIndex(sections, game);
        return sectionIndex < 0 ? null : sections[sectionIndex];
    }

    private static int IndexOfSection(
        IReadOnlyList<LibrarySection> sections,
        LibrarySection target)
    {
        for (var index = 0; index < sections.Count; index++)
        {
            if (ReferenceEquals(sections[index], target))
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOfGame(IReadOnlyList<GameEntry> games, GameEntry target)
    {
        for (var index = 0; index < games.Count; index++)
        {
            if (ReferenceEquals(games[index], target))
            {
                return index;
            }
        }

        return -1;
    }
}
