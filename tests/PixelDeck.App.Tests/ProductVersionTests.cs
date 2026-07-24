using PixelDeck.App.ViewModels;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Tests;

public sealed class ProductVersionTests
{
    [Fact]
    public void Assemblies_HaveIndependentProductVersions()
    {
        Assert.Equal(new Version(0, 8, 31, 0), typeof(MainViewModel).Assembly.GetName().Version);
        Assert.Equal(new Version(1, 12, 16, 0), typeof(NesMachine).Assembly.GetName().Version);
        Assert.Equal(new Version(0, 9, 10, 0), typeof(SnesMachine).Assembly.GetName().Version);
    }

    [Fact]
    public void Dashboard_FormatsAndSwitchesProductVersionLabels()
    {
        using var viewModel = new MainViewModel();

        Assert.Equal("PixelDeck v0.8.031", viewModel.PixelDeckVersionText);
        Assert.Equal("PixelNES v1.12.016", viewModel.LibraryEmulatorVersionText);

        viewModel.SelectedLibrarySystem = LibrarySystem.SuperNintendo;

        Assert.Equal("PixelSNES v0.9.010", viewModel.LibraryEmulatorVersionText);
    }
}
