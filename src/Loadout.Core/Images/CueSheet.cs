namespace Loadout.Images;

internal sealed class CueFileEntry
{
    public CueFileEntry(string path, string type, IReadOnlyList<CueTrackEntry> tracks)
    {
        Path = path;
        Type = type;
        Tracks = tracks;
    }

    public string Path { get; }

    public string Type { get; }

    public IReadOnlyList<CueTrackEntry> Tracks { get; }
}

internal sealed class CueTrackEntry
{
    public CueTrackEntry(int number, string mode, string? index01)
    {
        Number = number;
        Mode = mode;
        Index01 = index01;
    }

    public int Number { get; }

    public string Mode { get; }

    public string? Index01 { get; }
}

internal sealed class CueSheet
{
    public CueSheet(string path, IReadOnlyList<CueFileEntry> files)
    {
        Path = path;
        Files = files;
    }

    public string Path { get; }

    public IReadOnlyList<CueFileEntry> Files { get; }

    public int TrackCount => Files.Sum(file => file.Tracks.Count);
}

internal readonly record struct CueDecision(bool Supported, string? Reason, SectorLayout? Layout);

internal static class CueSheetParser
{
    public static CueSheet Parse(string cuePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? string.Empty;
        var files = new List<CueFileEntry>();
        string? currentPath = null;
        string? currentType = null;
        List<CueTrackEntry>? tracks = null;
        var number = 0;
        var mode = string.Empty;
        string? index01 = null;

        void FlushTrack()
        {
            if (string.IsNullOrEmpty(mode))
            {
                return;
            }

            tracks?.Add(new CueTrackEntry(number, mode, index01));
            mode = string.Empty;
            index01 = null;
        }

        void FlushFile()
        {
            FlushTrack();
            if (currentPath == null || tracks == null)
            {
                return;
            }

            files.Add(new CueFileEntry(currentPath, currentType ?? string.Empty, tracks));
            currentPath = null;
            currentType = null;
            tracks = null;
        }

        foreach (var raw in File.ReadAllLines(cuePath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("REM", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
            {
                FlushFile();
                if (!TryParseFile(line, out var name, out var type))
                {
                    throw new InvalidDataException("CUE FILE line could not be parsed: " + line);
                }

                currentPath = Resolve(directory, name);
                currentType = type;
                tracks = new List<CueTrackEntry>();
                continue;
            }

            if (line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
            {
                FlushTrack();
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !int.TryParse(parts[1], out number))
                {
                    throw new InvalidDataException("CUE TRACK line could not be parsed: " + line);
                }

                mode = parts[2];
                index01 = null;
                continue;
            }

            if (line.StartsWith("INDEX ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[1] == "01")
                {
                    index01 = parts[2];
                }
            }
        }

        FlushFile();
        return new CueSheet(Path.GetFullPath(cuePath), files);
    }

    public static bool References(string cuePath, string binPath)
    {
        try
        {
            var sheet = Parse(cuePath);
            var target = Path.GetFullPath(binPath);
            return sheet.Files.Any(file => string.Equals(file.Path, target, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    public static CueDecision Evaluate(CueSheet sheet)
    {
        if (sheet.Files.Count != 1)
        {
            return new CueDecision(false, ImageLimits.MultipleFiles, null);
        }

        var file = sheet.Files[0];
        if (!file.Type.Equals("BINARY", StringComparison.OrdinalIgnoreCase))
        {
            return new CueDecision(false, ImageLimits.NotBinary, null);
        }

        if (file.Tracks.Count == 0)
        {
            return new CueDecision(false, ImageLimits.BadMode, null);
        }

        if (file.Tracks.Count != 1 || file.Tracks.Any(track => track.Mode.Equals("AUDIO", StringComparison.OrdinalIgnoreCase)))
        {
            return new CueDecision(false, ImageLimits.MultiTrack, LayoutOf(file.Tracks[0].Mode));
        }

        var track = file.Tracks[0];
        var layout = LayoutOf(track.Mode);
        if (layout == null)
        {
            return new CueDecision(false, ImageLimits.BadMode, null);
        }

        if (!string.Equals(track.Index01, "00:00:00", StringComparison.Ordinal))
        {
            return new CueDecision(false, ImageLimits.DataNotAtStart, layout);
        }

        return new CueDecision(true, null, layout);
    }

    public static SectorLayout? LayoutOf(string mode)
    {
        if (mode.Equals("MODE2/2352", StringComparison.OrdinalIgnoreCase))
        {
            return SectorLayout.Mode2Raw2352;
        }

        if (mode.Equals("MODE1/2352", StringComparison.OrdinalIgnoreCase))
        {
            return SectorLayout.Mode1Raw2352;
        }

        if (mode.Equals("MODE1/2048", StringComparison.OrdinalIgnoreCase))
        {
            return SectorLayout.Headerless2048;
        }

        return null;
    }

    private static bool TryParseFile(string line, out string name, out string type)
    {
        name = string.Empty;
        type = string.Empty;
        var rest = line[5..].Trim();
        if (rest.Length == 0)
        {
            return false;
        }

        if (rest[0] == '"')
        {
            var end = rest.IndexOf('"', 1);
            if (end < 0)
            {
                return false;
            }

            name = rest[1..end];
            type = rest[(end + 1)..].Trim();
            return name.Length > 0 && type.Length > 0;
        }

        var space = rest.LastIndexOf(' ');
        if (space <= 0)
        {
            return false;
        }

        name = rest[..space].Trim();
        type = rest[(space + 1)..].Trim();
        return name.Length > 0 && type.Length > 0;
    }

    private static string Resolve(string directory, string name)
    {
        var normalized = name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var combined = Path.IsPathRooted(normalized) ? normalized : Path.Combine(directory, normalized);
        return Path.GetFullPath(combined);
    }
}
