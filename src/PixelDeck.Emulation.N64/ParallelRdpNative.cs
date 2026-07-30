using System.Runtime.InteropServices;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// Dynamically loads PixelDeck's optional C++ paraLLEl-RDP bridge. Nothing in
/// the managed core has a static native dependency: a missing/incompatible
/// library or Vulkan device produces a normal software-renderer fallback.
/// </summary>
public static class ParallelRdpNative
{
    public const uint RequiredAbiVersion = 2;
    public const string LibraryPathEnvironmentVariable =
        "PIXELDECK_PARALLEL_RDP_LIBRARY";

    public static bool TryLoadBridge(out string summary)
    {
        if (!ParallelRdpNativeApi.TryLoad(out var api, out summary))
        {
            return false;
        }

        using (api)
        {
            summary =
                $"The paraLLEl-RDP bridge ABI is compatible ({api.UpstreamRevision}).";
            return true;
        }
    }

    public static bool TryCreate(
        out ParallelRdpContext? context,
        out string summary)
    {
        context = null;
        if (!ParallelRdpNativeApi.TryLoad(out var api, out summary))
        {
            return false;
        }

        var status = api.Create(out var nativeContext);
        if (status != 0 || nativeContext == IntPtr.Zero)
        {
            summary =
                $"The paraLLEl-RDP bridge loaded but could not start: {api.GetError(status)}";
            api.Dispose();
            return false;
        }

        context = new ParallelRdpContext(api, nativeContext);
        summary =
            $"paraLLEl-RDP is ready ({api.UpstreamRevision}); ABI {RequiredAbiVersion}.";
        return true;
    }
}

/// <summary>
/// Owns one native Vulkan context, its aligned RDRAM mirror, and its
/// paraLLEl-RDP command processor.
/// </summary>
public sealed class ParallelRdpContext : IDisposable
{
    private const int BytesPerPixel = 4;
    private const int MaximumScanoutDimension = 4096;
    public const int HiddenRdramSize = N64Memory.RdramSize / 2;
    private ParallelRdpNativeApi? _api;
    private IntPtr _context;

    internal ParallelRdpContext(ParallelRdpNativeApi api, IntPtr context)
    {
        _api = api;
        _context = context;
    }

    ~ParallelRdpContext()
    {
        Dispose();
    }

    public string UpstreamRevision =>
        GetApi().UpstreamRevision;

    public void UploadRdram(byte[] canonicalRdram)
    {
        ValidateRdram(canonicalRdram);
        InvokePinned(
            canonicalRdram,
            pointer => GetApi().UploadRdram(
                GetContext(),
                pointer,
                (nuint)canonicalRdram.Length));
    }

    public void DownloadRdram(byte[] canonicalRdram)
    {
        ValidateRdram(canonicalRdram);
        InvokePinned(
            canonicalRdram,
            pointer => GetApi().DownloadRdram(
                GetContext(),
                pointer,
                (nuint)canonicalRdram.Length));
    }

    public void UploadHiddenRdram(byte[] hiddenRdram)
    {
        ValidateHiddenRdram(hiddenRdram);
        InvokePinned(
            hiddenRdram,
            pointer => GetApi().UploadHiddenRdram(
                GetContext(),
                pointer,
                (nuint)hiddenRdram.Length));
    }

    public void DownloadHiddenRdram(byte[] hiddenRdram)
    {
        ValidateHiddenRdram(hiddenRdram);
        InvokePinned(
            hiddenRdram,
            pointer => GetApi().DownloadHiddenRdram(
                GetContext(),
                pointer,
                (nuint)hiddenRdram.Length));
    }

    public void BeginFrame() =>
        ThrowIfFailed(GetApi().BeginFrame(GetContext()));

    public void Enqueue(N64RdpCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var words = command.CopyWords();
        InvokePinned(
            words,
            pointer => GetApi().EnqueueCommand(
                GetContext(),
                pointer,
                checked((uint)words.Length)));
    }

    public void SetViRegister(ParallelRdpViRegister register, uint value)
    {
        if (register is < ParallelRdpViRegister.Control or
            >= ParallelRdpViRegister.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(register));
        }

