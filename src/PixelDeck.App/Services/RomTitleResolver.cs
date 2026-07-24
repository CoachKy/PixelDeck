using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace PixelDeck.App.Services;

/// <summary>
/// Resolves stable display titles without changing or renaming local game images.
/// Catalog matches take precedence over cartridge metadata, which takes precedence
/// over the filename supplied by the caller.
/// </summary>
internal sealed class RomTitleResolver
{
    private const string NoCatalogRevision = "NO-CATALOG";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _sync = new();
    private readonly string _catalogFolder;
    private readonly string _cachePath;
    private Dictionary<string, CachedTitle> _cache;
    private RomTitleCatalog _catalog = RomTitleCatalog.Empty;
    private string _catalogRevision = NoCatalogRevision;
    private bool _cacheDirty;

    public RomTitleResolver(string gamesFolder)
    {
        var metadataRoot = Path.Combine(gamesFolder, ".pixeldeck");
        _catalogFolder = Path.Combine(metadataRoot, "metadata");
        _cachePath = Path.Combine(metadataRoot, "title-cache.json");
        Directory.CreateDirectory(_catalogFolder);
        _cache = LoadCache();
        RefreshCatalogs();
    }

    public string CatalogFolder => _catalogFolder;

    public void RefreshCatalogs()
    {
        lock (_sync)
        {
            var catalogFiles = EnumerateCatalogFiles();
            var revision = CalculateCatalogRevision(catalogFiles);
            if (string.Equals(revision, _catalogRevision, StringComparison.Ordinal))
            {
                return;
            }

            _catalog = RomTitleCatalog.Load(catalogFiles);
            _catalogRevision = revision;
        }
    }

    public string Resolve(
        FileInfo file,
        string relativePath,
        string extension,
        string fallbackTitle,
        string? cartridgeTitle)
    {
        var cacheKey = NormalizePath(relativePath);
        var modifiedTicks = file.LastWriteTimeUtc.Ticks;

        lock (_sync)
        {
            if (_cache.TryGetValue(cacheKey, out var cached) &&
                cached.Length == file.Length &&
                cached.LastWriteTimeUtcTicks == modifiedTicks &&
                string.Equals(cached.CatalogRevision, _catalogRevision, StringComparison.Ordinal))
            {
                return cached.Title;
            }
        }

        var resolvedTitle = TryResolveFromCatalog(file.FullName, extension)
            ?? NormalizeCartridgeTitle(cartridgeTitle)
            ?? TryReadNesNintendoHeaderTitle(file.FullName, extension, fallbackTitle)
            ?? fallbackTitle;

        lock (_sync)
        {
            _cache[cacheKey] = new CachedTitle(
                file.Length,
                modifiedTicks,
                _catalogRevision,
                resolvedTitle);
            _cacheDirty = true;
        }

        return resolvedTitle;
    }

