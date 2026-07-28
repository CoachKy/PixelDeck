using System.Text.Json;
using System.Text.Json.Serialization;
using PixelDeck.App.Input;
using PixelDeck.App.Settings;
using PixelDeck.App.ViewModels;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.N64;
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
    public void Pixel64PreservesAnalogMagnitudeAndUsesTheRightStickAsCButtons()
    {
        var settings = new PixelDeckSettings();
        var state = new GamepadState(
            GamepadButton.A | GamepadButton.LeftTrigger,
            LeftX: 18_431,
            LeftY: short.MinValue,
            RightX: 13_000,
            RightY: 0);

        var controller = GamepadInputMapper.ToN64Controller(
            state,
            GamepadInputMapper.N64MapForPort(settings, 1));

        Assert.Equal(N64Button.A | N64Button.Z | N64Button.CRight, controller.Buttons);
        Assert.InRange(controller.StickX, (sbyte)39, (sbyte)41);
        Assert.Equal(-80, controller.StickY);
    }

    [Fact]
    public void Nintendo64MappingsSurviveAJsonRoundTripWithoutDuplicatingPorts()
    {
        var settings = new PixelDeckSettings();
        settings.N64Ports[2].CLeft = GamepadButton.Back;
        settings.PlayerFourControllerIndex = 1;

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        var restored = JsonSerializer.Deserialize<PixelDeckSettings>(
            JsonSerializer.Serialize(settings, options),
            options);

        Assert.NotNull(restored);
        Assert.Equal(N64ButtonMap.PortCount, restored.N64Ports.Count);
        Assert.Equal(GamepadButton.Back, restored.N64Ports[2].CLeft);
        Assert.Equal(GamepadButton.LeftThumb, restored.N64Ports[0].CLeft);
        Assert.Equal(1, restored.PlayerFourControllerIndex);
    }

    [Fact]
    public void EveryNintendo64PortStartsWithItsOwnEditableMapping()
    {
        var settings = new PixelDeckSettings();

        Assert.Equal(N64ButtonMap.PortCount, settings.N64Ports.Count);
        Assert.Equal(0, settings.ControllerIndex);
        Assert.Equal(1, settings.PlayerTwoControllerIndex);
        Assert.Equal(2, settings.PlayerThreeControllerIndex);
        Assert.Equal(3, settings.PlayerFourControllerIndex);

        // Re-mapping one port must not disturb the others.
        GamepadInputMapper.N64MapForPort(settings, 3).A = GamepadButton.B;

        Assert.Equal(GamepadButton.B, GamepadInputMapper.N64MapForPort(settings, 3).A);
        Assert.Equal(GamepadButton.A, GamepadInputMapper.N64MapForPort(settings, 1).A);
        Assert.Equal(GamepadButton.A, GamepadInputMapper.N64MapForPort(settings, 4).A);
    }

    [Fact]
    public void Nintendo64PortsTranslateTheirOwnButtonMappings()
    {
        var settings = new PixelDeckSettings();
        var portFour = GamepadInputMapper.N64MapForPort(settings, 4);
        portFour.A = GamepadButton.B;
        portFour.Z = GamepadButton.RightShoulder;

        // Clear the defaults those two physical buttons used to drive, so the assertion below is
        // about the remap rather than about two actions sharing a button.
        portFour.CDown = GamepadButton.None;
        portFour.R = GamepadButton.None;
        var gamepad = GamepadButton.B | GamepadButton.RightShoulder;

        Assert.Equal(
            N64Button.A | N64Button.Z,
            GamepadInputMapper.ToN64Controller(
                gamepad,
                GamepadInputMapper.N64MapForPort(settings, 4)).Buttons);

        // Port one keeps the defaults, where those same physical buttons mean C-down and R.
        Assert.Equal(
            N64Button.CDown | N64Button.R,
            GamepadInputMapper.ToN64Controller(
                gamepad,
                GamepadInputMapper.N64MapForPort(settings, 1)).Buttons);
    }

    [Fact]
    public void ClearingACButtonLeavesTheRightStickAsItsOnlyInput()
    {
        var settings = new PixelDeckSettings();
        var map = GamepadInputMapper.N64MapForPort(settings, 1);
        map.CDown = GamepadButton.None;

        Assert.Equal(
            N64Button.None,
            GamepadInputMapper.ToN64Controller(GamepadButton.B, map).Buttons);
        Assert.Equal(
            N64Button.CDown,
            GamepadInputMapper.ToN64Controller(
                new GamepadState(GamepadButton.None, 0, 0, 0, RightY: -20_000),
                map).Buttons);
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
            [true, true, false, false],
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

    [Fact]
    public void OnlyTheNintendo64ProfileOffersPlayersThreeAndFour()
    {
        using var viewModel = new MainViewModel();

        foreach (var console in viewModel.ControllerSetupConsoles)
        {
            viewModel.SelectedControllerSetupConsole = console;
            var expected = console.Console == ControllerSetupConsole.Nintendo64 ? 4 : 2;
            Assert.Equal(expected, viewModel.ControllerSetupPlayers.Count);
        }
    }

    [Fact]
    public void LeavingTheNintendo64ProfileMovesTheSelectionOffAHiddenPlayer()
    {
        using var viewModel = new MainViewModel();
        viewModel.SelectedControllerSetupConsole =
            viewModel.ControllerSetupConsoles.First(
                option => option.Console == ControllerSetupConsole.Nintendo64);
        viewModel.SelectedControllerSetupPlayer = viewModel.ControllerSetupPlayers[3];

        viewModel.SelectedControllerSetupConsole =
            viewModel.ControllerSetupConsoles.First(
                option => option.Console == ControllerSetupConsole.Nintendo);

        Assert.Equal(2, viewModel.ControllerSetupPlayers.Count);
        Assert.Equal(
            ControllerSetupPlayer.PlayerOne,
            viewModel.SelectedControllerSetupPlayer.Player);
        Assert.Contains(viewModel.SelectedControllerSetupPlayer, viewModel.ControllerSetupPlayers);
    }

    [Fact]
    public void EachPlayerKeepsADistinctPhysicalControllerSlot()
    {
        using var viewModel = new MainViewModel();

        // Player three grabbing player one's device must push player one somewhere free.
        viewModel.SelectedPlayerThreeControllerSlot = viewModel.ControllerSlots[0];

        var assigned = new[]
        {
            viewModel.SelectedControllerSlot.Index,
            viewModel.SelectedPlayerTwoControllerSlot.Index,
            viewModel.SelectedPlayerThreeControllerSlot.Index,
            viewModel.SelectedPlayerFourControllerSlot.Index
        };

        Assert.Equal(0, viewModel.SelectedPlayerThreeControllerSlot.Index);
        Assert.Equal(assigned.Length, assigned.Distinct().Count());
    }
}