        ThrowIfFailed(
            GetApi().SetViRegister(
                GetContext(),
                checked((uint)register),
                value));
    }

    public void ApplyViState(ParallelRdpViState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        foreach (var entry in state.EnumerateRegisters())
        {
            SetViRegister(entry.Register, entry.Value);
        }
    }

    public ParallelRdpFrame Scanout()
    {
        var api = GetApi();
        ThrowIfFailed(
            api.Scanout(GetContext(), out var width, out var height));
        if (width > MaximumScanoutDimension ||
            height > MaximumScanoutDimension)
        {
            throw new InvalidDataException(
                $"paraLLEl-RDP returned an invalid {width}x{height} scanout.");
        }

        var expectedLength = checked((int)(width * height * BytesPerPixel));
        if (expectedLength == 0)
        {
            return new ParallelRdpFrame(
                checked((int)width),
                checked((int)height),
                []);
        }

        var rgba = new byte[expectedLength];
        nuint bytesWritten = 0;
        InvokePinned(
            rgba,
            pointer => api.CopyScanout(
                GetContext(),
                pointer,
                (nuint)rgba.Length,
                out bytesWritten));
        if (bytesWritten != (nuint)rgba.Length)
        {
            throw new InvalidDataException(
                $"paraLLEl-RDP returned {bytesWritten} RGBA bytes for a " +
                $"{width}x{height} scanout.");
        }

        return new ParallelRdpFrame(
            checked((int)width),
            checked((int)height),
            rgba);
    }

    public void Dispose()
    {
        var api = Interlocked.Exchange(ref _api, null);
        var context = Interlocked.Exchange(ref _context, IntPtr.Zero);
        if (api is null)
        {
            return;
        }

        if (context != IntPtr.Zero)
        {
            api.Destroy(context);
        }

        api.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void ValidateRdram(byte[] canonicalRdram)
    {
        ArgumentNullException.ThrowIfNull(canonicalRdram);
        if (canonicalRdram.Length != N64Memory.RdramSize)
        {
            throw new ArgumentException(
                $"paraLLEl-RDP requires exactly {N64Memory.RdramSize} RDRAM bytes.",
                nameof(canonicalRdram));
        }
    }

    private static void ValidateHiddenRdram(byte[] hiddenRdram)
    {
        ArgumentNullException.ThrowIfNull(hiddenRdram);
        if (hiddenRdram.Length != HiddenRdramSize)
        {
            throw new ArgumentException(
                $"paraLLEl-RDP requires exactly {HiddenRdramSize} hidden-RDRAM bytes.",
                nameof(hiddenRdram));
        }
    }

    private void InvokePinned(byte[] data, Func<IntPtr, int> operation)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            ThrowIfFailed(operation(handle.AddrOfPinnedObject()));
        }
        finally
        {
            handle.Free();
        }
    }

    private void InvokePinned(uint[] data, Func<IntPtr, int> operation)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            ThrowIfFailed(operation(handle.AddrOfPinnedObject()));
        }
        finally
        {
            handle.Free();
        }
    }

    private void ThrowIfFailed(int status)
    {
        if (status != 0)
        {
            throw new InvalidOperationException(GetApi().GetError(status));
        }
    }

    private ParallelRdpNativeApi GetApi() =>
        _api ?? throw new ObjectDisposedException(nameof(ParallelRdpContext));

    private IntPtr GetContext()
    {
        var context = _context;
        return context != IntPtr.Zero
            ? context
            : throw new ObjectDisposedException(nameof(ParallelRdpContext));
    }
}

/// <summary>
/// A complete set of N64 VI registers consumed by paraLLEl-RDP's scanout.
/// </summary>
public sealed record ParallelRdpViState(
    uint Control,
    uint Origin,
    uint Width,
    uint Interrupt,
    uint CurrentLine,
    uint Timing,
    uint VerticalSync,
    uint HorizontalSync,
    uint Leap,
    uint HorizontalStart,
    uint VerticalStart,
    uint VerticalBurst,
    uint XScale,
    uint YScale)
{
    public static ParallelRdpViState FromMemory(N64Memory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return new ParallelRdpViState(
            memory.ViControl,
            memory.ViOrigin,
            memory.ViWidth,
            memory.ViVerticalInterrupt,
            memory.ViCurrent,
            memory.ViBurst,
            memory.ViVerticalSync,
            memory.ViHorizontalSync,
            memory.ViHorizontalSyncLeap,
            memory.ViHorizontalVideo,
            memory.ViVerticalVideo,
            memory.ViVerticalBurst,
            memory.ViXScale,
            memory.ViYScale);
    }

    internal IEnumerable<(ParallelRdpViRegister Register, uint Value)>
        EnumerateRegisters()
    {
        yield return (ParallelRdpViRegister.Control, Control);
        yield return (ParallelRdpViRegister.Origin, Origin);
        yield return (ParallelRdpViRegister.Width, Width);
        yield return (ParallelRdpViRegister.Interrupt, Interrupt);
        yield return (ParallelRdpViRegister.CurrentLine, CurrentLine);
        yield return (ParallelRdpViRegister.Timing, Timing);
        yield return (ParallelRdpViRegister.VerticalSync, VerticalSync);
        yield return (ParallelRdpViRegister.HorizontalSync, HorizontalSync);
        yield return (ParallelRdpViRegister.Leap, Leap);
        yield return (ParallelRdpViRegister.HorizontalStart, HorizontalStart);
        yield return (ParallelRdpViRegister.VerticalStart, VerticalStart);
        yield return (ParallelRdpViRegister.VerticalBurst, VerticalBurst);
        yield return (ParallelRdpViRegister.XScale, XScale);
        yield return (ParallelRdpViRegister.YScale, YScale);
    }
}

