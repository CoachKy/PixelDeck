using PixelDeck.App.Services;

namespace PixelDeck.App.Tests;

public class EmulatorDiagnosticsTests
{
    /// <summary>
    /// The writer moved off the calling thread so logging cannot stall
    /// emulation, which means a broken queue or a writer that never starts
    /// silently loses every line — including the ones from the other cores.
    /// This asserts the whole path, from Write to bytes on disk.
    /// </summary>
    [Fact]
    public void WrittenLinesReachTheLogFile()
    {
        var marker = $"diagnostics-selftest-{Guid.NewGuid():N}";

        EmulatorDiagnostics.Write(marker);
        EmulatorDiagnostics.Flush();

        Assert.True(
            File.Exists(EmulatorDiagnostics.LogPath),
            $"No diagnostics log was created at {EmulatorDiagnostics.LogPath}.");

        // The file is shared with a running emulator, so it has to be opened
        // the same permissive way the writer opens it.
        using var stream = new FileStream(
            EmulatorDiagnostics.LogPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        Assert.Contains(marker, reader.ReadToEnd(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The log trims itself once it outgrows its budget. Trimming used to run
    /// while the writer still held the file open, which threw, killed the
    /// writer thread, and silently stopped logging for the rest of the session
    /// — the log froze a few hundred bytes past the limit and never moved.
    /// </summary>
    [Fact]
    public void LoggingSurvivesTheLogFillingUp()
    {
        // Comfortably past the 256 KB budget so the trim path has to run.
        var padding = new string('x', 512);
        for (var index = 0; index < 700; index++)
        {
            EmulatorDiagnostics.Write($"fill-{index}-{padding}");
        }

        var marker = $"post-trim-{Guid.NewGuid():N}";
        EmulatorDiagnostics.Write(marker);
        EmulatorDiagnostics.Flush();

        using var stream = new FileStream(
            EmulatorDiagnostics.LogPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        Assert.Contains(marker, reader.ReadToEnd(), StringComparison.Ordinal);
    }
}
