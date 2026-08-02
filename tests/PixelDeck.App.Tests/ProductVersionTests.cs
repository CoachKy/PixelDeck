using PixelDeck.App.ViewModels;
using PixelDeck.Emulation.GameCube;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.N64;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Tests;

public sealed class ProductVersionTests
{
    [Fact]
    public void Assemblies_HaveIndependentProductVersions()
    {
        Assert.Equal(new Version(1, 30, 90, 0), typeof(MainViewModel).Assembly.GetName().Version);
        Assert.Equal(new Version(1, 15, 23, 0), typeof(NesMachine).Assembly.GetName().Version);
        Assert.Equal(new Version(1, 16, 23, 0), typeof(SnesMachine).Assembly.GetName().Version);
        Assert.Equal(new Version(0, 16, 26, 0), typeof(N64Machine).Assembly.GetName().Version);
        Assert.Equal(new Version(0, 11, 11, 0), typeof(GameCubeMachine).Assembly.GetName().Version);
    }

    [Fact]
    public void Launcher_IsVersionedIndependentlyOfTheRelease()
    {
        // The launcher's version is what decides whether an update has to replace
        // PixelDeck.exe. Tying it to the release number would mean every release
        // looked like a launcher change, which is the opposite of the intent.
        var launcher = typeof(PixelDeck.Launcher.UpdateApplier).Assembly.GetName().Version;

        Assert.Equal(new Version(1, 1, 0, 0), launcher);
        Assert.NotEqual(typeof(MainViewModel).Assembly.GetName().Version, launcher);
    }

    [Fact]
    public void Dashboard_FormatsAndSwitchesProductVersionLabels()
    {
        using var viewModel = new MainViewModel();

        Assert.Equal("PixelDeck v1.30.090", viewModel.PixelDeckVersionText);
        Assert.Equal("PixelNES v1.15.023", viewModel.LibraryEmulatorVersionText);

        viewModel.SelectedLibrarySystem = LibrarySystem.SuperNintendo;

        Assert.Equal("PixelSNES v1.16.023", viewModel.LibraryEmulatorVersionText);

        viewModel.SelectedLibrarySystem = LibrarySystem.Nintendo64;

        Assert.Equal("Pixel64 v0.16.026", viewModel.LibraryEmulatorVersionText);

        viewModel.SelectedLibrarySystem = LibrarySystem.GameCube;

        Assert.Equal("PixelCube v0.11.011", viewModel.LibraryEmulatorVersionText);
    }
}
