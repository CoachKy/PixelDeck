using PixelDeck.App.Input;
using PixelDeck.App.Settings;
using PixelDeck.App.ViewModels;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Tests;

public sealed class ControllerSettingsTests
{
    [Fact]
    public void NintendoAndSuperNintendoHaveIndependentDefaults()
    {
        var settings = new PixelDeckSettings();

        Assert.Equal(0, settings.ControllerIndex);
        Assert.Equal(1, settings.PlayerTwoControllerIndex);
        Assert.Equal(GamepadButton.A, settings.AButton);
        Assert.Equal(GamepadButton.X, settings.BButton);
        Assert.Equal(GamepadButton.Start, settings.StartButton);
        Assert.Equal(GamepadButton.Back, settings.SelectButton);
        Assert.Equal(GamepadButton.A, settings.PlayerTwoAButton);
        Assert.Equal(GamepadButton.X, settings.PlayerTwoBButton);
        Assert.Equal(GamepadButton.Start, settings.PlayerTwoStartButton);
        Assert.Equal(GamepadButton.Back, settings.PlayerTwoSelectButton);
        Assert.True(settings.RemoveNesSpriteLimit);
        Assert.True(settings.HideNesHorizontalOverscan);
        Assert.Equal(Mmc3IrqRevision.Auto, settings.Mmc3IrqRevision);
        Assert.Equal(NesPpuRevision.Rp2C02G, settings.NesPpuRevision);
        Assert.False(settings.EnableNesOamDecay);
        Assert.Equal(
            NesOamCorruptionMode.StableCpuPpuAlignment,
            settings.NesOamCorruptionMode);

        Assert.Equal(GamepadButton.B, settings.SnesAButton);
        Assert.Equal(GamepadButton.A, settings.SnesBButton);
        Assert.Equal(GamepadButton.Y, settings.SnesXButton);
        Assert.Equal(GamepadButton.X, settings.SnesYButton);
        Assert.Equal(GamepadButton.LeftShoulder, settings.SnesLButton);
        Assert.Equal(GamepadButton.RightShoulder, settings.SnesRButton);
        Assert.Equal(GamepadButton.Start, settings.SnesStartButton);
        Assert.Equal(GamepadButton.Back, settings.SnesSelectButton);
        Assert.Equal(GamepadButton.B, settings.PlayerTwoSnesAButton);
        Assert.Equal(GamepadButton.A, settings.PlayerTwoSnesBButton);
        Assert.Equal(GamepadButton.Y, settings.PlayerTwoSnesXButton);
        Assert.Equal(GamepadButton.X, settings.PlayerTwoSnesYButton);
        Assert.Equal(GamepadButton.LeftShoulder, settings.PlayerTwoSnesLButton);
        Assert.Equal(GamepadButton.RightShoulder, settings.PlayerTwoSnesRButton);
        Assert.Equal(GamepadButton.Start, settings.PlayerTwoSnesStartButton);
        Assert.Equal(GamepadButton.Back, settings.PlayerTwoSnesSelectButton);
    }

    [Fact]
    public void RightTriggerHasItsOwnInputFlagForFastForward()
    {
        Assert.NotEqual(GamepadButton.None, GamepadButton.RightTrigger);
        Assert.Equal(
            GamepadButton.None,
            GamepadButton.RightTrigger & (GamepadButton.A | GamepadButton.B | GamepadButton.RightShoulder));
    }

    [Fact]
    public void SharedMappingsTranslateEachPhysicalControllerForBothCores()
    {
        var settings = new PixelDeckSettings();
        var gamepad =
            GamepadButton.A |
            GamepadButton.X |
            GamepadButton.Start |
            GamepadButton.DPadRight;

        Assert.Equal(
            NesButton.A | NesButton.B | NesButton.Start | NesButton.Right,
            GamepadInputMapper.ToNesButtons(gamepad, settings));
        Assert.Equal(
            SnesButton.B | SnesButton.Y | SnesButton.Start | SnesButton.Right,
            GamepadInputMapper.ToSnesButtons(gamepad, settings));
    }

    [Fact]
    public void PlayerTwoCanUseIndependentMappingsForEachConsole()
    {
        var settings = new PixelDeckSettings
        {
            PlayerTwoAButton = GamepadButton.B,
            PlayerTwoBButton = GamepadButton.A,
            PlayerTwoSnesAButton = GamepadButton.X,
            PlayerTwoSnesBButton = GamepadButton.Y,
            PlayerTwoSnesXButton = GamepadButton.LeftShoulder
        };
        var gamepad = GamepadButton.B | GamepadButton.Y;

        Assert.Equal(
            NesButton.A,
            GamepadInputMapper.ToNesButtons(gamepad, settings, playerTwo: true));
        Assert.Equal(
            SnesButton.B,
            GamepadInputMapper.ToSnesButtons(gamepad, settings, playerTwo: true));
        Assert.Equal(
            NesButton.None,
            GamepadInputMapper.ToNesButtons(gamepad, settings));
    }

    [Fact]
    public void UnassignedButtonDoesNotReadAsPressedOnAnIdleController()
    {
        var settings = new PixelDeckSettings
        {
            AButton = GamepadButton.None,
            SnesAButton = GamepadButton.None
        };

        Assert.Equal(NesButton.None, GamepadInputMapper.ToNesButtons(GamepadButton.None, settings));
        Assert.Equal(SnesButton.None, GamepadInputMapper.ToSnesButtons(GamepadButton.None, settings));
    }

    [Theory]
    [InlineData(0, "NO CONTROLLERS CONNECTED")]
    [InlineData(1, "1 CONTROLLER CONNECTED")]
    [InlineData(2, "2 CONTROLLERS CONNECTED")]
    [InlineData(4, "4 CONTROLLERS CONNECTED")]
    public void ConnectedControllerCountUsesDashboardFriendlyText(int count, string expected)
    {
        Assert.Equal(expected, MainViewModel.FormatConnectedControllerCount(count));
    }

    [Fact]
    public void ConnectionSnapshotCountsAndIdentifiesLogicalSlots()
    {
        var connections = new GamepadConnections(0b1010);

        Assert.Equal(2, connections.Count);
        Assert.False(connections.IsConnected(0));
        Assert.True(connections.IsConnected(1));
        Assert.False(connections.IsConnected(2));
        Assert.True(connections.IsConnected(3));
    }

    [Fact]
    public void ControllerStatusShowsBackendAndDetectedDeviceNames()
    {
        using var viewModel = new MainViewModel();

        viewModel.UpdateControllerStatus(
            playerOneConnected: true,
            playerTwoConnected: true,
            connectedControllerCount: 2,
            ["DualSense Wireless Controller", "Xbox One Controller", null, null],
            "SDL3");

        Assert.Equal("SDL3 GAMEPAD INPUT", viewModel.ControllerInputBackendText);
        Assert.Equal("2 CONTROLLERS CONNECTED", viewModel.ConnectedControllerCountText);
        Assert.Equal(
            "Controller 1 — DualSense Wireless Controller",
            viewModel.ControllerSlots[0].Label);
        Assert.Equal("Controller 2 — Xbox One Controller", viewModel.ControllerSlots[1].Label);
        Assert.Equal("Controller 3 — Not connected", viewModel.ControllerSlots[2].Label);
    }
}