public enum ParallelRdpViRegister : uint
{
    Control = 0,
    Origin = 1,
    Width = 2,
    Interrupt = 3,
    CurrentLine = 4,
    Timing = 5,
    VerticalSync = 6,
    HorizontalSync = 7,
    Leap = 8,
    HorizontalStart = 9,
    VerticalStart = 10,
    VerticalBurst = 11,
    XScale = 12,
    YScale = 13,
    Count = 14,
}

public sealed class ParallelRdpFrame
{
    private readonly byte[] _rgba;

    internal ParallelRdpFrame(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        _rgba = rgba;
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlyMemory<byte> Rgba => _rgba;
}

/// <summary>
/// Replays complete Pixel64 native-command traces through paraLLEl-RDP. This
/// gives the integration a deterministic validation path before live HLE
/// triangles are lowered to native RDP edge packets.
/// </summary>
public static class ParallelRdpTraceReplay
{
    public static bool TryReplay(
        N64RdpTrace trace,
        ParallelRdpViState? viState,
        out ParallelRdpNativeReplayResult? result,
        out string summary) =>
        TryReplay(
            trace,
            viState,
            initialHiddenRdram: null,
            out result,
            out summary);

    public static bool TryReplay(
        N64RdpTrace trace,
        ParallelRdpViState? viState,
        ReadOnlyMemory<byte>? initialHiddenRdram,
        out ParallelRdpNativeReplayResult? result,
        out string summary)
    {
        ArgumentNullException.ThrowIfNull(trace);
        result = null;
        if (!trace.IsComplete)
        {
            summary =
                "The trace is incomplete because its HLE geometry or source " +
                "commands have not all been lowered to native RDP packets.";
            return false;
        }

        var hiddenRdram = CreateInitialHiddenRdram(initialHiddenRdram);

        if (!ParallelRdpNative.TryCreate(out var context, out summary) ||
            context is null)
        {
            return false;
        }

        using (context)
        {
            try
            {
                var rdram = trace.Rdram.ToArray();
                var outputHiddenRdram =
                    new byte[ParallelRdpContext.HiddenRdramSize];
                context.UploadRdram(rdram);
                context.UploadHiddenRdram(hiddenRdram);
                context.BeginFrame();
                foreach (var command in trace.Commands)
                {
                    context.Enqueue(command);
                }

                ParallelRdpFrame? frame = null;
                if (viState is not null)
                {
                    context.ApplyViState(viState);
                    frame = context.Scanout();
                }

                context.DownloadRdram(rdram);
                context.DownloadHiddenRdram(outputHiddenRdram);
                result = new ParallelRdpNativeReplayResult(
                    context.UpstreamRevision,
                    trace.Rdram.Span,
                    rdram,
                    hiddenRdram,
                    outputHiddenRdram,
                    trace.Commands,
                    frame);
                summary =
                    $"Replayed {trace.Commands.Count} native RDP commands " +
                    $"through {context.UpstreamRevision}.";
                return true;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or InvalidDataException)
            {
                summary = $"paraLLEl-RDP replay failed safely: {exception.Message}";
                return false;
            }
        }
    }

