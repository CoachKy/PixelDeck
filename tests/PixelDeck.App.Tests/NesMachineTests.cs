using PixelDeck.App.Services;
using PixelDeck.Emulation.Nes;
using SkiaSharp;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

public sealed class NesMachineTests
{
    private readonly ITestOutputHelper _output;

    public NesMachineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void LocalNesImagesCompleteSeveralFramesWhenPresent()
    {
        var gamesFolder = FindGamesFolder();
        if (!Directory.Exists(gamesFolder))
        {
            return;
        }

        var gameFilter = Environment.GetEnvironmentVariable("PIXELDECK_NES_GAME_FILTER");
        var discoveredImages = Directory
            .EnumerateFiles(gamesFolder, "*.nes", SearchOption.AllDirectories)
            .Where(path =>
                string.IsNullOrWhiteSpace(gameFilter) ||
                Path.GetFileNameWithoutExtension(path).Contains(
                    gameFilter,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var supportedImages = 0;
        var imagesWithAudio = 0;
        var failures = new List<string>();

        foreach (var gamePath in discoveredImages)
        {
            try
            {
                var machine = NesMachine.Load(gamePath);
                supportedImages++;

                uint[] lastFrame = [];
                var audioPeak = 0f;
                for (var frame = 0; frame < 240; frame++)
                {
                    var pixels = machine.RunFrame();
                    Assert.Equal(256 * 240, pixels.Length);
                    lastFrame = pixels.ToArray();
                    audioPeak = Math.Max(audioPeak, DrainAudioPeak(machine));
                }

                machine.SetControllerState(1, NesButton.Start | NesButton.A);
                machine.RunFrame();
                audioPeak = Math.Max(audioPeak, DrainAudioPeak(machine));
                machine.RunFrame();
                audioPeak = Math.Max(audioPeak, DrainAudioPeak(machine));
                machine.SetControllerState(1, NesButton.None);
                for (var frame = 0; frame < 360; frame++)
                {
                    lastFrame = machine.RunFrame().ToArray();
                    audioPeak = Math.Max(audioPeak, DrainAudioPeak(machine));
                }

                CaptureFrameWhenRequested(gamePath, lastFrame);
                var distinctColors = lastFrame.Distinct().Count();
                _output.WriteLine($"{Path.GetFileName(gamePath)}: mapper {machine.Cartridge.MapperNumber}, PC=${machine.ProgramCounter:X4}, colors={distinctColors}, audio peak={audioPeak:0.000}");
                if (audioPeak >= 0.0001f)
                {
                    imagesWithAudio++;
                }

                if (machine.CpuCycles <= 0 || distinctColors < 2)
                {
                    failures.Add($"{Path.GetFileName(gamePath)} did not produce an active frame (PC=${machine.ProgramCounter:X4}, colors={distinctColors}).");
                }
            }
            catch (NotSupportedException)
            {
                continue;
            }
            catch (Exception exception)
            {
                failures.Add($"{Path.GetFileName(gamePath)} failed: {exception.Message}");
            }
        }

        if (discoveredImages.Length > 0)
        {
            Assert.True(supportedImages > 0, "At least one local NES image should use an implemented mapper.");
            if (string.IsNullOrWhiteSpace(gameFilter))
            {
                Assert.True(
                    imagesWithAudio > 0,
                    "At least one local NES image should exercise the APU during the smoke test.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void SaveStateRestoresTheExactNextFrameWhenALocalImageIsPresent()
    {
        var gamePaths = FindSupportedLocalImages().ToArray();
        if (gamePaths.Length == 0)
        {
            return;
        }

        foreach (var gamePath in gamePaths)
        {
            var machine = NesMachine.Load(gamePath);
            for (var frame = 0; frame < 30; frame++)
            {
                machine.RunFrame();
            }

            var state = machine.SaveState();
            var expectedProgramCounter = machine.ProgramCounter;
            var expectedCycles = machine.CpuCycles;
            var expectedNextFrame = machine.RunFrame().ToArray();

            machine.LoadState(state);

            Assert.Equal(expectedProgramCounter, machine.ProgramCounter);
            Assert.Equal(expectedCycles, machine.CpuCycles);
            Assert.Equal(expectedNextFrame, machine.RunFrame().ToArray());
        }
    }

    private static IEnumerable<string> FindSupportedLocalImages()
    {
        var gamesFolder = FindGamesFolder();
        if (!Directory.Exists(gamesFolder))
        {
            yield break;
        }

        foreach (var gamePath in Directory.EnumerateFiles(gamesFolder, "*.nes", SearchOption.AllDirectories))
        {
            var supported = false;
            try
            {
                _ = NesMachine.Load(gamePath);
                supported = true;
            }
            catch (NotSupportedException)
            {
            }

            if (supported)
            {
                yield return gamePath;
            }
        }
    }

    private static string FindGamesFolder()
    {
        var configuredGamesFolder = Environment.GetEnvironmentVariable("PIXELDECK_GAMES_FOLDER");
        if (!string.IsNullOrWhiteSpace(configuredGamesFolder))
        {
            return Path.GetFullPath(configuredGamesFolder);
        }

        var workingDirectoryGames = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Games"));
        return Directory.Exists(workingDirectoryGames)
            ? workingDirectoryGames
            : Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "Games"));
    }

    private static float DrainAudioPeak(NesMachine machine)
    {
        var samples = new float[machine.BufferedAudioSampleCount];
        machine.ReadAudioSamples(samples);
        return samples.Length == 0 ? 0 : samples.Max(sample => Math.Abs(sample));
    }

    private static void CaptureFrameWhenRequested(string gamePath, uint[] frame)
    {
        var captureFolder = Environment.GetEnvironmentVariable("PIXELDECK_CAPTURE_NES");
        if (string.IsNullOrWhiteSpace(captureFolder))
        {
            return;
        }

        frame = frame.ToArray();
        NesFramePresentation.MaskHorizontalOverscan(frame, NesPpu.Width, NesPpu.Height);
        Directory.CreateDirectory(captureFolder);
        using var bitmap = new SKBitmap(256, 240, SKColorType.Bgra8888, SKAlphaType.Opaque);
        for (var y = 0; y < 240; y++)
        {
            for (var x = 0; x < 256; x++)
            {
                var pixel = frame[(y * 256) + x];
                bitmap.SetPixel(
                    x,
                    y,
                    new SKColor(
                        (byte)(pixel >> 16),
                        (byte)(pixel >> 8),
                        (byte)pixel,
                        0xFF));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var fileName = Path.GetFileNameWithoutExtension(gamePath) + ".png";
        using var stream = File.Create(Path.Combine(captureFolder, fileName));
        data.SaveTo(stream);
    }
}
