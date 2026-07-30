namespace PixelDeck.Emulation.N64;

/// <summary>
/// Opt-in live bridge which mirrors a Fast3D task through the native RDP.
/// The software renderer always executes first, so any incomplete lowering or
/// native failure retains a valid frame and permanently returns to the proven
/// backend for the rest of the session.
/// </summary>
internal sealed class ParallelRdpGraphicsBackend : IN64GraphicsBackend, IDisposable
{
    internal const string BackendEnvironmentVariable =
        "PIXELDECK_N64_VIDEO_BACKEND";
    internal const string BackendEnvironmentValue = "parallel-rdp";

    private readonly N64Memory _memory;
    private readonly Fast3dRenderer _software;
    private readonly byte[] _initialRdram = new byte[N64Memory.RdramSize];
    private readonly byte[] _nativeRdram = new byte[N64Memory.RdramSize];
    private ParallelRdpContext? _context;
    private bool _disposed;

    private ParallelRdpGraphicsBackend(
        N64Memory memory,
        Fast3dRenderer software,
        ParallelRdpContext context)
    {
        _memory = memory;
        _software = software;
        _context = context;
    }

    public string Name => IsNativeActive
        ? "Pixel64 paraLLEl-RDP HLE bridge"
        : _software.Name;

    public bool RasterizationEnabled
    {
        get => _software.RasterizationEnabled;
        set => _software.RasterizationEnabled = value;
    }

    internal bool IsNativeActive => _context is not null;

    internal long NativeTasksRendered { get; private set; }

    internal string? FallbackReason { get; private set; }

    internal static bool IsRequested =>
        string.Equals(
            Environment.GetEnvironmentVariable(BackendEnvironmentVariable),
            BackendEnvironmentValue,
            StringComparison.OrdinalIgnoreCase);

    internal static bool TryCreate(
        N64Memory memory,
        Fast3dRenderer software,
        out ParallelRdpGraphicsBackend? backend,
        out string summary)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(software);
        backend = null;
        if (!IsRequested)
        {
            summary =
                $"Set {BackendEnvironmentVariable}={BackendEnvironmentValue} " +
                "to opt into the native Pixel64 renderer.";
            return false;
        }

        if (!ParallelRdpNative.TryCreate(out var context, out summary) ||
            context is null)
        {
            return false;
        }

        backend = new ParallelRdpGraphicsBackend(memory, software, context);
        summary =
            $"Pixel64 live rendering is using {context.UpstreamRevision}.";
        return true;
    }

    public void Execute(N64RspTask task)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var context = _context;
        if (context is null || !RasterizationEnabled)
        {
            _software.Execute(task);
            return;
        }

        _memory.Rdram.CopyTo(_initialRdram, 0);
        _software.BeginRdpTraceCapture();
        N64RdpCommandBatch batch;
        try
        {
            _software.Execute(task);
        }
        finally
        {
            batch = _software.EndRdpCommandBatchCapture();
        }

        if (!batch.IsComplete)
        {
            DisableNative(
                $"The task left {batch.OmittedHlePrimitiveCommands:N0} HLE " +
                $"primitive(s) and {batch.UnsupportedSourceCommands:N0} source " +
                "command(s) unlowered.");
            return;
        }

        try
        {
            context.UploadRdram(_initialRdram);
            context.BeginFrame();
            foreach (var command in batch.Commands)
            {
                context.Enqueue(command);
            }

            context.DownloadRdram(_nativeRdram);
            _nativeRdram.CopyTo(_memory.Rdram, 0);
            NativeTasksRendered++;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or InvalidDataException)
        {
            DisableNative(
                $"The native renderer failed safely: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _context, null)?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void DisableNative(string reason)
    {
        FallbackReason = reason;
        Interlocked.Exchange(ref _context, null)?.Dispose();
    }
}
