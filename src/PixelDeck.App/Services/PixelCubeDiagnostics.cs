using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Services;

/// <summary>
/// The dashboard's single PixelCube trace log, and the wire that carries its
/// records into <see cref="EmulatorDiagnostics"/>.
/// </summary>
/// <remarks>
/// PixelCube keeps its own detailed trace file, which is where a channel like
/// <c>cpu</c> or <c>registers</c> belongs — those produce far more than
/// <c>emulator.log</c> should ever hold. What comes across to the dashboard's
/// log is only what a bug report needs: the identity of the disc, the boot
/// image, and anything that failed. Debug and verbose records stay in the
/// trace file.
///
/// The level and channels come from <c>PIXELCUBE_TRACE</c>, so a session can
/// be opened right up without a rebuild.
/// </remarks>
internal static class PixelCubeDiagnostics
{
    private static readonly Lazy<GameCubeTraceLog> LazyLog =
        new(CreateLog, LazyThreadSafetyMode.ExecutionAndPublication);

    public static GameCubeTraceLog Log => LazyLog.Value;

    /// <summary>
    /// Drains PixelCube's trace on shutdown, alongside the dashboard's own.
    /// </summary>
    public static void Flush()
    {
        if (LazyLog.IsValueCreated)
        {
            LazyLog.Value.Flush();
        }
    }

    private static GameCubeTraceLog CreateLog()
    {
        var log = GameCubeTraceLog.CreateFromEnvironment();
        log.AddSink(new GameCubeTraceDelegateSink(ForwardToEmulatorLog));
        EmulatorDiagnostics.Write(
            $"PixelCube trace: level={log.Level} channels={log.Channels} " +
            $"file={log.Settings.FilePath}");
        return log;
    }

    private static void ForwardToEmulatorLog(GameCubeTraceRecord record)
    {
        if (record.Level > GameCubeTraceLevel.Information)
        {
            return;
        }

        EmulatorDiagnostics.Write($"PixelCube {record.Channel}: {record.Message}");
    }
}
