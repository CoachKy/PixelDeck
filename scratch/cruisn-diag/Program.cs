using CruisnDiag;
using PixelDeck.Emulation.N64;

// Drives a cartridge headlessly while mashing Start, dumping a PNG every N
// fields, so a menu screen can be reached and inspected without a display.

var rom = Value("--rom") ?? Path.Combine("Games", "Nintendo64", "Cruis'n USA (USA) (Rev B).n64");
var fields = int.TryParse(Value("--fields"), out var f) ? f : 1800;
var every = int.TryParse(Value("--every"), out var e) ? e : 60;
var mashUntil = int.TryParse(Value("--mash-until"), out var m) ? m : int.MaxValue;
var captureFrom = int.TryParse(Value("--capture-from"), out var c) ? c : int.MaxValue;
var output = Value("--out") ?? Path.Combine("scratch", "cruisn-diag", "shots");

if (Value("--bridge") is not null)
{
    var bridgeMode = Value("--bridge") switch
    {
        "exclusive" => N64RdpBridgeMode.Exclusive,
        "off" => N64RdpBridgeMode.Off,
        _ => N64RdpBridgeMode.Mirror
    };
    Directory.CreateDirectory(output);
    Bridge.Run(rom, fields, bridgeMode, output);
    return 0;
}

if (Value("--bench") is not null)
{
    var benchMachine = N64Machine.Load(rom);
    for (var warm = 0; warm < 120; warm++)
    {
        benchMachine.RunFrame();
    }

    var idle0 = benchMachine.Cpu.IdleInstructionsSkipped;
    var instr0 = benchMachine.Cpu.InstructionsExecuted;
    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    for (var field = 0; field < fields; field++)
    {
        benchMachine.RunFrame();
    }

    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
    var idle = benchMachine.Cpu.IdleInstructionsSkipped - idle0;
    var executed = benchMachine.Cpu.InstructionsExecuted - instr0;
    Console.WriteLine(
        $"{elapsed.TotalMilliseconds / fields,7:F2} ms/field   " +
        $"idle-skip={(executed == 0 ? 0 : 100.0 * idle / executed),5:F1}%   " +
        $"instr/field={executed / fields,9:N0}");
    return 0;
}

Directory.CreateDirectory(output);
var machine = N64Machine.Load(rom);

for (var field = 0; field < fields; field++)
{
    // Two fields down, six up: long enough to register, short enough that
    // menus advance rather than auto-repeating past what we want to see.
    var phase = field % 8;
    machine.SetControllerState(
        1,
        new N64ControllerState(
            field < mashUntil && phase < 2 ? N64Button.Start : N64Button.None,
            0,
            0));

    if (field == captureFrom)
    {
        machine.Renderer.TextureCaptureEnabled = true;
    }

    machine.RunFrame();

    if ((field + 1) % every == 0)
    {
        Png.WriteArgb(
            Path.Combine(output, $"field{field + 1:D5}.png"),
            machine.CurrentFrame,
            machine.Width,
            machine.Height);
        Console.WriteLine($"field {field + 1,5}  {machine.Width}x{machine.Height}");
    }
}

if (captureFrom != int.MaxValue)
{
    var textureDirectory = Path.Combine(output, "textures");
    Directory.CreateDirectory(textureDirectory);
    var index = 0;
    foreach (var texture in machine.Renderer.CapturedTextures.Take(200))
    {
        var name =
            $"{index:D3}-{texture.FormatName.Replace(' ', '_')}-{texture.Width}x{texture.Height}" +
            $"-tmem{texture.BaseBitOffset / 64:D3}-n{texture.SampleCount}";
        Png.WriteRgba(
            Path.Combine(textureDirectory, name + ".png"),
            texture.Rgba,
            texture.Width,
            texture.Height);
        index++;
    }

    Console.WriteLine($"{machine.Renderer.CapturedTextures.Count} textures, {index} written");
}

Console.WriteLine("done");
return 0;

string? Value(string flag)
{
    var index = Array.IndexOf(args, flag);
    if (index < 0)
    {
        return null;
    }

    return index + 1 < args.Length ? args[index + 1] : string.Empty;
}