    /// <summary>
    /// Replays ordered graphics tasks through one native context. Each trace
    /// supplies the exact CPU-visible pre-task RDRAM, while hidden RDRAM is
    /// deliberately carried from one task to the next just like the physical
    /// RDP coverage store and the live Pixel64 backend.
    /// </summary>
    public static bool TryReplaySequence(
        IReadOnlyList<N64RdpTrace> traces,
        ReadOnlyMemory<byte>? initialHiddenRdram,
        out IReadOnlyList<ParallelRdpNativeReplayResult> results,
        out string summary)
    {
        ArgumentNullException.ThrowIfNull(traces);
        results = [];
        if (traces.Count == 0)
        {
            throw new ArgumentException(
                "Native sequence replay requires at least one trace.",
                nameof(traces));
        }

        for (var index = 0; index < traces.Count; index++)
        {
            var trace = traces[index] ??
                throw new ArgumentException(
                    "Native sequence replay cannot contain a null trace.",
                    nameof(traces));
            if (!trace.IsComplete)
            {
                summary =
                    $"Trace {index + 1} is incomplete because its HLE work " +
                    "has not all been lowered to native RDP packets.";
                return false;
            }
        }

        var hiddenRdram = CreateInitialHiddenRdram(initialHiddenRdram);
        if (!ParallelRdpNative.TryCreate(out var context, out summary) ||
            context is null)
        {
            return false;
        }

        using (context)
        {
            try
            {
                var replayResults =
                    new List<ParallelRdpNativeReplayResult>(traces.Count);
                context.UploadHiddenRdram(hiddenRdram);
                foreach (var trace in traces)
                {
                    var rdram = trace.Rdram.ToArray();
                    var outputHiddenRdram =
                        new byte[ParallelRdpContext.HiddenRdramSize];
                    context.UploadRdram(rdram);
                    context.BeginFrame();
                    foreach (var command in trace.Commands)
                    {
                        context.Enqueue(command);
                    }

                    context.DownloadRdram(rdram);
                    context.DownloadHiddenRdram(outputHiddenRdram);
                    replayResults.Add(
                        new ParallelRdpNativeReplayResult(
                            context.UpstreamRevision,
                            trace.Rdram.Span,
                            rdram,
                            hiddenRdram,
                            outputHiddenRdram,
                            trace.Commands,
                            frame: null));
                    hiddenRdram = outputHiddenRdram;
                }

                results = replayResults;
                summary =
                    $"Replayed {traces.Count:N0} ordered native RDP task(s) " +
                    $"through {context.UpstreamRevision} with hidden coverage " +
                    "preserved between tasks.";
                return true;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or InvalidDataException)
            {
                summary =
                    $"paraLLEl-RDP sequence replay failed safely: " +
                    exception.Message;
                return false;
            }
        }
    }

    private static byte[] CreateInitialHiddenRdram(
        ReadOnlyMemory<byte>? initialHiddenRdram)
    {
        var hiddenRdram = initialHiddenRdram?.ToArray() ??
            new byte[ParallelRdpContext.HiddenRdramSize];
        if (initialHiddenRdram is null)
        {
            Array.Fill(hiddenRdram, (byte)0x03);
        }
        else if (hiddenRdram.Length != ParallelRdpContext.HiddenRdramSize)
        {
            throw new ArgumentException(
                $"Native replay requires exactly " +
                $"{ParallelRdpContext.HiddenRdramSize} hidden-RDRAM bytes.",
                nameof(initialHiddenRdram));
        }

        return hiddenRdram;
    }
}

public sealed class ParallelRdpNativeReplayResult
{
    private readonly byte[] _rdram;
    private readonly byte[] _hiddenRdram;

    internal ParallelRdpNativeReplayResult(
        string upstreamRevision,
        ReadOnlySpan<byte> initialRdram,
        byte[] rdram,
        ReadOnlySpan<byte> initialHiddenRdram,
        byte[] hiddenRdram,
        IReadOnlyList<N64RdpCommand> commands,
        ParallelRdpFrame? frame)
    {
        UpstreamRevision = upstreamRevision;
        _rdram = rdram;
        _hiddenRdram = hiddenRdram;
        Frame = frame;
        RdramDelta = ParallelRdpBufferDelta.Create(
            "RDRAM",
            0,
            initialRdram,
            rdram);
        HiddenCoverage = ParallelRdpBufferDelta.Create(
            "hidden coverage",
            0,
            initialHiddenRdram,
            hiddenRdram);
        var layout = N64RdpOutputLayoutParser.Analyze(commands);
        Framebuffer = CreateRegionDelta(
            "framebuffer",
            layout.Framebuffer,
            initialRdram,
            rdram);
        DepthBuffer = CreateRegionDelta(
            "depth buffer",
            layout.DepthBuffer,
            initialRdram,
            rdram);
    }

