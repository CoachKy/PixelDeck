using System.Diagnostics;
using PixelDeck.Emulation.Snes;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

/// <summary>
/// The Super Nintendo core previously had no performance coverage, so a game
/// costing several times the per-frame budget produced no failing test. These
/// gates mirror the NES and N64 ones: allocation is asserted everywhere,
/// wall-clock budgets only on developer hardware.
/// </summary>
public sealed class SnesPerformanceTests(ITestOutputHelper output)
{
    private const int WarmupFrames = 120;
    private const int MeasuredFrames = 180;

    /// <summary>
    /// Reports per-frame cost for every locally installed cartridge, slowest
    /// first. This is the diagnostic that identifies which games are
    /// expensive and why; it does not gate.
    /// </summary>
    [Fact]
    public void ReportsPerGameFrameCostForLocalLibrary()
    {
        var requested = Environment.GetEnvironmentVariable("PIXELSNES_PROFILE");
        if (string.IsNullOrWhiteSpace(requested))
        {
            output.WriteLine("Set PIXELSNES_PROFILE=1 (or a filename fragment) to profile the local library.");
            return;
        }

        var games = FindCartridges()
            .Where(path => requested == "1" ||
                           Path.GetFileName(path).Contains(requested, StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName)
            .ToArray();
        if (games.Length == 0)
        {
            output.WriteLine("No matching Super Nintendo cartridges are installed.");
            return;
        }

        var results = new List<(string Name, double Fps, double WorstMs, long Allocated)>();
        foreach (var path in games)
        {
            try
            {
                results.Add(Measure(path));
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
            {
                output.WriteLine($"{Path.GetFileNameWithoutExtension(path)}: not supported ({exception.Message})");
            }
        }

        output.WriteLine($"{"game",-46} {"fps",8} {"worst ms",9} {"alloc/frame",12}");
        foreach (var (name, fps, worstMs, allocated) in results.OrderBy(entry => entry.Fps))
        {
            output.WriteLine(
                $"{name,-46} {fps,8:0.0} {worstMs,9:0.00} {allocated,12:N0}");
        }

        if (results.Count > 1)
        {
            var slowest = results.MinBy(entry => entry.Fps);
            var fastest = results.MaxBy(entry => entry.Fps);
            output.WriteLine(
                $"spread: {slowest.Name} is {fastest.Fps / slowest.Fps:0.00}x the cost of {fastest.Name}");
        }
    }

    private static (string Name, double Fps, double WorstMs, long Allocated) Measure(string path)
    {
        var machine = SnesMachine.Load(path);
        var audio = new float[4_096];
        for (var frame = 0; frame < WarmupFrames; frame++)
        {
            machine.RunFrame();
            machine.ReadAudioSamples(audio);
        }

        // Measure the emulation step on its own first: any allocation here is
        // real per-frame garbage rather than test-harness noise.
        var runOnlyBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < MeasuredFrames; frame++)
        {
            machine.RunFrame();
        }

        var runOnly = (GC.GetAllocatedBytesForCurrentThread() - runOnlyBefore) / MeasuredFrames;

        var worst = 0L;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var frame = 0; frame < MeasuredFrames; frame++)
        {
            var frameStarted = Stopwatch.GetTimestamp();
            machine.RunFrame();
            machine.ReadAudioSamples(audio);
            worst = Math.Max(worst, Stopwatch.GetTimestamp() - frameStarted);
        }

        Console.WriteLine($"    RunFrame-only alloc/frame: {runOnly:N0} bytes");

        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        return (
            Path.GetFileNameWithoutExtension(path),
            MeasuredFrames / elapsed.TotalSeconds,
            worst * 1000.0 / Stopwatch.Frequency,
            allocated / MeasuredFrames);
    }

    private static IEnumerable<string> FindCartridges()
    {
        var configured = Environment.GetEnvironmentVariable("PIXELDECK_GAMES_FOLDER");
        var gamesFolder = string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Games"))
            : Path.GetFullPath(configured);
        var folder = Path.Combine(gamesFolder, "SuperNintendo");
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(path =>
                    Path.GetExtension(path).Equals(".sfc", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(path).Equals(".smc", StringComparison.OrdinalIgnoreCase))
            : [];
    }
}
