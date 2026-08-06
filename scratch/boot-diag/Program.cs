using PixelDeck.Emulation.N64;

// Finds where a cartridge that submits no graphics task is actually spending
// its time. Samples the program counter between short instruction slices, then
// disassembles enough around the winner to recognise the loop.

if (args.Length > 0 && args[0] == "--flash")
{
    return BootDiag.FlashCheck.Run();
}

var rom = args.Length > 0 ? args[0] : null;
if (rom is null || !File.Exists(rom))
{
    Console.Error.WriteLine("usage: boot-diag <rom> [fields] [countPerOp]");
    return 2;
}

var fields = args.Length > 1 && int.TryParse(args[1], out var f) ? f : 300;
var countPerOp = args.Length > 2 && int.TryParse(args[2], out var c) ? c : 0;

var machine = N64Machine.Load(rom);
if (countPerOp > 0)
{
    machine.Cpu.CountPerOp = countPerOp;
}

Console.WriteLine($"{machine.Cartridge.Title}  code={machine.Cartridge.GameCode} " +
                  $"cic={machine.Cartridge.Cic} countPerOp={machine.Cpu.CountPerOp} " +
                  $"entry=0x{machine.Cartridge.EffectiveEntryPoint:X8}");

var histogram = new Dictionary<uint, long>();
long samples = 0;
for (var field = 0; field < fields; field++)
{
    for (var slice = 0; slice < 60; slice++)
    {
        machine.RunInstructions(2000);
        histogram[machine.Cpu.ProgramCounter] = histogram.GetValueOrDefault(machine.Cpu.ProgramCounter) + 1;
        samples++;
    }

    machine.RunFrame();
}

Console.WriteLine(
    $"  microcode={machine.Renderer.DetectedMicrocodeName} " +
    $"crc=0x{machine.Renderer.MicrocodeCrc32:X8} " +
    "");
Console.WriteLine(
    $"  entryReached={machine.ReachedCartridgeEntryPoint} " +
    $"gfxTasks={machine.GraphicsTasksSubmitted} audioTasks={machine.AudioTasksSubmitted} " +
    $"tris={machine.Renderer.TrianglesDrawn:N0} " +
    $"instr={machine.Cpu.InstructionsExecuted:N0} " +
    $"unsupportedInstr={machine.Cpu.UnsupportedInstructionCount}");

Console.WriteLine("  hottest program counters:");
foreach (var entry in histogram.OrderByDescending(e => e.Value).Take(6))
{
    Console.WriteLine($"    0x{entry.Key:X8}  {100.0 * entry.Value / samples,5:F1}%");
}

var hottest = histogram.OrderByDescending(e => e.Value).First().Key;
Console.WriteLine($"  around 0x{hottest:X8}:");
for (var offset = -0x18; offset <= 0x18; offset += 4)
{
    var address = (uint)(hottest + offset);
    Console.WriteLine(
        $"    0x{address:X8}{(offset == 0 ? " <=" : "   ")} {machine.Memory.ReadUInt32(address):X8}");
}

// Both halt loops load a pointer into $a0 before calling their handler, which
// is how these games report an assertion. Dumping the argument registers as
// text usually names the failure outright.
// Re-run and stop the instant the halt address is first reached. Sampling
// later only ever shows the loop; the registers on arrival still hold the
// caller in $ra and whatever the handler was told in $a0-$a3.
if (args.Length > 3 && uint.TryParse(args[3], System.Globalization.NumberStyles.HexNumber, null, out var haltAddress))
{
    var fresh = N64Machine.Load(rom);
    if (countPerOp > 0)
    {
        fresh.Cpu.CountPerOp = countPerOp;
    }

    var reached = false;
    for (var step = 0; step < 400_000_000 && !reached; step++)
    {
        fresh.RunInstructions(1);
        if (fresh.Cpu.ProgramCounter == haltAddress)
        {
            reached = true;
        }
    }

    Console.WriteLine($"  first arrival at 0x{haltAddress:X8}: {reached}");
    if (reached)
    {
        string[] names =
        [
            "zero", "at", "v0", "v1", "a0", "a1", "a2", "a3",
            "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7",
            "s0", "s1", "s2", "s3", "s4", "s5", "s6", "s7",
            "t8", "t9", "k0", "k1", "gp", "sp", "fp", "ra"
        ];
        for (var register = 1; register < 32; register++)
        {
            var value = (uint)fresh.Cpu.Registers[register];
            var text = ReadText(fresh, value);
            Console.WriteLine(
                $"    ${names[register],-4} = 0x{value:X8}" +
                (text.Length >= 3 ? $"  \"{text}\"" : string.Empty));
        }
    }
}

Console.WriteLine("  register text:");
for (var register = 0; register < 32; register++)
{
    var value = (uint)machine.Cpu.Registers[register];
    if (value is < 0x80000000 or >= 0x80800000)
    {
        continue;
    }

    var text = ReadText(machine, value);
    if (text.Length >= 4)
    {
        Console.WriteLine($"    ${register,-2} = 0x{value:X8}  \"{text}\"");
    }
}

BootDiag.Shot.Write("scratch/boot-diag/frame.png", machine.CurrentFrame, machine.Width, machine.Height);
Console.WriteLine("  frame written");
return 0;

static string ReadText(N64Machine machine, uint address)
{
    var builder = new System.Text.StringBuilder();
    for (var offset = 0u; offset < 96; offset++)
    {
        var value = machine.Memory.ReadByte(address + offset);
        if (value == 0)
        {
            break;
        }

        if (value is < 0x20 or > 0x7E)
        {
            return string.Empty;
        }

        builder.Append((char)value);
    }

    return builder.ToString();
}

