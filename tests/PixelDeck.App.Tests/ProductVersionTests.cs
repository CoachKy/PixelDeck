using PixelDeck.App.ViewModels;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Tests;

public sealed class ProductVersionTests
{
    [Fact]
    public void Assemblies_HaveIndependentProductVersions()
    {
        Assert.Equal(new Version(0, 8, 0, 0), typeof(MainViewModel).Assembly.GetName().Version);
        Assert.Equal(new Version(1, 7, 0, 0), typeof(NesMachine).Assembly.GetName().Version);
        Assert.Equal(new Version(1, 3, 1, 0), typeof(SnesMachine).Assembly.GetName().Version);
    }

    [Fact]
    public void Dashboard_FormatsAndSwitchesProductVersionLabels()
    {
        using var viewModel = new MainViewModel();

        Assert.Equal("PixelDeck v0.8.000", viewModel.PixelDeckVersionText);
        Assert.Equal("PixelNES v1.7.000", viewModel.LibraryEmulatorVersionText);

        viewModel.SelectedLibrarySystem = LibrarySystem.SuperNintendo;

        Assert.Equal("PixelSNES v1.3.001", viewModel.LibraryEmulatorVersionText);
    }
}
