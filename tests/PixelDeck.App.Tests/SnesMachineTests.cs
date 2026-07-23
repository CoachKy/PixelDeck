using PixelDeck.Emulation.Snes;
using SkiaSharp;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

public sealed class SnesMachineTests
{
    private readonly ITestOutputHelper _output;

    public SnesMachineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SyntheticLoRomRunsFramesAndRestoresState()
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var machine = SnesMachine.Load(gamePath);
            var firstFrame = machine.RunFrame().ToArray();

            Assert.Equal(256 * 224, firstFrame.Length);
            Assert.Contains(firstFrame, color => color != 0xFF000000u);
            Assert.True(machine.CpuCycles > 0);

            var state = machine.SaveState();
            var expectedAddress = machine.ProgramAddress;
            var expectedCycles = machine.CpuCycles;
            var expectedFrame = machine.RunFrame().ToArray();

            machine.LoadState(state);

            Assert.Equal(expectedAddress, machine.ProgramAddress);
            Assert.Equal(expectedCycles, machine.CpuCycles);
            Assert.Equal(expectedFrame, machine.RunFrame().ToArray());
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void EightBitAccumulatorTransfersItsFullHiddenValueToSixteenBitIndexes()
    {
        var gamePath = CreateSyntheticLoRom(
        [
            0x18,             // CLC
            0xFB,             // XCE - enter native mode
            0xC2, 0x10,       // REP #$10 - 16-bit indexes
            0xE2, 0x20,       // SEP #$20 - 8-bit accumulator
            0xA9, 0x12,       // LDA #$12
            0xEB,             // XBA - hidden high byte = $12
            0xA9, 0x34,       // LDA #$34 - full C register = $1234
            0xAA,             // TAX
            0x8E, 0x00, 0x00, // STX $0000
            0xA8,             // TAY
            0x8C, 0x02, 0x00, // STY $0002
            0xDB              // STP
        ]);

        try
        {
            var machine = SnesMachine.Load(gamePath);
            machine.RunFrame();

            Assert.Equal(0x34, machine.PeekMemory(0x0000));
            Assert.Equal(0x12, machine.PeekMemory(0x0001));
            Assert.Equal(0x34, machine.PeekMemory(0x0002));
            Assert.Equal(0x12, machine.PeekMemory(0x0003));
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void LocalSnesImagesCompleteFramesWhenPresent()
    {
        var gamesFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Games", "SuperNintendo"));
        if (!Directory.Exists(gamesFolder))
        {
            return;
        }

        var gamePaths = Directory.EnumerateFiles(gamesFolder)
            .Where(path => Path.GetExtension(path) is ".sfc" or ".smc")
            .Where(path =>
            {
                var filter = Environment.GetEnvironmentVariable("PIXELDECK_SNES_GAME");
                return string.IsNullOrWhiteSpace(filter) ||
                       Path.GetFileName(path).Contains(filter, StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        var requestedFrameCount = int.TryParse(
            Environment.GetEnvironmentVariable("PIXELDECK_SNES_FRAMES"),
            out var parsedFrameCount)
            ? Math.Max(1, parsedFrameCount)
            : (int?)null;
        var supportedImages = 0;
        var failures = new List<string>();

        foreach (var gamePath in gamePaths)
        {
            try
            {
                var machine = SnesMachine.Load(gamePath);
                supportedImages++;
                var gameName = Path.GetFileName(gamePath);
                var frameCount = requestedFrameCount ?? RequiredBootFrames(gameName);
                uint[] frame = [];
                var maximumColors = 0;
                var visibleFrames = 0;
                var audioBuffer = new float[4096];
                long audioSamplesRead = 0;
                var audioPeak = 0f;
                var isZelda = gameName
                    .Contains("Link to the Past", StringComparison.OrdinalIgnoreCase);
                var zeldaStartFrames = 0;
                for (var index = 0; index < frameCount; index++)
                {
                    if (isZelda &&
                        zeldaStartFrames == 0 &&
                        machine.PeekMemory(0x7E0010) == 0 &&
                        machine.PeekMemory(0x7E0011) >= 8)
                    {
                        zeldaStartFrames = 4;
                    }

                    machine.SetControllerState(
                        1,
                        zeldaStartFrames > 0 || (!isZelda && index is >= 180 and < 184)
                            ? SnesButton.Start
                            : SnesButton.None);
                    if (zeldaStartFrames > 0)
                    {
                        zeldaStartFrames--;
                    }
                    frame = machine.RunFrame().ToArray();
                    int chunkRead;
                    while ((chunkRead = machine.ReadAudioSamples(audioBuffer)) > 0)
                    {
                        audioSamplesRead += chunkRead;
                        for (var sampleIndex = 0; sampleIndex < chunkRead; sampleIndex++)
                        {
                            audioPeak = Math.Max(audioPeak, Math.Abs(audioBuffer[sampleIndex]));
                        }
                    }

                    maximumColors = Math.Max(maximumColors, frame.Distinct().Count());
                    if (!machine.IsDisplayBlanked)
                    {
                        visibleFrames++;
                    }
                }

                var colors = frame.Distinct().Count();
                var dspState =
                    $"voices={machine.ActiveAudioVoiceCount}, " +
                    $"FLG=${machine.ReadDspRegister(0x6C):X2}, " +
                    $"MVOL=${machine.ReadDspRegister(0x0C):X2}/${machine.ReadDspRegister(0x1C):X2}, " +
                    $"KOFF=${machine.ReadDspRegister(0x5C):X2}, " +
                    $"DIR=${machine.ReadDspRegister(0x5D):X2}, " +
                    $"ENDX=${machine.ReadDspRegister(0x7C):X2}";
                CaptureFrameWhenRequested(gamePath, frame, machine.Width, machine.Height);
                _output.WriteLine(
                    $"{gameName}: {machine.Cartridge.Info.MapMode}, frames={frameCount}, " +
                    $"PC=${machine.ProgramAddress:X6}, colors={colors}/{maximumColors}, visible={visibleFrames}, blank={machine.IsDisplayBlanked}, " +
                    $"brightness={machine.DisplayBrightness}, mode={machine.BackgroundMode}, " +
                    $"layers=${machine.MainScreenLayers:X2}, PPU writes={machine.PpuRegisterWriteCount}, " +
                    $"APU=${machine.ApuOutputWord:X4}, " +
                    $"SPC={machine.ApuExecutedInstructions} unsupported=${machine.ApuFirstUnsupportedOpcode:X2}" +
                    $"@${machine.ApuFirstUnsupportedAddress:X4}, " +
                    $"audio={audioSamplesRead / 2} stereo frames peak={audioPeak:F4} ({dspState}), " +
                    $"VRAM={machine.NonZeroVramBytes}, CGRAM={machine.NonZeroCgramBytes}, OAM={machine.NonZeroOamBytes}, " +
                    $"NMI={machine.NmiCount}, IRQ={machine.IrqCount}, BRK={machine.BrkCount}, COP={machine.CopCount}, " +
                    $"BRK first/last=${machine.FirstBrkAddress:X6}/${machine.LastBrkAddress:X6}, " +
                    $"reset reentry={machine.ResetVectorReentryCount}");
                if (isZelda)
                {
                    _output.WriteLine(
                        $"Zelda WRAM: main={machine.PeekMemory(0x7E0010)}, " +
                        $"sub={machine.PeekMemory(0x7E0011)}, " +
                        $"subsub={machine.PeekMemory(0x7E00B0)}, " +
                        $"frame={machine.PeekMemory(0x7E001A)}, " +
                        $"intro={machine.PeekMemory(0x7E1E0A)}, " +
                        $"INIDISP=${machine.PeekMemory(0x7E0013):X2}");
                }

                if (machine.CpuCycles <= 0)
                {
                    failures.Add($"{gameName} did not execute CPU cycles.");
                }

                if (IsKnownSample(gameName))
                {
                    if (machine.BrkCount != 0 ||
                        machine.CopCount != 0 ||
                        machine.ApuFirstUnsupportedAddress != ushort.MaxValue)
                    {
                        failures.Add(
                            $"{gameName} entered an invalid CPU/APU path " +
                            $"(BRK={machine.BrkCount}, COP={machine.CopCount}, " +
                            $"SPC unsupported=${machine.ApuFirstUnsupportedOpcode:X2}@${machine.ApuFirstUnsupportedAddress:X4}).");
                    }

                    var minimumColors = gameName.Contains("Mega Man X", StringComparison.OrdinalIgnoreCase) ? 3 : 8;
                    if (maximumColors < minimumColors || visibleFrames < 100)
                    {
                        failures.Add(
                            $"{gameName} did not reach its expected visible boot scene " +
                            $"(maximum colors={maximumColors}, visible frames={visibleFrames}).");
                    }

                    if (audioPeak < 0.0001f)
                    {
                        failures.Add(
                            $"{gameName} did not produce audible S-DSP samples " +
                            $"({audioSamplesRead / 2} stereo frames, peak={audioPeak:F4}).");
                    }
                }

                if (isZelda &&
                    frameCount >= 900 &&
                    (visibleFrames < 300 || machine.PeekMemory(0x7E0010) == 0))
                {
                    failures.Add(
                        $"{gameName} did not progress beyond its intro " +
                        $"(visible frames={visibleFrames}, main module={machine.PeekMemory(0x7E0010)}).");
                }
            }
            catch (Exception exception) when (exception is NotSupportedException or InvalidDataException)
            {
                continue;
            }
            catch (Exception exception)
            {
                failures.Add($"{Path.GetFileName(gamePath)} failed: {exception.Message}");
            }
        }

        if (gamePaths.Length > 0)
        {
            Assert.True(supportedImages > 0, "At least one local SNES image should use a supported standard map mode.");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static int RequiredBootFrames(string gameName) =>
        gameName.Contains("Donkey Kong Country", StringComparison.OrdinalIgnoreCase) ||
        gameName.Contains("Final Fantasy III", StringComparison.OrdinalIgnoreCase) ||
        gameName.Contains("Link to the Past", StringComparison.OrdinalIgnoreCase)
            ? 1_200
            : 600;

    private static bool IsKnownSample(string gameName) =>
        gameName.Contains("chrono", StringComparison.OrdinalIgnoreCase) ||
        gameName.Contains("Donkey Kong Country", StringComparison.OrdinalIgnoreCase) ||
        gameName.Contains("Final Fantasy III", StringComparison.OrdinalIgnoreCase) ||
        gameName.Contains("Link to the Past", StringComparison.OrdinalIgnoreCase) ||
        gameName.Contains("Mega Man X", StringComparison.OrdinalIgnoreCase) ||
        gameName.Contains("Super Mario World", StringComparison.OrdinalIgnoreCase);

    private static string CreateSyntheticLoRom(byte[]? program = null)
    {
        var image = new byte[32 * 1024];
        program ??=
        [
            0x78,             // SEI
            0xA9, 0x0F,       // LDA #$0F
            0x8D, 0x00, 0x21, // STA $2100 - display on, full brightness
            0xA9, 0x1F,       // LDA #$1F
            0x8D, 0x22, 0x21, // STA $2122 - backdrop red low byte
            0xA9, 0x00,
            0x8D, 0x22, 0x21, // STA $2122 - backdrop red high byte
            0xDB              // STP
        ];
        program.CopyTo(image, 0);

        const int header = 0x7FC0;
        "PIXELDECK SNES TEST  ".Select(character => (byte)character).ToArray().CopyTo(image, header);
        image[header + 0x15] = 0x20;
        image[header + 0x16] = 0x00;
        image[header + 0x17] = 0x05;
        image[header + 0x18] = 0x00;
        image[header + 0x19] = 0x01;
        image[header + 0x1C] = 0xCB;
        image[header + 0x1D] = 0xED;
        image[header + 0x1E] = 0x34;
        image[header + 0x1F] = 0x12;
        image[header + 0x3C] = 0x00;
        image[header + 0x3D] = 0x80;

        var path = Path.Combine(Path.GetTempPath(), $"PixelDeck-{Guid.NewGuid():N}.sfc");
        File.WriteAllBytes(path, image);
        return path;
    }

    private static void CaptureFrameWhenRequested(string gamePath, uint[] frame, int width, int height)
    {
        var captureFolder = Environment.GetEnvironmentVariable("PIXELDECK_CAPTURE_SNES");
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
        var fileName = Path.GetFileNameWithoutExtension(gamePath) + ".png";
        using var stream = File.Create(Path.Combine(captureFolder, fileName));
        data.SaveTo(stream);
    }
}
