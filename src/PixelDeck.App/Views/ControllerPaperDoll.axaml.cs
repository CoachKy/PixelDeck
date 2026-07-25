using Avalonia;
using Avalonia.Controls;

namespace PixelDeck.App.Views;

public partial class ControllerPaperDoll : UserControl
{
    public static readonly StyledProperty<string> DeviceTextProperty =
        AvaloniaProperty.Register<ControllerPaperDoll, string>(
            nameof(DeviceText),
            "Controller not connected");

    public static readonly StyledProperty<string> ConsoleTextProperty =
        AvaloniaProperty.Register<ControllerPaperDoll, string>(nameof(ConsoleText), "NINTENDO");

    public static readonly StyledProperty<string> SouthActionTextProperty =
        AvaloniaProperty.Register<ControllerPaperDoll, string>(nameof(SouthActionText), "—");

    public static readonly StyledProperty<string> EastActionTextProperty =
        AvaloniaProperty.Register<ControllerPaperDoll, string>(nameof(EastActionText), "—");

    public static readonly StyledProperty<string> WestActionTextProperty =
        AvaloniaProperty.Register<ControllerPaperDoll, string>(nameof(WestActionText), "—");

    public static readonly StyledProperty<string> NorthActionTextProperty =
        AvaloniaProperty.Register<ControllerPaperDoll, string>(nameof(NorthActionText), "—");

    public static readonly StyledProperty<string> LeftShoulderActionTextProperty =
        AvaloniaProperty.Register<ControllerPaperDoll, string>(nameof(LeftShoulderActionText), "—");

    public static readonly StyledProperty<string> RightShoulderActionTextProperty =
        AvaloniaProperty.Register<ControllerPaperDoll, string>(nameof(RightShoulderActionText), "—");

    public static readonly StyledProperty<string> StartActionTextProperty =
        AvaloniaProperty.Register<ControllerPaperDoll, string>(nameof(StartActionText), "START");

    public static readonly StyledProperty<string> BackActionTextProperty =
        AvaloniaProperty.Register<ControllerPaperDoll, string>(nameof(BackActionText), "SELECT");

    public ControllerPaperDoll()
    {
        InitializeComponent();
    }

    public string DeviceText
    {
        get => GetValue(DeviceTextProperty);
        set => SetValue(DeviceTextProperty, value);
    }

    public string ConsoleText
    {
        get => GetValue(ConsoleTextProperty);
        set => SetValue(ConsoleTextProperty, value);
    }

    public string SouthActionText
    {
        get => GetValue(SouthActionTextProperty);
        set => SetValue(SouthActionTextProperty, value);
    }

    public string EastActionText
    {
        get => GetValue(EastActionTextProperty);
        set => SetValue(EastActionTextProperty, value);
    }

    public string WestActionText
    {
        get => GetValue(WestActionTextProperty);
        set => SetValue(WestActionTextProperty, value);
    }

    public string NorthActionText
    {
        get => GetValue(NorthActionTextProperty);
        set => SetValue(NorthActionTextProperty, value);
    }

    public string LeftShoulderActionText
    {
        get => GetValue(LeftShoulderActionTextProperty);
        set => SetValue(LeftShoulderActionTextProperty, value);
    }

    public string RightShoulderActionText
    {
        get => GetValue(RightShoulderActionTextProperty);
        set => SetValue(RightShoulderActionTextProperty, value);
    }

    public string StartActionText
    {
        get => GetValue(StartActionTextProperty);
        set => SetValue(StartActionTextProperty, value);
    }

    public string BackActionText
    {
        get => GetValue(BackActionTextProperty);
        set => SetValue(BackActionTextProperty, value);
    }
}