    public string UpstreamRevision { get; }

    public ReadOnlyMemory<byte> Rdram => _rdram;

    public ReadOnlyMemory<byte> HiddenRdram => _hiddenRdram;

    public string RdramSha256 => RdramDelta.OutputSha256;

    public string HiddenRdramSha256 => HiddenCoverage.OutputSha256;

    public ParallelRdpBufferDelta RdramDelta { get; }

    public ParallelRdpBufferDelta HiddenCoverage { get; }

    public ParallelRdpBufferDelta? Framebuffer { get; }

    public ParallelRdpBufferDelta? DepthBuffer { get; }

    public ParallelRdpFrame? Frame { get; }

    private static ParallelRdpBufferDelta? CreateRegionDelta(
        string name,
        N64RdpMemoryRegion? region,
        ReadOnlySpan<byte> input,
        ReadOnlySpan<byte> output)
    {
        if (region is not { } value)
        {
            return null;
        }

        var address = checked((int)value.Address);
        return ParallelRdpBufferDelta.Create(
            name,
            value.Address,
            input.Slice(address, value.Length),
            output.Slice(address, value.Length));
    }
}

internal sealed class ParallelRdpNativeApi : IDisposable
{
    private static readonly string[] RequiredExports =
    [
        "pd_parallel_rdp_get_abi_version",
        "pd_parallel_rdp_get_upstream_revision",
        "pd_parallel_rdp_get_last_error",
        "pd_parallel_rdp_create",
        "pd_parallel_rdp_destroy",
        "pd_parallel_rdp_upload_rdram",
        "pd_parallel_rdp_download_rdram",
        "pd_parallel_rdp_upload_hidden_rdram",
        "pd_parallel_rdp_download_hidden_rdram",
        "pd_parallel_rdp_begin_frame",
        "pd_parallel_rdp_enqueue_command",
        "pd_parallel_rdp_set_vi_register",
        "pd_parallel_rdp_scanout",
        "pd_parallel_rdp_copy_scanout",
    ];

    private IntPtr _library;

    private ParallelRdpNativeApi(IntPtr library)
    {
        _library = library;
        GetAbiVersion = GetDelegate<GetAbiVersionDelegate>(
            library,
            RequiredExports[0]);
        GetUpstreamRevision = GetDelegate<GetStringDelegate>(
            library,
            RequiredExports[1]);
        GetLastError = GetDelegate<GetStringDelegate>(
            library,
            RequiredExports[2]);
        Create = GetDelegate<CreateDelegate>(library, RequiredExports[3]);
        Destroy = GetDelegate<DestroyDelegate>(library, RequiredExports[4]);
        UploadRdram = GetDelegate<RdramDelegate>(library, RequiredExports[5]);
        DownloadRdram = GetDelegate<RdramDelegate>(library, RequiredExports[6]);
        UploadHiddenRdram = GetDelegate<RdramDelegate>(
            library,
            RequiredExports[7]);
        DownloadHiddenRdram = GetDelegate<RdramDelegate>(
            library,
            RequiredExports[8]);
        BeginFrame = GetDelegate<ContextDelegate>(library, RequiredExports[9]);
        EnqueueCommand = GetDelegate<EnqueueDelegate>(
            library,
            RequiredExports[10]);
        SetViRegister = GetDelegate<SetViRegisterDelegate>(
            library,
            RequiredExports[11]);
        Scanout = GetDelegate<ScanoutDelegate>(library, RequiredExports[12]);
        CopyScanout = GetDelegate<CopyScanoutDelegate>(
            library,
            RequiredExports[13]);
    }

    public GetAbiVersionDelegate GetAbiVersion { get; }
    public GetStringDelegate GetUpstreamRevision { get; }
    public GetStringDelegate GetLastError { get; }
    public CreateDelegate Create { get; }
    public DestroyDelegate Destroy { get; }
    public RdramDelegate UploadRdram { get; }
    public RdramDelegate DownloadRdram { get; }
    public RdramDelegate UploadHiddenRdram { get; }
    public RdramDelegate DownloadHiddenRdram { get; }
    public ContextDelegate BeginFrame { get; }
    public EnqueueDelegate EnqueueCommand { get; }
    public SetViRegisterDelegate SetViRegister { get; }
    public ScanoutDelegate Scanout { get; }
    public CopyScanoutDelegate CopyScanout { get; }

