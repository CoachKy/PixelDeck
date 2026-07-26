using PixelDeck.App.ViewModels;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.N64;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Tests;

public sealed class ProductVersionTests
{
    [Fact]
    public void Assemblies_HaveIndependentProductVersions()
    {
        Assert.Equal(new Version(0, 18, 59, 0), typeof(MainViewModel).Assembly.GetName().Version);
        Assert.Equal(new Version(1, 15, 22, 0), typeof(NesMachine).Assembly.GetName().Version);
        Assert.Equal(new Version(0, 15, 19, 0), typeof(SnesMachine).Assembly.GetName().Version);
        Assert.Equal(new Version(0, 4, 6, 0), typeof(N64Machine).Assembly.GetName().Version);
    }

    [Fact]
    public void Dashboard_FormatsAndSwitchesProductVersionLabels()
    {
        using var viewModel = new MainViewModel();

        Assert.Equal("PixelDeck v0.18.059", viewModel.PixelDeckVersionText);
        Assert.Equal("PixelNES v1.15.022", viewModel.LibraryEmulatorVersionText);

        viewModel.SelectedLibrarySystem = LibrarySystem.SuperNintendo;

        Assert.Equal("PixelSNES v0.15.019", viewModel.LibraryEmulatorVersionText);

        viewModel.SelectedLibrarySystem = LibrarySystem.Nintendo64;

        Assert.Equal("Pixel64 v0.4.006", viewModel.LibraryEmulatorVersionText);
    }
}
