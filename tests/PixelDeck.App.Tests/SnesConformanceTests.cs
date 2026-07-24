using PixelDeck.Emulation.Snes;
using SkiaSharp;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

public sealed class SnesConformanceTests
{
    private readonly ITestOutputHelper _output;

    public SnesConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void PinnedCpuTestScreensMatchTheirHardwareReferenceFrames()
    {
        var testRoot = Environment.GetEnvironmentVariable("PIXELDECK_SNES_TEST_ROMS");
        if (string.IsNullOrWhiteSpace(testRoot))
        {
            _output.WriteLine(
                "Set PIXELDECK_SNES_TEST_ROMS to run pinned SNES CPU conformance images.");
            return;
        }

        var testImages = Directory.EnumerateFiles(
                Path.GetFullPath(testRoot),
                "*.sfc",
                SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.NotEmpty(testImages);

        var failures = new List<string>();
        foreach (var imagePath in testImages)
        {
            var referencePath = Path.ChangeExtension(imagePath, ".png");
            Assert.True(
                File.Exists(referencePath),
                $"The hardware reference frame is missing for {Path.GetFileName(imagePath)}.");

            var machine = SnesMachine.Load(imagePath);
            var requiresReset = Path.GetFileNameWithoutExtension(imagePath)
                .Equals("CPUMSC", StringComparison.OrdinalIgnoreCase);
            for (var frame = 0; frame < 180; frame++)
            {
                machine.RunFrame();
                if (requiresReset && frame == 89)
                {
                    machine.Reset();
                }
            }

            using var reference = SKBitmap.Decode(referencePath);
            Assert.NotNull(reference);
            Assert.Equal(machine.Width, reference.Width);
            Assert.Equal(machine.Height, reference.Height);

            var differences = CountColorDifferences(machine.CurrentFrame, reference);
            CaptureActualWhenRequested(imagePath, machine.CurrentFrame, machine.Width, machine.Height);
            _output.WriteLine(
                $"{Path.GetFileName(imagePath)}: {differences} differing pixels, " +
                $"PC=${machine.ProgramAddress:X6}, cycles={machine.CpuCycles}.");
            if (differences != 0)
            {
                failures.Add(
                    $"{Path.GetFileName(imagePath)} differs from its hardware reference " +
                    $"at {differences:N0} of {machine.Width * machine.Height:N0} pixels.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static int CountColorDifferences(ReadOnlySpan<uint> actual, SKBitmap reference)
    {
        var differences = 0;
        for (var y = 0; y < reference.Height; y++)
        {
            for (var x = 0; x < reference.Width; x++)
            {
                var expectedColor = reference.GetPixel(x, y);
                var actualColor = actual[(y * reference.Width) + x];
                var expected15 = ToColor15(expectedColor.Red, expectedColor.Green, expectedColor.Blue);
                var actual15 = ToColor15(
                    (byte)(actualColor >> 16),
                    (byte)(actualColor >> 8),
                    (byte)actualColor);
                if (actual15 != expected15)
                {
                    differences++;
                }
            }
        }

        return differences;
    }

    private static ushort ToColor15(byte red, byte green, byte blue)
    {
        static int Quantize(byte component) => Math.Min(31, (component + 4) >> 3);
        return (ushort)(
            Quantize(red) |
            (Quantize(green) << 5) |
            (Quantize(blue) << 10));
    }

    private static void CaptureActualWhenRequested(
        string imagePath,
        ReadOnlySpan<uint> frame,
        int width,
        int height)
    {
        var captureRoot = Environment.GetEnvironmentVariable("PIXELDECK_CAPTURE_SNES");
        if (string.IsNullOrWhiteSpace(captureRoot))
        {
            return;
        }

        Directory.CreateDirectory(captureRoot);
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = frame[(y * width) + x];
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
        using var output = File.Create(
            Path.Combine(captureRoot, Path.GetFileNameWithoutExtension(imagePath) + "-actual.png"));
        data.SaveTo(output);
    }
}
