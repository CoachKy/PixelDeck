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
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnGameCardTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (sender is Border { DataContext: GameEntry game })
        {
            SetCurrentValue(SelectedGameProperty, game);
            Focus();
            eventArgs.Handled = true;
        }
    }

    private void OnGameCardDoubleTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (sender is Border { DataContext: GameEntry game })
        {
            SetCurrentValue(SelectedGameProperty, game);
            Focus();
            GameInvoked?.Invoke(this, EventArgs.Empty);
            eventArgs.Handled = true;
        }
    }
}
