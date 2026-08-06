using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PixelDeck.Emulation.N64;

// Frame-hash baselines for Pixel64.
//
// Records a hash of each cartridge's output at a fixed field count and compares
// later runs against it. It exists so that a change to the renderer can be shown
// to have altered nothing except what was intended -- the safety net a large
// refactor needs, and the thing Pixel64 has never had.
//
// Only hashes are stored, never frame buffers, so no game-derived imagery is
// written to disk or committed.

var command = args.Length > 0 ? args[0] : "check";
var gamesFolder = ArgumentValue("--games") ?? Path.Combine("Games", "Nintendo64");
var baselinePath = ArgumentValue("--baseline") ?? Path.Combine("docs", "pixel64-frame-baselines.json");
var fields = int.TryParse(ArgumentValue("--fields"), out var parsed) ? parsed : 1800;
var filter = ArgumentValue("--filter");

if (command is not ("record" or "check"))
{
    Console.Error.WriteLine("usage: dotnet run -- [record|check] [--games <dir>] [--baseline <file>] [--fields N] [--filter text]");
    return 2;
}

var roms = Directory.Exists(gamesFolder)
    ? Directory.EnumerateFiles(gamesFolder)
        .Where(path => path.EndsWith(".z64", StringComparison.OrdinalIgnoreCase) ||
                       path.EndsWith(".n64", StringComparison.OrdinalIgnoreCase) ||
                       path.EndsWith(".v64", StringComparison.OrdinalIgnoreCase))
        .Where(path => filter is null ||
                       Path.GetFileName(path).Contains(filter, StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToList()
    : [];

if (roms.Count == 0)
{
    Console.Error.WriteLine($"No cartridges found under '{gamesFolder}'.");
    return 2;
}

var baselines = File.Exists(baselinePath)
    ? JsonSerializer.Deserialize<Dictionary<string, BaselineEntry>>(File.ReadAllText(baselinePath))
        ?? new Dictionary<string, BaselineEntry>()
    : new Dictionary<string, BaselineEntry>();

int matched = 0, changed = 0, added = 0, unbaselined = 0, failed = 0;

foreach (var rom in roms)
{
    var name = Path.GetFileNameWithoutExtension(rom);
    string hash;
    int width, height;
    try
    {
        var machine = N64Machine.Load(rom);
        for (var field = 0; field < fields; field++)
        {
            machine.RunFrame();
        }

        width = machine.Width;
        height = machine.Height;
        hash = HashFrame(machine.CurrentFrame, width, height);
    }
    catch (Exception error)
    {
        Console.WriteLine($"  FAILED       {name}: {error.GetType().Name}: {error.Message}");
        failed++;
        continue;
    }

    var entry = new BaselineEntry(fields, width, height, hash);
    if (command == "record")
    {
        if (baselines.TryGetValue(name, out var old) && old.Hash != hash)
        {
            Console.WriteLine($"  updated      {name}  {old.Width}x{old.Height} -> {width}x{height}");
            changed++;
        }
        else if (!baselines.ContainsKey(name))
        {
            Console.WriteLine($"  recorded     {name}  {width}x{height}");
            added++;
        }
        else
        {
            Console.WriteLine($"  unchanged    {name}");
            matched++;
        }

        baselines[name] = entry;
        continue;
    }

    if (!baselines.TryGetValue(name, out var expected))
    {
        Console.WriteLine($"  UNBASELINED  {name}  {width}x{height}");
        unbaselined++;
    }
    else if (expected.Fields != fields)
    {
        Console.WriteLine($"  SKIPPED      {name}: baseline was taken at {expected.Fields} fields, not {fields}");
        unbaselined++;
    }
    else if (expected.Hash == hash)
    {
        Console.WriteLine($"  match        {name}");
        matched++;
    }
    else
    {
        Console.WriteLine(
            $"  CHANGED      {name}  expected {expected.Width}x{expected.Height} {expected.Hash[..12]}, " +
            $"got {width}x{height} {hash[..12]}");
        changed++;
    }
}

if (command == "record")
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(baselinePath))!);
    File.WriteAllText(
        baselinePath,
        JsonSerializer.Serialize(baselines, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\n{added} recorded, {changed} updated, {matched} unchanged -> {baselinePath}");
    return 0;
}

Console.WriteLine($"\n{matched} match, {changed} changed, {unbaselined} unbaselined, {failed} failed");
return changed > 0 || failed > 0 ? 1 : 0;

string? ArgumentValue(string flag)
{
    var index = Array.IndexOf(args, flag);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string HashFrame(ReadOnlySpan<uint> frame, int width, int height)
{
    var bytes = new byte[(frame.Length * 4) + 8];
    BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), width);
    BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), height);
    for (var index = 0; index < frame.Length; index++)
    {
        BitConverter.TryWriteBytes(bytes.AsSpan(8 + (index * 4), 4), frame[index]);
    }

    return Convert.ToHexString(SHA256.HashData(bytes));
}

internal sealed record BaselineEntry(int Fields, int Width, int Height, string Hash);
