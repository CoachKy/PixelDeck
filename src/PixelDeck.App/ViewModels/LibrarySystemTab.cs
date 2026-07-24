using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PixelDeck.App.ViewModels;

public sealed partial class LibrarySystemTab : ObservableObject
{
    public LibrarySystemTab(
        LibrarySystem system,
        string label,
        string systemTitle,
        string folder,
        string emptyText,
        IReadOnlyList<string> platformCodes,
        string emulatorVersionText,
        Action select)
    {
        System = system;
        Label = label;
        SystemTitle = systemTitle;
        Folder = folder;
        EmptyText = emptyText;
        PlatformCodes = platformCodes;
        EmulatorVersionText = emulatorVersionText;
        SelectCommand = new RelayCommand(select);
    }

    public LibrarySystem System { get; }

    public string Label { get; }

    public string SystemTitle { get; }

    public string Folder { get; }

    public string EmptyText { get; }

    public IReadOnlyList<string> PlatformCodes { get; }

    public string EmulatorVersionText { get; }

    public IRelayCommand SelectCommand { get; }

    [ObservableProperty]
    private bool isSelected;
}
