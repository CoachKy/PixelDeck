using PixelDeck.Emulation.N64;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

var inputPath = Path.GetFullPath(args[0]);
var repeats = 2;
string? exportRdpPath = null;
for (var index = 1; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--repeat" when index + 1 < args.Length &&
                             int.TryParse(args[++index], out var parsedRepeats) &&
                             parsedRepeats is >= 1 and <= 100:
            repeats = parsedRepeats;
            break;
        case "--export-rdp" when index + 1 < args.Length:
            exportRdpPath = Path.GetFullPath(args[++index]);
            break;
        default:
            Console.Error.WriteLine($"Unknown or invalid option: {args[index]}");
            PrintUsage();
            return 1;
    }
}

try
{
    if (Path.GetExtension(inputPath).Equals(".p64rdp", StringComparison.OrdinalIgnoreCase))
    {
        if (exportRdpPath is not null)
        {
            Console.Error.WriteLine("--export-rdp requires a .p64gfx input.");
            return 1;
        }

        return ReplayRdpTrace(inputPath, repeats);
    }

    var capture = N64GraphicsTaskCapture.Load(inputPath);
    if (exportRdpPath is not null)
    {
        var trace = N64RdpTrace.Capture(capture);
        trace.Save(exportRdpPath);
        Console.WriteLine($"RDP trace: {exportRdpPath}");
        Console.WriteLine(
            $"RDP packets: {trace.Commands.Count:N0}; " +
            $"complete: {trace.IsComplete}; " +
            $"omitted HLE primitives: {trace.OmittedHlePrimitiveCommands:N0}; " +
            $"unsupported source commands: {trace.UnsupportedSourceCommands:N0}");
        Console.WriteLine(
            "RDP opcodes: " +
            string.Join(
                ", ",
                trace.Commands
                    .GroupBy(command => command.Opcode)
                    .OrderBy(group => group.Key)
                    .Select(group => $"0x{group.Key:X2}={group.Count():N0}")));
        Console.WriteLine($"RDP trace SHA-256: {trace.TraceSha256}");
    }

    return ReplayGraphicsCapture(inputPath, capture, repeats);
}
catch (Exception exception) when (
    exception is IOException or
    UnauthorizedAccessException or
    InvalidDataException or
    InvalidOperationException)
{
    Console.Error.WriteLine($"Graphics replay failed: {exception.Message}");
    return 1;
}

static int ReplayGraphicsCapture(
    string capturePath,
    N64GraphicsTaskCapture capture,
    int repeats)
{
    N64GraphicsReplayResult? first = null;
    var deterministic = true;
    for (var iteration = 0; iteration < repeats; iteration++)
    {
        var result = N64GraphicsReplay.Replay(capture);
        first ??= result;
        deterministic &= result.RdramSha256 == first.RdramSha256;
    }

    var rdp = first!.RdpState;
    Console.WriteLine($"Capture: {capturePath}");
    Console.WriteLine($"Input RDRAM SHA-256: {capture.RdramSha256}");
    Console.WriteLine($"Backend: {first.BackendName}");
    Console.WriteLine($"Commands: {first.CommandsProcessed:N0}");
    Console.WriteLine($"Unsupported commands: {first.UnsupportedCommands:N0}");
    Console.WriteLine(
        $"Color image: 0x{first.ColorImageAddress:X8}, " +
        $"width {first.ColorImageWidth}, size {first.ColorImageSize}");
    if (rdp is not null)
    {
        Console.WriteLine(
            $"RDP: other-high 0x{rdp.Value.OtherModeHigh:X8}, " +
            $"other-low 0x{rdp.Value.OtherModeLow:X8}, cycle {rdp.Value.CycleType}");
        Console.WriteLine(
            $"RDP pixels: alpha rejected {rdp.Value.AlphaPixelsRejected:N0}, " +
            $"framebuffer blended {rdp.Value.FramebufferPixelsBlended:N0}");
    }

    Console.WriteLine($"Output RDRAM SHA-256: {first.RdramSha256}");
    Console.WriteLine($"Deterministic across {repeats} replay(s): {deterministic}");
    return deterministic ? 0 : 2;
}

static int ReplayRdpTrace(string tracePath, int repeats)
{
    var trace = N64RdpTrace.Load(tracePath);
    N64RdpReplayResult? first = null;
    var deterministic = true;
    for (var iteration = 0; iteration < repeats; iteration++)
    {
        var result = N64RdpReplay.Replay(trace);
        first ??= result;
        deterministic &= result.RdramSha256 == first.RdramSha256;
    }

    Console.WriteLine($"RDP trace: {tracePath}");
    Console.WriteLine($"Trace SHA-256: {trace.TraceSha256}");
    Console.WriteLine($"Input RDRAM SHA-256: {trace.RdramSha256}");
    Console.WriteLine($"Microcode: {trace.Microcode}");
    Console.WriteLine($"RDP packets: {trace.Commands.Count:N0}");
    Console.WriteLine(
        $"Complete: {trace.IsComplete}; " +
        $"omitted HLE primitives: {trace.OmittedHlePrimitiveCommands:N0}; " +
        $"unsupported source commands: {trace.UnsupportedSourceCommands:N0}");
    Console.WriteLine($"Backend: {first!.BackendName}");
    Console.WriteLine($"Unsupported RDP packets: {first.UnsupportedCommands:N0}");
    Console.WriteLine(
        $"Color image: 0x{first.ColorImageAddress:X8}, " +
        $"width {first.ColorImageWidth}, size {first.ColorImageSize}");
    Console.WriteLine($"Output RDRAM SHA-256: {first.RdramSha256}");
    Console.WriteLine($"Deterministic across {repeats} replay(s): {deterministic}");
    return deterministic ? 0 : 2;
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        Pixel64 graphics and RDP replay

          dotnet run --project tools/PixelDeck.N64GraphicsReplay -- <capture.p64gfx>
              [--repeat <1-100>] [--export-rdp <trace.p64rdp>]

          dotnet run --project tools/PixelDeck.N64GraphicsReplay -- <trace.p64rdp>
              [--repeat <1-100>]
        """);
}
