using System.Security.Cryptography;
using System.Text.Json;

namespace Loadout.Images;

public static class ChdCacheLayout
{
    public const string TempSuffix = ".extracting";

    public const string IndexFileName = "conversion-cache.json";

    public static string Stem(string cacheDirectory, string sourcePath)
    {
        var full = Path.GetFullPath(sourcePath);
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full))).ToLowerInvariant()[..16];
        return Path.Combine(cacheDirectory, hash + "-" + Path.GetFileName(full));
    }

    public static string Iso(string cacheDirectory, string sourcePath) => Stem(cacheDirectory, sourcePath) + ".iso";

    public static string Cue(string cacheDirectory, string sourcePath) => Stem(cacheDirectory, sourcePath) + ".cue";

    public static string Bin(string cacheDirectory, string sourcePath) => Stem(cacheDirectory, sourcePath) + ".bin";

    public static string IsoTemp(string cacheDirectory, string sourcePath) => Iso(cacheDirectory, sourcePath) + TempSuffix;

    public static string CueTemp(string cacheDirectory, string sourcePath) => Stem(cacheDirectory, sourcePath) + TempSuffix + ".cue";

    public static string BinTemp(string cacheDirectory, string sourcePath) => Bin(cacheDirectory, sourcePath) + TempSuffix;

    public static string IndexPath(string cacheDirectory) => Path.Combine(cacheDirectory, IndexFileName);

    public static bool IsTemp(string path) =>
        path.EndsWith(TempSuffix, StringComparison.Ordinal) || path.EndsWith(TempSuffix + ".cue", StringComparison.Ordinal);
}

internal static class SourceFingerprint
{
    public const int FullHashLimit = 16 * 1024 * 1024;

    public static string Compute(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var length = stream.Length;
        if (length <= FullHashLimit)
        {
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        var prefix = new byte[1024 * 1024];
        var prefixRead = stream.Read(prefix, 0, prefix.Length);
        sha.TransformBlock(prefix, 0, prefixRead, null, 0);
        var tailCount = (int)Math.Min(1024 * 1024, length);
        stream.Position = length - tailCount;
        var suffix = new byte[tailCount];
        var suffixRead = stream.Read(suffix, 0, suffix.Length);
        sha.TransformBlock(suffix, 0, suffixRead, null, 0);
        var lengthBytes = BitConverter.GetBytes(length);
        sha.TransformFinalBlock(lengthBytes, 0, lengthBytes.Length);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
    }
}

internal sealed class CacheIdentity
{
    public required string SourcePath { get; init; }

    public required long SourceLength { get; init; }

    public required long SourceMtimeUtcTicks { get; init; }

    public required string Fingerprint { get; init; }

    public required string ToolVersion { get; init; }

    public static CacheIdentity Capture(string sourcePath, string toolVersion)
    {
        var info = new FileInfo(sourcePath);
        return new CacheIdentity
        {
            SourcePath = Path.GetFullPath(sourcePath),
            SourceLength = info.Length,
            SourceMtimeUtcTicks = info.LastWriteTimeUtc.Ticks,
            Fingerprint = SourceFingerprint.Compute(sourcePath),
            ToolVersion = toolVersion,
        };
    }
}

internal sealed class CacheRecord
{
    public string SourcePath { get; set; } = string.Empty;

    public long SourceLength { get; set; }

    public long SourceMtimeUtcTicks { get; set; }

    public string Fingerprint { get; set; } = string.Empty;

    public string OutputPath { get; set; } = string.Empty;

    public string? CompanionPath { get; set; }

    public long OutputLength { get; set; }

    public long CompanionLength { get; set; }

    public string? DiscId { get; set; }

    public string Status { get; set; } = "incomplete";

    public string ToolVersion { get; set; } = string.Empty;

    public uint Layer0Sectors { get; set; }

    public uint Layer1Sectors { get; set; }

    public int TrackCount { get; set; }

    public string Kind { get; set; } = string.Empty;
}

internal sealed class CacheDocument
{
    public List<CacheRecord> Records { get; set; } = new();
}

internal sealed class ConversionCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _directory;

    public ConversionCache(string directory)
    {
        _directory = directory;
    }

    public IReadOnlyList<string> LeftoverTemporaryFiles()
    {
        if (!Directory.Exists(_directory))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(_directory)
            .Where(ChdCacheLayout.IsTemp)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public CacheRecord? TryReuse(CacheIdentity identity, ChdImageKind kind)
    {
        var record = Load().Records.LastOrDefault(candidate => SameSource(candidate, identity) && candidate.Kind == kind.ToString());
        if (record == null || !string.Equals(record.Status, "valid", StringComparison.Ordinal))
        {
            return null;
        }

        if (ChdCacheLayout.IsTemp(record.OutputPath))
        {
            return null;
        }

        if (!OutputMatches(record))
        {
            return null;
        }

        return record;
    }

    public void Write(CacheRecord record)
    {
        Directory.CreateDirectory(_directory);
        var document = Load();
        document.Records.RemoveAll(candidate => string.Equals(candidate.SourcePath, record.SourcePath, StringComparison.Ordinal));
        document.Records.Add(record);
        var path = ChdCacheLayout.IndexPath(_directory);
        var temp = path + ChdCacheLayout.TempSuffix;
        File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private static bool OutputMatches(CacheRecord record)
    {
        if (!File.Exists(record.OutputPath))
        {
            return false;
        }

        var output = new FileInfo(record.OutputPath);
        if (output.Length != record.OutputLength || output.Length == 0)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(record.CompanionPath))
        {
            if (!File.Exists(record.CompanionPath))
            {
                return false;
            }

            var companion = new FileInfo(record.CompanionPath);
            if (companion.Length != record.CompanionLength || companion.Length == 0)
            {
                return false;
            }
        }

        var facts = Iso9660Image.Read(DataPath(record), ForcedLayout(record), declaredCompactDisc: record.Kind == ChdImageKind.Cd.ToString());
        if (!facts.Readable || facts.Corrupt)
        {
            return false;
        }

        if (facts.Layer0Sectors != record.Layer0Sectors || facts.Layer1Sectors != record.Layer1Sectors)
        {
            return false;
        }

        var actualId = facts.DiscId?.Canonical;
        return string.Equals(actualId, record.DiscId, StringComparison.Ordinal);
    }

    private static string DataPath(CacheRecord record)
        => string.IsNullOrEmpty(record.CompanionPath) ? record.OutputPath : record.CompanionPath;

    private static SectorLayout? ForcedLayout(CacheRecord record)
    {
        if (record.Kind != ChdImageKind.Cd.ToString() || string.IsNullOrEmpty(record.OutputPath) || !File.Exists(record.OutputPath))
        {
            return null;
        }

        try
        {
            var decision = CueSheetParser.Evaluate(CueSheetParser.Parse(record.OutputPath));
            return decision.Layout;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool SameSource(CacheRecord record, CacheIdentity identity)
        => string.Equals(record.SourcePath, identity.SourcePath, StringComparison.Ordinal)
            && record.SourceLength == identity.SourceLength
            && record.SourceMtimeUtcTicks == identity.SourceMtimeUtcTicks
            && string.Equals(record.Fingerprint, identity.Fingerprint, StringComparison.Ordinal)
            && string.Equals(record.ToolVersion, identity.ToolVersion, StringComparison.Ordinal);

    private CacheDocument Load()
    {
        var path = ChdCacheLayout.IndexPath(_directory);
        if (!File.Exists(path))
        {
            return new CacheDocument();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CacheDocument>(json, JsonOptions) ?? new CacheDocument();
    }
}
