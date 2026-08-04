namespace PixelDeck.Emulation.GameCube;

public enum TevColorInput : byte
{
    CPrev = 0,
    APrev = 1,
    C0 = 2,
    A0 = 3,
    C1 = 4,
    A1 = 5,
    C2 = 6,
    A2 = 7,
    TexC = 8,
    TexA = 9,
    RasC = 10,
    RasA = 11,
    One = 12,
    Half = 13,
    Zero = 15
}

public enum TevScale : byte
{
    Scale1 = 0,
    Scale2 = 1,
    Scale4 = 2,
    ScaleHalf = 3
}

public struct TevStageConfig
{
    public TevColorInput A;
    public TevColorInput B;
    public TevColorInput C;
    public TevColorInput D;
    public TevScale Scale;

    public static TevStageConfig DefaultModulate => new()
    {
        A = TevColorInput.Zero,
        B = TevColorInput.TexC,
        C = TevColorInput.RasC,
        D = TevColorInput.Zero,
        Scale = TevScale.Scale1
    };
}

/// <summary>
/// Nintendo GameCube GX TEV (Texture Environment) multi-stage color and alpha combiner.
/// </summary>
public sealed class GameCubeTevPipeline
{
    private readonly TevStageConfig[] _stages = new TevStageConfig[16];

    public int StageCount { get; set; } = 1;

    public uint ConstantColor0 { get; set; } = 0xFFFFFFFFu;
    public uint ConstantColor1 { get; set; } = 0xFFFFFFFFu;
    public uint ConstantColor2 { get; set; } = 0xFFFFFFFFu;

    public GameCubeTevPipeline()
    {
        _stages[0] = TevStageConfig.DefaultModulate;
    }

    public void ConfigureStage(int stageIndex, TevStageConfig config)
    {
        if (stageIndex >= 0 && stageIndex < 16)
        {
            _stages[stageIndex] = config;
        }
    }

    public uint Evaluate(uint texColor, uint rasterColor)
    {
        var prevColor = rasterColor;

        for (var i = 0; i < Math.Clamp(StageCount, 1, 16); i++)
        {
            prevColor = EvaluateStage(_stages[i], texColor, rasterColor, prevColor);
        }

        return prevColor;
    }

    private uint EvaluateStage(TevStageConfig stage, uint texColor, uint rasterColor, uint prevColor)
    {
        var a = GetInput(stage.A, texColor, rasterColor, prevColor);
        var b = GetInput(stage.B, texColor, rasterColor, prevColor);
        var c = GetInput(stage.C, texColor, rasterColor, prevColor);
        var d = GetInput(stage.D, texColor, rasterColor, prevColor);

        var r = CombineChannel(a.r, b.r, c.r, d.r, stage.Scale);
        var g = CombineChannel(a.g, b.g, c.g, d.g, stage.Scale);
        var blue = CombineChannel(a.b, b.b, c.b, d.b, stage.Scale);
        var alpha = CombineChannel(a.a, b.a, c.a, d.a, stage.Scale);

        return (uint)((alpha << 24) | (r << 16) | (g << 8) | blue);
    }

    private (byte r, byte g, byte b, byte a) GetInput(
        TevColorInput input,
        uint texColor,
        uint rasterColor,
        uint prevColor)
    {
        var color = input switch
        {
            TevColorInput.CPrev => prevColor,
            TevColorInput.APrev => (prevColor & 0xFF000000u) | ((prevColor >> 24) * 0x010101u),
            TevColorInput.C0 => ConstantColor0,
            TevColorInput.C1 => ConstantColor1,
            TevColorInput.C2 => ConstantColor2,
            TevColorInput.TexC => texColor,
            TevColorInput.TexA => (texColor & 0xFF000000u) | ((texColor >> 24) * 0x010101u),
            TevColorInput.RasC => rasterColor,
            TevColorInput.RasA => (rasterColor & 0xFF000000u) | ((rasterColor >> 24) * 0x010101u),
            TevColorInput.One => 0xFFFFFFFFu,
            TevColorInput.Half => 0x80808080u,
            _ => 0x00000000u
        };

        return (
            (byte)((color >> 16) & 0xFF),
            (byte)((color >> 8) & 0xFF),
            (byte)(color & 0xFF),
            (byte)((color >> 24) & 0xFF)
        );
    }

    private static byte CombineChannel(byte a, byte b, byte c, byte d, TevScale scale)
    {
        // Formula: (D + (1 - C) * A + C * B) * Scale
        var factor = c / 255f;
        var interpolated = ((1f - factor) * a) + (factor * b);
        var result = (d + interpolated) * GetScaleMultiplier(scale);

        return (byte)Math.Clamp(result, 0f, 255f);
    }

    private static float GetScaleMultiplier(TevScale scale) => scale switch
    {
        TevScale.Scale2 => 2f,
        TevScale.Scale4 => 4f,
        TevScale.ScaleHalf => 0.5f,
        _ => 1f
    };
}
