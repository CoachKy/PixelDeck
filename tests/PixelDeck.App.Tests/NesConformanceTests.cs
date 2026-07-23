using System.Globalization;
using System.Text;
using PixelDeck.Emulation.Nes;
using SkiaSharp;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

public sealed class NesConformanceTests
{
    private const int MaximumFrames = 3_600;
    private readonly ITestOutputHelper _output;

    public NesConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InstalledBlarggCompatibleRomsPass()
    {
        var testRoot = ResolveTestRoot();
        if (!Directory.Exists(testRoot))
        {
            _output.WriteLine(
                "No NES conformance ROM directory is installed. See TestRoms/README.md or set PIXELDECK_NES_TEST_ROMS.");
            return;
        }

        var romPaths = Directory.EnumerateFiles(testRoot, "*.nes", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (romPaths.Length == 0)
        {
            _output.WriteLine($"No .nes conformance ROMs were found beneath {testRoot}.");
            return;
        }

        var failures = new List<string>();
        foreach (var romPath in romPaths)
        {
            try
            {
                var result = RunBlarggTest(romPath);
                _output.WriteLine(
                    $"{Path.GetRelativePath(testRoot, romPath)}: status={result.Status}, frames={result.Frames}, {result.Message}");
                if (result.Status != 0)
                {
                    failures.Add(
                        $"{Path.GetRelativePath(testRoot, romPath)} returned {result.Status}: {result.Message}");
                }
            }
            catch (Exception exception)
            {
                failures.Add($"{Path.GetRelativePath(testRoot, romPath)} crashed: {exception.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static BlarggResult RunBlarggTest(string romPath)
    {
        var maximumFrames = ResolveMaximumFrames();
        var machine = NesMachine.Load(
            romPath,
            options: new NesEmulationOptions
            {
                Mmc3IrqRevision = ResolveMmc3IrqRevision()
            });
        var protocolSeen = false;
        var resetRequestedAtFrame = -1;
        var waitingForResetAcknowledgement = false;

        for (var frame = 1; frame <= maximumFrames; frame++)
        {
            machine.RunFrame();
            if (machine.PeekCpuMemory(0x6001) != 0xDE ||
                machine.PeekCpuMemory(0x6002) != 0xB0 ||
                machine.PeekCpuMemory(0x6003) != 0x61)
            {
                continue;
            }

            protocolSeen = true;
            var status = machine.PeekCpuMemory(0x6000);
            if (waitingForResetAcknowledgement)
            {
                if (status == 0x81)
                {
                    continue;
                }

                waitingForResetAcknowledgement = false;
            }

            if (status < 0x80)
            {
                CaptureFrameWhenRequested(
                    romPath,
                    machine.CurrentFrame,
                    machine.Width,
                    machine.Height);
                return new BlarggResult(status, frame, ReadMessage(machine));
            }

            if (status == 0x81)
            {
                if (resetRequestedAtFrame < 0)
                {
                    resetRequestedAtFrame = frame;
                }
                else if (frame - resetRequestedAtFrame >= 7)
                {
                    machine.Reset();
                    resetRequestedAtFrame = -1;
                    waitingForResetAcknowledgement = true;
                }
            }
            else
            {
                resetRequestedAtFrame = -1;
            }
        }

        var visualResultAddress = ResolveVisualResultAddress();
        if (!protocolSeen && visualResultAddress is ushort resultAddress)
        {
            var result = machine.PeekCpuMemory(resultAddress);
            var status = result == 1 ? (byte)0 : result == 0 ? (byte)0xFF : result;
            CaptureFrameWhenRequested(
                romPath,
                machine.CurrentFrame,
                machine.Width,
                machine.Height);
            return new BlarggResult(
                status,
                maximumFrames,
                $"Visual result ${resultAddress:X4}=${result:X2}" +
                (result == 1 ? " (pass)." : "."));
        }

        var detail = protocolSeen
            ? $"Timed out while status was ${machine.PeekCpuMemory(0x6000):X2}. {ReadMessage(machine)}"
            : $"The ROM never published the Blargg $6000 signature. " +
              $"Diagnostic PC=${machine.ProgramCounter:X4}, $00F8=${machine.PeekCpuMemory(0x00F8):X2}.";
        CaptureFrameWhenRequested(
            romPath,
            machine.CurrentFrame,
            machine.Width,
            machine.Height);
        return new BlarggResult(0xFF, maximumFrames, detail);
    }

    private static ushort? ResolveVisualResultAddress()
    {
        var configured = Environment.GetEnvironmentVariable("PIXELDECK_NES_VISUAL_RESULT_ADDRESS");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var value = configured.Trim();
        if (value.StartsWith('$'))
        {
            value = value[1..];
        }
        else if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if (ushort.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
        {
            return address;
        }

        throw new InvalidOperationException(
            "PIXELDECK_NES_VISUAL_RESULT_ADDRESS must be a 16-bit hexadecimal address.");
    }

    private static int ResolveMaximumFrames()
    {
        var configured = Environment.GetEnvironmentVariable("PIXELDECK_NES_TEST_MAX_FRAMES");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return MaximumFrames;
        }

        if (int.TryParse(configured, out var frames) && frames > 0)
        {
            return frames;
        }

        throw new InvalidOperationException("PIXELDECK_NES_TEST_MAX_FRAMES must be a positive integer.");
    }

    private static void CaptureFrameWhenRequested(
        string romPath,
        ReadOnlySpan<uint> frame,
        int width,
        int height)
    {
        var captureFolder = Environment.GetEnvironmentVariable("PIXELDECK_CAPTURE_NES_CONFORMANCE");
        if (string.IsNullOrWhiteSpace(captureFolder))
        {
            return;
        }

        Directory.CreateDirectory(captureFolder);
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
        var fileName = Path.GetFileNameWithoutExtension(romPath) + ".png";
        using var stream = File.Create(Path.Combine(captureFolder, fileName));
        data.SaveTo(stream);
    }

    private static Mmc3IrqRevision ResolveMmc3IrqRevision()
    {
        var configured = Environment.GetEnvironmentVariable("PIXELDECK_NES_MMC3_IRQ_REVISION");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Mmc3IrqRevision.Auto;
        }

        if (Enum.TryParse<Mmc3IrqRevision>(configured, ignoreCase: true, out var revision) &&
            Enum.IsDefined(revision))
        {
            return revision;
        }

        throw new InvalidOperationException(
            "PIXELDECK_NES_MMC3_IRQ_REVISION must be Auto, Sharp, or Nec.");
    }

    private static string ReadMessage(NesMachine machine)
    {
        var message = new StringBuilder();
        for (var address = 0x6004; address <= 0x7FFF; address++)
        {
            var value = machine.PeekCpuMemory((ushort)address);
            if (value == 0)
            {
                break;
            }

            message.Append(value is >= 0x20 and <= 0x7E || value is (byte)'\r' or (byte)'\n'
                ? (char)value
                : '?');
        }

        return message.ToString().Trim();
    }

    private static string ResolveTestRoot()
    {
        var configured = Environment.GetEnvironmentVariable("PIXELDECK_NES_TEST_ROMS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "TestRoms"));
    }

    private sealed record BlarggResult(byte Status, int Frames, string Message);
}
