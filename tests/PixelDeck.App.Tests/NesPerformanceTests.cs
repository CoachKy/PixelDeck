using System.Diagnostics;
using PixelDeck.Emulation.Nes;

namespace PixelDeck.App.Tests;

public sealed class NesPerformanceTests
{
    [Fact]
    public void RenderingRunsAllocationFreeWithRealtimeHeadroom()
    {
        using var image = RenderingStressNesImage.Create();
        var machine = NesMachine.Load(
            image.Path,
            options: new NesEmulationOptions { RemoveSpriteLimit = true });
        var audio = new float[1_024];

        for (var frame = 0; frame < 20; frame++)
        {
            machine.RunFrame();
            machine.ReadAudioSamples(audio);
        }

        var timer = new Stopwatch();
        var frameDurations = new long[300];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        timer.Start();
        const int measuredFrames = 300;
        for (var frame = 0; frame < measuredFrames; frame++)
        {
            var frameStarted = Stopwatch.GetTimestamp();
            machine.RunFrame();
            machine.ReadAudioSamples(audio);
            frameDurations[frame] = Stopwatch.GetTimestamp() - frameStarted;
        }

        timer.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(frameDurations);
        var p99 = TimeSpan.FromSeconds(
            frameDurations[(int)(measuredFrames * 0.99) - 1] / (double)Stopwatch.Frequency);

#if DEBUG
        // Debug builds carry bounds checks and unoptimized dot-pipeline state
        // transitions. Keep a meaningful developer guard while Release below
        // retains the production performance budget.
        var totalBudget = TimeSpan.FromSeconds(3.25);
#else
        var totalBudget = TimeSpan.FromSeconds(2.5);
#endif
        Assert.True(
            timer.Elapsed < totalBudget,
            $"{measuredFrames} stress frames took {timer.Elapsed.TotalSeconds:0.000}s; " +
            $"the {totalBudget.TotalSeconds:0.00}s budget remains faster than about 5s realtime.");
        Assert.True(
            p99 < TimeSpan.FromSeconds(1.0 / 60.0),
            $"99th-percentile core frame time was {p99.TotalMilliseconds:0.000}ms.");
        Assert.True(allocatedBytes <= 256, $"The frame loop allocated {allocatedBytes} bytes.");
        Assert.Equal(0, machine.DroppedAudioSampleCount);
    }

    private sealed class RenderingStressNesImage : IDisposable
    {
        private RenderingStressNesImage(string directoryPath, string path)
        {
            DirectoryPath = directoryPath;
            Path = path;
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public static RenderingStressNesImage Create()
        {
            var directoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PixelDeck.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var path = System.IO.Path.Combine(directoryPath, "rendering-stress.nes");
            var image = new byte[16 + 16_384 + 8_192];
            image[0] = (byte)'N';
            image[1] = (byte)'E';
            image[2] = (byte)'S';
            image[3] = 0x1A;
            image[4] = 1;
            image[5] = 1;

            var program = new byte[]
            {
                0x78,                   // SEI
                0xA9, 0x18,             // LDA #$18
                0x8D, 0x01, 0x20,       // STA $2001
                0x4C, 0x06, 0x80        // JMP $8006
            };
            program.CopyTo(image, 16);

            var vectorOffset = 16 + 0x3FFA;
            WriteVector(image, vectorOffset, 0x8006);
            WriteVector(image, vectorOffset + 2, 0x8000);
            WriteVector(image, vectorOffset + 4, 0x8006);

            var chrOffset = 16 + 16_384;
            for (var row = 0; row < 8; row++)
            {
                image[chrOffset + row] = 0xFF;
                image[chrOffset + 8 + row] = 0xFF;
            }

            File.WriteAllBytes(path, image);
            return new RenderingStressNesImage(directoryPath, path);
        }

        public void Dispose()
        {
            var testRoot = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PixelDeck.Tests"))
                .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var resolvedDirectory = System.IO.Path.GetFullPath(DirectoryPath);
            if (!resolvedDirectory.StartsWith(testRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to remove a directory outside the PixelDeck test area.");
            }

            if (Directory.Exists(resolvedDirectory))
            {
                Directory.Delete(resolvedDirectory, recursive: true);
            }
        }

        private static void WriteVector(byte[] image, int offset, ushort address)
        {
            image[offset] = (byte)address;
            image[offset + 1] = (byte)(address >> 8);
        }
    }
}
