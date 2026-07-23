namespace PixelDeck.App.Services;

internal static class CrashSafeFile
{
    public static bool Exists(string path) =>
        File.Exists(path) || File.Exists(GetTemporaryPath(path));

    public static IReadOnlyList<string> GetReadCandidates(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var candidates = new List<string>(2);
        if (File.Exists(path))
        {
            candidates.Add(path);
        }

        var temporaryPath = GetTemporaryPath(path);
        if (File.Exists(temporaryPath))
        {
            candidates.Add(temporaryPath);
        }

        return candidates;
    }

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = GetTemporaryPath(path);
        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4_096,
                   FileOptions.WriteThrough))
        {
            stream.Write(data);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    public static void CommitTemporary(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.Move(GetTemporaryPath(path), path, overwrite: true);
    }

    public static string GetTemporaryPath(string path) => path + ".tmp";
}
