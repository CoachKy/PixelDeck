using System.Globalization;
using PixelDeck.Emulation.GameCube;

namespace PixelDeck.CubeTrace;

/// <summary>
/// Runs a GameCube disc through PixelCube from the command line and prints
/// what happened.
/// </summary>
/// <remarks>
/// The point is to make an iteration cheap. Working out which instruction to
/// implement next should not require launching the dashboard, going full
/// screen, finding the game and reading a log afterwards — it should be one
/// command that ends with a ranked list of what the run hit. Pixel64 and
/// PixelSNES both grew a harness like this eventually; PixelCube gets one
/// while the core is still small enough for it to shape the work.
/// </remarks>
internal static class Program
{
    private const long DefaultInstructionBudget = 2_000_000;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(
                """
                PixelCube trace harness

                  cubetrace <disc> [options]

                  --instructions <n>   How many instructions to run (default 2,000,000).
                  --survey             Skip unimplemented instructions instead of stopping,
                                       to survey everything a stretch of code touches.
                                       The resulting state is fiction; use it to plan only.
                  --trace <spec>       Trace level and channels, e.g. "debug:disc,cpu".
                  --disassemble <n>    Print the first n instructions as they execute.
                  --files              List the disc's file table and stop.
                """);
            return args.Length == 0 ? 1 : 0;
        }

        var options = CommandLineOptions.Parse(args);
        if (!File.Exists(options.DiscPath))
        {
            Console.Error.WriteLine($"No such disc image: {options.DiscPath}");
            return 1;
        }

        using var trace = new GameCubeTraceLog(options.TraceSettings);
        trace.AddSink(new GameCubeTraceDelegateSink(
            record => Console.WriteLine(record.Format())));

        using var machine = GameCubeMachine.Load(options.DiscPath, trace);
        machine.TraceStartupReport();

        if (options.ListFiles)
        {
            PrintFileTable(machine);
            return 0;
        }

        if (options.Watch is { } watchAddress)
        {
            machine.Memory.WatchAddress = watchAddress;
            machine.Memory.WatchLength = 4;
        }

        machine.Cpu.UnimplementedPolicy = options.Survey
            ? GekkoUnimplementedPolicy.Survey
            : GekkoUnimplementedPolicy.Stop;

        PrintLeadingInstructions(machine, options.DisassembleCount);

        var started = DateTime.UtcNow;
        var result = machine.Run(options.InstructionBudget);
        var elapsed = DateTime.UtcNow - started;

        PrintResult(machine, result, elapsed);
        PrintInstructionsAround(machine, options.At ?? result.Pc, options.DisassembleCount);

        // The tally is written by the machine as it closes, so the run ends
        // with the work list whether it was asked for or not.
        trace.Flush();

        return result.Outcome == GekkoOutcome.Completed ? 0 : 2;
    }

    /// <summary>
    /// Disassembles ahead of the entry point without executing, so a run that
    /// stops immediately still shows what it was about to do.
    /// </summary>
    private static void PrintLeadingInstructions(GameCubeMachine machine, int count)
    {
        if (count <= 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"--- {count} instructions from the entry point ---");
        var address = machine.Cpu.Pc;
        for (var index = 0; index < count; index++, address += 4)
        {
            if (!machine.Memory.TryReadInstruction(address, out var instruction))
            {
                Console.WriteLine($"  {address:X8}  <unmapped>");
                break;
            }

            Console.WriteLine(
                $"  {address:X8}  {instruction:X8}  " +
                GekkoDisassembler.Describe(instruction, address));
        }
    }

    /// <summary>
    /// Disassembles either side of where a run ended. A run that stops without
    /// hitting anything unimplemented has stopped inside a loop, and the loop
    /// itself is the only thing that says what it is waiting for.
    /// </summary>
    private static void PrintInstructionsAround(GameCubeMachine machine, uint pc, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var start = pc - ((uint)count / 2 * 4);
        Console.WriteLine();
        Console.WriteLine($"--- {count} instructions around 0x{pc:X8} ---");
        for (var index = 0; index < count; index++)
        {
            var address = start + ((uint)index * 4);
            if (!machine.Memory.TryReadInstruction(address, out var instruction))
            {
                continue;
            }

            Console.WriteLine(
                $"  {(address == pc ? ">" : " ")} {address:X8}  {instruction:X8}  " +
                GekkoDisassembler.Describe(instruction, address));
        }
    }

    private static void PrintFileTable(GameCubeMachine machine)
    {
        var fileSystem = machine.Disc.FileSystem;
        Console.WriteLine();
        Console.WriteLine($"--- {fileSystem.Files.Count} files ---");
        foreach (var file in fileSystem.Files)
        {
            Console.WriteLine(
                $"  {file.Path,-56} 0x{file.Offset:X8}  {file.Length,12:N0}");
        }
    }

    private static void PrintResult(
        GameCubeMachine machine,
        GekkoRunResult result,
        TimeSpan elapsed)
    {
        var cpu = machine.Cpu;
        Console.WriteLine();
        Console.WriteLine("--- run ---");
        Console.WriteLine($"outcome       : {result.Outcome}");
        Console.WriteLine($"instructions  : {result.InstructionsExecuted:N0}");
        Console.WriteLine(
            $"rate          : {result.InstructionsExecuted / Math.Max(elapsed.TotalSeconds, 1e-9) / 1e6:F1} M/s");
        Console.WriteLine($"stopped at    : 0x{result.Pc:X8}");
        Console.WriteLine(
            $"instruction   : {result.Instruction:X8}  " +
            GekkoDisassembler.Describe(result.Instruction, result.Pc));
        Console.WriteLine($"lr / ctr      : 0x{cpu.Lr:X8} / 0x{cpu.Ctr:X8}");
        Console.WriteLine($"cr / xer      : 0x{cpu.Cr:X8} / 0x{cpu.Xer:X8}");
        Console.WriteLine($"msr           : 0x{cpu.Msr:X8}");
        Console.WriteLine();
        Console.WriteLine("--- registers ---");
        for (var row = 0; row < 8; row++)
        {
            var line = string.Join(
                "  ",
                Enumerable.Range(0, 4).Select(column =>
                {
                    var register = (row * 4) + column;
                    return $"r{register,-2}={cpu.Gpr[register]:X8}";
                }));
            Console.WriteLine($"  {line}");
        }
    }

    private sealed record CommandLineOptions(
        string DiscPath,
        long InstructionBudget,
        bool Survey,
        bool ListFiles,
        int DisassembleCount,
        uint? At,
        uint? Watch,
        GameCubeTraceSettings TraceSettings)
    {
        public static CommandLineOptions Parse(string[] args)
        {
            var discPath = args[0];
            var budget = DefaultInstructionBudget;
            var survey = false;
            var listFiles = false;
            var disassembleCount = 0;
            uint? at = null;
            uint? watch = null;
            var settings = new GameCubeTraceSettings(
                GameCubeTraceLevel.Information,
                GameCubeTraceChannel.Default);

            for (var index = 1; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--instructions" when index + 1 < args.Length:
                        budget = long.Parse(args[++index], CultureInfo.InvariantCulture);
                        break;
                    case "--at" when index + 1 < args.Length:
                        at = Convert.ToUInt32(args[++index].Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase), 16);
                        break;
                    case "--watch" when index + 1 < args.Length:
                        watch = Convert.ToUInt32(args[++index].Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase), 16);
                        break;
                    case "--survey":
                        survey = true;
                        break;
                    case "--files":
                        listFiles = true;
                        break;
                    case "--disassemble" when index + 1 < args.Length:
                        disassembleCount = int.Parse(args[++index], CultureInfo.InvariantCulture);
                        break;
                    case "--trace" when index + 1 < args.Length:
                        settings = GameCubeTraceSettings.Parse(args[++index]);
                        break;
                    default:
                        throw new ArgumentException($"Unrecognised option: {args[index]}");
                }
            }

            return new CommandLineOptions(
                discPath,
                budget,
                survey,
                listFiles,
                disassembleCount,
                at,
                watch,
                settings);
        }
    }
}
