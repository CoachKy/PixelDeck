using PixelDeck.App.Input;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

public sealed class GamepadBackendTests(ITestOutputHelper output)
{
    [Fact]
    public void SdlStateUsesStandardFaceButtonsStickThresholdsAndR2()
    {
        var buttons = SdlGamepadBackend.Translate(new SdlGamepadState(
            GamepadButton.A | GamepadButton.B | GamepadButton.Guide,
            LeftX: -18_001,
            LeftY: 18_001,
            LeftTrigger: 4_096,
            RightTrigger: 4_097));

        Assert.Equal(
            GamepadButton.A |
            GamepadButton.B |
            GamepadButton.Guide |
            GamepadButton.DPadLeft |
            GamepadButton.DPadDown |
            GamepadButton.RightTrigger,
            buttons);
    }

    [Fact]
    public void PackagedSdlBackendInitializesAndEnumeratesControllerSlots()
    {
        Assert.True(SdlGamepadBackend.TryCreate(out var backend));
        using (backend)
        {
            var connections = backend.ReadConnections();
            Assert.InRange(connections.Count, 0, GamepadManager.MaximumControllers);

            output.WriteLine($"SDL controllers: {connections.Count}");
            for (var index = 0; index < GamepadManager.MaximumControllers; index++)
            {
                var name = backend.GetControllerName(index);
                output.WriteLine($"Slot {index + 1}: {name ?? "Not connected"}");
                if (connections.IsConnected(index))
                {
                    Assert.False(string.IsNullOrWhiteSpace(name));
                    output.WriteLine($"Buttons: {backend.ReadButtons(index)}");
                }
            }
        }
    }
}
