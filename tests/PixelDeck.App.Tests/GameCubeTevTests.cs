using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public class GameCubeTevTests
{
    [Fact]
    public void TevPipeline_ModulatesTextureAndRasterColor()
    {
        var tev = new GameCubeTevPipeline();
        tev.StageCount = 1;
        tev.ConfigureStage(0, TevStageConfig.DefaultModulate);

        var texColor = 0xFF808080u; // Half intensity ARGB
        var rasColor = 0xFFFFFFFFu; // Full white

        var result = tev.Evaluate(texColor, rasColor);
        Assert.Equal(0xFF808080u, result);
    }

    [Fact]
    public void TevPipeline_EvaluatesScaleMultiplierCorrectly()
    {
        var tev = new GameCubeTevPipeline();
        tev.StageCount = 1;
        tev.ConfigureStage(0, new TevStageConfig
        {
            A = TevColorInput.Zero,
            B = TevColorInput.TexC,
            C = TevColorInput.RasC,
            D = TevColorInput.Zero,
            Scale = TevScale.Scale2
        });

        var texColor = 0xFF404040u;
        var rasColor = 0xFF808080u;

        var result = tev.Evaluate(texColor, rasColor);
        Assert.NotEqual(0u, result);
    }
}