    public void SaveCache()
    {
        lock (_sync)
        {
            if (!_cacheDirty)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                var temporaryPath = _cachePath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_cache, JsonOptions));
                File.Move(temporaryPath, _cachePath, overwrite: true);
                _cacheDirty = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(exception);
            }
        }
    }

    private string? TryResolveFromCatalog(string filePath, string extension)
    {
        RomTitleCatalog catalog;
        lock (_sync)
        {
            catalog = _catalog;
        }

        return catalog.TryResolve(filePath, extension, out var title) ? title : null;
    }

    private string[] EnumerateCatalogFiles()
    {
        try
        {
            return Directory
                .EnumerateFiles(_catalogFolder, "*", SearchOption.TopDirectoryOnly)
                .Where(path =>
                    Path.GetExtension(path).Equals(".dat", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(exception);
            return [];
        }
    }

    private static string CalculateCatalogRevision(IEnumerable<string> catalogFiles)
    {
        var description = new StringBuilder();
        foreach (var path in catalogFiles)
        {
            try
            {
                var file = new FileInfo(path);
                description
                    .Append(file.Name.ToUpperInvariant())
                    .Append(':')
                    .Append(file.Length)
                    .Append(':')
                    .Append(file.LastWriteTimeUtc.Ticks)
                    .Append(';');
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(exception);
            }
        }

        if (description.Length == 0)
        {
            return NoCatalogRevision;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(description.ToString())));
    }

    private Dictionary<string, CachedTitle> LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return new Dictionary<string, CachedTitle>(StringComparer.OrdinalIgnoreCase);
            }

            var cache = JsonSerializer.Deserialize<Dictionary<string, CachedTitle>>(
                File.ReadAllText(_cachePath),
                JsonOptions);
            return cache is null
                ? new Dictionary<string, CachedTitle>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CachedTitle>(cache, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine(exception);
            return new Dictionary<string, CachedTitle>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? NormalizeCartridgeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var normalized = string.Join(' ', title.Split(
            [' ', '\0'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length == 0 ? null : normalized;
    }

    private static string? TryReadNesNintendoHeaderTitle(
        string filePath,
        string extension,
        string fallbackTitle)
    {
        if (!extension.Equals(".nes", StringComparison.OrdinalIgnoreCase) ||
            !LooksLikeOpaqueFileTitle(fallbackTitle))
        {
            return null;
        }

        try
        {
            Span<byte> header = stackalloc byte[16];
            using var stream = File.OpenRead(filePath);
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("NES\x1A"u8))
            {
                return null;
            }

            var isNes20 = (header[7] & 0x0C) == 0x08;
            var prgLength = GetNesRomSize(
                header[4],
                (byte)(header[9] & 0x0F),
                isNes20,
                16_384);
            var prgOffset = 16 + ((header[6] & 0x04) != 0 ? 512 : 0);
            if (prgLength < 32 || stream.Length < prgOffset + (long)prgLength)
            {
                return null;
            }

            stream.Position = prgOffset + prgLength - 32L;
            Span<byte> nintendoHeader = stackalloc byte[32];
            stream.ReadExactly(nintendoHeader);

            var validationSum = 0;
            for (var index = 0x12; index <= 0x19; index++)
            {
                validationSum = (validationSum + nintendoHeader[index]) & 0xFF;
            }

            var encoding = nintendoHeader[0x16];
            var encodedLength = nintendoHeader[0x17];
            if (validationSum != 0 || encoding is not (1 or 2) || encodedLength is 0 or > 15)
            {
                return null;
            }

            var titleLength = encodedLength + 1;
            var rightJustified = nintendoHeader.Slice(16 - titleLength, titleLength);
            var title = DecodeNintendoTitle(rightJustified);
            if (title is { Length: >= 4 })
            {
                return title;
            }

            // A small number of cartridges use a left-justified variant.
            title = DecodeNintendoTitle(nintendoHeader[..titleLength]);
            return title is { Length: >= 4 } ? title : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
        {
            Debug.WriteLine(exception);
            return null;
        }
    }

    private static string? DecodeNintendoTitle(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value is 0 or 0x20)
            {
                continue;
            }

            // Encoding 2 can contain JIS X 0201 katakana. Without a declared
            // code page, retain only its portable ASCII subset.
            if (value is < 0x21 or > 0x7E)
            {
                return null;
            }
        }

        var title = Encoding.ASCII.GetString(bytes).Replace('\0', ' ').Trim();
        title = string.Join(' ', title.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private static bool LooksLikeOpaqueFileTitle(string title)
    {
        if (title.Contains('~') ||
            title.StartsWith("UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
            title.StartsWith("UNTITLED", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (title.Length is < 2 or > 12 || title.Any(character => !char.IsLetterOrDigit(character)))
        {
            return false;
        }

        var letters = title.Where(char.IsLetter).ToArray();
        return letters.Length == 0 ||
               letters.All(char.IsUpper) ||
               letters.All(char.IsLower);
    }

    private static int GetNesRomSize(
        byte leastSignificant,
        byte mostSignificant,
        bool isNes20,
        int unitSize)
    {
        if (!isNes20)
        {
            return checked(leastSignificant * unitSize);
        }

        if (mostSignificant != 0x0F)
        {
            return checked((leastSignificant | (mostSignificant << 8)) * unitSize);
        }

        var exponent = leastSignificant >> 2;
        if (exponent > 30)
        {
            throw new InvalidDataException("The NES 2.0 ROM size is too large to inspect.");
        }

        return checked((1 << exponent) * (((leastSignificant & 0x03) * 2) + 1));
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').ToUpperInvariant();

    private sealed record CachedTitle(
        long Length,
        long LastWriteTimeUtcTicks,
        string CatalogRevision,
        string Title);
}

internal sealed class RomTitleCatalog
{
    private static readonly uint[] Crc32Table = CreateCrc32Table();

    private readonly Dictionary<string, string> _sha1Titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _crc32Titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ambiguousSha1 = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ambiguousCrc32 = new(StringComparer.OrdinalIgnoreCase);

    public static RomTitleCatalog Empty { get; } = new();

    public static RomTitleCatalog Load(IEnumerable<string> paths)
    {
        var catalog = new RomTitleCatalog();
        foreach (var path in paths)
        {
            try
            {
                catalog.LoadFile(path);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                JsonException or XmlException or InvalidDataException)
            {
                Debug.WriteLine(exception);
            }
        }

        return catalog;
    }

    public bool TryResolve(string filePath, string extension, out string title)
    {
        title = string.Empty;
        if (_sha1Titles.Count == 0 && _crc32Titles.Count == 0)
        {
            return false;
        }

        var image = File.ReadAllBytes(filePath);
        if (TryResolveBytes(image, out title))
        {
            return true;
        }

        if (extension.Equals(".nes", StringComparison.OrdinalIgnoreCase) &&
            TryGetNesPayload(image, out var nesPayload) &&
            TryResolveBytes(nesPayload, out title))
        {
            return true;
        }

        if ((extension.Equals(".sfc", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".smc", StringComparison.OrdinalIgnoreCase)) &&
            image.Length % 1024 == 512 &&
            TryResolveBytes(image.AsSpan(512), out title))
        {
            return true;
        }

        if (extension.Equals(".fds", StringComparison.OrdinalIgnoreCase) &&
            image.Length > 16 &&
            image.AsSpan(0, 4).SequenceEqual("FDS\x1A"u8) &&
            TryResolveBytes(image.AsSpan(16), out title))
        {
            return true;
        }

        return false;
    }

    private bool TryResolveBytes(ReadOnlySpan<byte> bytes, out string title)
    {
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes));
        if (_sha1Titles.TryGetValue(sha1, out title!))
        {
            return true;
        }

        var crc32 = CalculateCrc32(bytes).ToString("X8");
        return _crc32Titles.TryGetValue(crc32, out title!);
    }

    private void LoadFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            LoadJson(path);
            return;
        }

        var text = File.ReadAllText(path);
        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
            text.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal))
        {
            LoadXml(text);
            return;
        }

        LoadClrMamePro(text);
    }

    private void LoadJson(string path)
    {
        var document = JsonSerializer.Deserialize<RomCatalogDocument>(
            File.ReadAllText(path),
            RomTitleResolverJson.Options);
        if (document?.Games is null)
        {
            return;
        }

        foreach (var game in document.Games)
        {
            Add(game.Title, game.Sha1, game.Crc32);
        }
    }

    private void LoadXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null
        };
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, settings);
        var document = XDocument.Load(reader, LoadOptions.None);

        foreach (var game in document
                     .Descendants()
                     .Where(element =>
                         element.Name.LocalName.Equals("game", StringComparison.OrdinalIgnoreCase) ||
                         element.Name.LocalName.Equals("machine", StringComparison.OrdinalIgnoreCase) ||
                         element.Name.LocalName.Equals("software", StringComparison.OrdinalIgnoreCase)))
        {
            var title = game.Elements()
                .FirstOrDefault(element =>
                    element.Name.LocalName.Equals("description", StringComparison.OrdinalIgnoreCase))
                ?.Value;
            title ??= game.Attribute("name")?.Value;

            foreach (var rom in game.Descendants().Where(element =>
                         element.Name.LocalName.Equals("rom", StringComparison.OrdinalIgnoreCase)))
            {
                Add(
                    title,
                    FindAttribute(rom, "sha1"),
                    FindAttribute(rom, "crc") ?? FindAttribute(rom, "crc32"));
            }
        }
    }

    private void LoadClrMamePro(string text)
    {
        var tokens = Tokenize(text);
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (tokens[index].Kind != DatTokenKind.Word ||
                ( !tokens[index].Value.Equals("game", StringComparison.OrdinalIgnoreCase) &&
                  !tokens[index].Value.Equals("machine", StringComparison.OrdinalIgnoreCase)) ||
                tokens[index + 1].Kind != DatTokenKind.Open)
            {
                continue;
            }

            var end = FindClosingToken(tokens, index + 1);
            if (end < 0)
            {
                break;
            }

            string? name = null;
            string? description = null;
            var hashes = new List<(string? Sha1, string? Crc32)>();
            var depth = 1;

            for (var item = index + 2; item < end;)
            {
                var token = tokens[item];
                if (token.Kind == DatTokenKind.Open)
                {
                    depth++;
                    item++;
                    continue;
                }

                if (token.Kind == DatTokenKind.Close)
                {
                    depth--;
                    item++;
                    continue;
                }

                if (depth == 1 &&
                    token.Kind == DatTokenKind.Word &&
                    item + 1 < end)
                {
                    if (token.Value.Equals("name", StringComparison.OrdinalIgnoreCase))
                    {
                        name = tokens[item + 1].Value;
                        item += 2;
                        continue;
                    }

                    if (token.Value.Equals("description", StringComparison.OrdinalIgnoreCase))
                    {
                        description = tokens[item + 1].Value;
                        item += 2;
                        continue;
                    }

                    if (token.Value.Equals("rom", StringComparison.OrdinalIgnoreCase) &&
                        tokens[item + 1].Kind == DatTokenKind.Open)
                    {
                        var romEnd = FindClosingToken(tokens, item + 1);
                        if (romEnd < 0 || romEnd > end)
                        {
                            break;
                        }

                        hashes.Add(ReadRomHashes(tokens, item + 2, romEnd));
                        item = romEnd + 1;
                        continue;
                    }
                }

                item++;
            }

            var title = string.IsNullOrWhiteSpace(description) ? name : description;
            foreach (var hash in hashes)
            {
                Add(title, hash.Sha1, hash.Crc32);
            }

            index = end;
        }
    }

    private static (string? Sha1, string? Crc32) ReadRomHashes(
        IReadOnlyList<DatToken> tokens,
        int start,
        int end)
    {
        string? sha1 = null;
        string? crc32 = null;
        var depth = 1;

        for (var index = start; index + 1 < end; index++)
        {
            if (tokens[index].Kind == DatTokenKind.Open)
            {
                depth++;
                continue;
            }

            if (tokens[index].Kind == DatTokenKind.Close)
            {
                depth--;
                continue;
            }

            if (depth != 1 || tokens[index].Kind != DatTokenKind.Word)
            {
                continue;
            }

            if (tokens[index].Value.Equals("sha1", StringComparison.OrdinalIgnoreCase))
            {
                sha1 = tokens[index + 1].Value;
            }
            else if (
                tokens[index].Value.Equals("crc", StringComparison.OrdinalIgnoreCase) ||
                tokens[index].Value.Equals("crc32", StringComparison.OrdinalIgnoreCase))
            {
                crc32 = tokens[index + 1].Value;
            }
        }

        return (sha1, crc32);
    }

    private void Add(string? title, string? sha1, string? crc32)
    {
        title = NormalizeTitle(title);
        if (title is null)
        {
            return;
        }

        AddHash(_sha1Titles, _ambiguousSha1, NormalizeHash(sha1, 40), title);
        AddHash(_crc32Titles, _ambiguousCrc32, NormalizeHash(crc32, 8), title);
    }

    private static void AddHash(
        IDictionary<string, string> titles,
        ISet<string> ambiguous,
        string? hash,
        string title)
    {
        if (hash is null || ambiguous.Contains(hash))
        {
            return;
        }

        if (titles.TryGetValue(hash, out var existing))
        {
            if (!string.Equals(existing, title, StringComparison.Ordinal))
            {
                titles.Remove(hash);
                ambiguous.Add(hash);
            }

            return;
        }

        titles[hash] = title;
    }

    private static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return string.Join(' ', title.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string? NormalizeHash(string? hash, int expectedLength)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        var normalized = new string(hash.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return normalized.Length == expectedLength ? normalized : null;
    }

    private static string? FindAttribute(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private static bool TryGetNesPayload(byte[] image, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (image.Length < 16 || !image.AsSpan(0, 4).SequenceEqual("NES\x1A"u8))
        {
            return false;
        }

        try
        {
            var isNes20 = (image[7] & 0x0C) == 0x08;
            var prgLength = GetNesRomSize(image[4], (byte)(image[9] & 0x0F), isNes20, 16_384);
            var chrLength = GetNesRomSize(image[5], (byte)(image[9] >> 4), isNes20, 8_192);
            var offset = 16 + ((image[6] & 0x04) != 0 ? 512 : 0);
            var contentLength = checked(prgLength + chrLength);
            if (contentLength <= 0 || image.Length < offset + contentLength)
            {
                return false;
            }

            payload = image.AsSpan(offset, contentLength);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static int GetNesRomSize(
        byte leastSignificant,
        byte mostSignificant,
        bool isNes20,
        int unitSize)
    {
        if (!isNes20)
        {
            return checked(leastSignificant * unitSize);
        }

        if (mostSignificant != 0x0F)
        {
            return checked((leastSignificant | (mostSignificant << 8)) * unitSize);
        }

        var exponent = leastSignificant >> 2;
        if (exponent > 30)
        {
            throw new OverflowException();
        }

        return checked((1 << exponent) * (((leastSignificant & 0x03) * 2) + 1));
    }

    private static List<DatToken> Tokenize(string text)
    {
        var tokens = new List<DatToken>();
        for (var index = 0; index < text.Length;)
        {
            var character = text[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            if (character == '(')
            {
                tokens.Add(new DatToken(DatTokenKind.Open, "("));
                index++;
                continue;
            }

            if (character == ')')
            {
                tokens.Add(new DatToken(DatTokenKind.Close, ")"));
                index++;
                continue;
            }

            if (character == '"')
            {
                index++;
                var value = new StringBuilder();
                while (index < text.Length)
                {
                    character = text[index++];
                    if (character == '"')
                    {
                        break;
                    }

                    if (character == '\\' && index < text.Length)
                    {
                        value.Append(text[index++]);
                    }
                    else
                    {
                        value.Append(character);
                    }
                }

                tokens.Add(new DatToken(DatTokenKind.String, value.ToString()));
                continue;
            }

            var start = index;
            while (index < text.Length &&
                   !char.IsWhiteSpace(text[index]) &&
                   text[index] is not ('(' or ')' or '"'))
            {
                index++;
            }

            if (index > start)
            {
                tokens.Add(new DatToken(DatTokenKind.Word, text[start..index]));
            }
        }

        return tokens;
    }

    private static int FindClosingToken(IReadOnlyList<DatToken> tokens, int openIndex)
    {
        var depth = 0;
        for (var index = openIndex; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == DatTokenKind.Open)
            {
                depth++;
            }
            else if (tokens[index].Kind == DatTokenKind.Close && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static uint CalculateCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc = Crc32Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320U ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private sealed record RomCatalogDocument(IReadOnlyList<RomCatalogEntry>? Games);

    private sealed record RomCatalogEntry(
        string? Title,
        string? Sha1,
        string? Crc32);

    private readonly record struct DatToken(DatTokenKind Kind, string Value);

    private enum DatTokenKind
    {
        Word,
        String,
        Open,
        Close
    }

    private static class RomTitleResolverJson
    {
        public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
    }
}