    public string UpstreamRevision =>
        ReadUtf8(GetUpstreamRevision()) ?? "unknown paraLLEl-RDP revision";

    public static bool TryLoad(
        out ParallelRdpNativeApi api,
        out string summary)
    {
        api = null!;
        var attempted = new List<string>();
        string? lastFailure = null;
        foreach (var candidate in EnumerateCandidates())
        {
            if (!attempted.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                attempted.Add(candidate);
            }

            IntPtr library;
            try
            {
                if (!NativeLibrary.TryLoad(candidate, out library))
                {
                    continue;
                }
            }
            catch (Exception exception) when (
                exception is BadImageFormatException or
                DllNotFoundException or
                FileLoadException)
            {
                lastFailure = exception.Message;
                continue;
            }

            try
            {
                var loaded = new ParallelRdpNativeApi(library);
                var abiVersion = loaded.GetAbiVersion();
                if (abiVersion != ParallelRdpNative.RequiredAbiVersion)
                {
                    summary =
                        $"The paraLLEl-RDP bridge ABI is {abiVersion}; " +
                        $"Pixel64 requires {ParallelRdpNative.RequiredAbiVersion}.";
                    loaded.Dispose();
                    return false;
                }

                api = loaded;
                summary = $"Loaded the optional paraLLEl-RDP bridge from {candidate}.";
                return true;
            }
            catch (Exception exception) when (
                exception is EntryPointNotFoundException or
                MarshalDirectiveException or
                BadImageFormatException)
            {
                lastFailure = exception.Message;
                NativeLibrary.Free(library);
            }
        }

        summary = lastFailure is null
            ? "The optional paraLLEl-RDP native bridge is not installed; " +
              "Pixel64 will keep using its managed software renderer."
            : "No compatible paraLLEl-RDP native bridge could be loaded; " +
              $"Pixel64 will use its software renderer. {lastFailure}";
        return false;
    }

    public string GetError(int status)
    {
        var detail = ReadUtf8(GetLastError());
        return string.IsNullOrWhiteSpace(detail)
            ? $"The paraLLEl-RDP bridge returned status {status}."
            : detail;
    }

    public void Dispose()
    {
        var library = Interlocked.Exchange(ref _library, IntPtr.Zero);
        if (library != IntPtr.Zero)
        {
            NativeLibrary.Free(library);
        }
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        var explicitPath = Environment.GetEnvironmentVariable(
            ParallelRdpNative.LibraryPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return Path.GetFullPath(explicitPath);
        }

        var fileName = OperatingSystem.IsWindows()
            ? "PixelDeck.ParallelRdp.dll"
            : OperatingSystem.IsLinux()
                ? "libPixelDeck.ParallelRdp.so"
                : "libPixelDeck.ParallelRdp.dylib";
        var runtime = GetRuntimeIdentifier();
        yield return Path.Combine(AppContext.BaseDirectory, fileName);
        yield return Path.Combine(
            AppContext.BaseDirectory,
            "Native",
            runtime,
            fileName);
        yield return Path.Combine(
            AppContext.BaseDirectory,
            "Components",
            fileName);
        yield return fileName;
    }

    private static string GetRuntimeIdentifier()
    {
        var platform = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsLinux()
                ? "linux"
                : "osx";
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };
        return $"{platform}-{architecture}";
    }

    private static T GetDelegate<T>(IntPtr library, string name)
        where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(library, name, out var pointer))
        {
            throw new EntryPointNotFoundException(name);
        }

        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private static string? ReadUtf8(IntPtr value) =>
        value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate uint GetAbiVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr GetStringDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CreateDelegate(out IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void DestroyDelegate(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int RdramDelegate(
        IntPtr context,
        IntPtr rdram,
        nuint size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ContextDelegate(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int EnqueueDelegate(
        IntPtr context,
        IntPtr words,
        uint wordCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SetViRegisterDelegate(
        IntPtr context,
        uint registerIndex,
        uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ScanoutDelegate(
        IntPtr context,
        out uint width,
        out uint height);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CopyScanoutDelegate(
        IntPtr context,
        IntPtr rgba,
        nuint capacity,
        out nuint bytesWritten);
}
