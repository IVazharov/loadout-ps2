using System.Globalization;
using System.Text;

namespace Loadout.Discs;

/// <summary>A stretch of sectors in one state.</summary>
public readonly record struct DiscMapRange(long Start, long Count, DiscSectorState State)
{
    public long End => Start + Count;
}

/// <summary>
/// The state of every sector of a disc being backed up, as a list of stretches, with how each was read. It is saved beside
/// the copy, so a backup can stop at any time and go on later, after cleaning the disc or in another drive.
/// </summary>
public sealed class DiscCopyMap
{
    private const string Header = "loadout disc map 1";

    private readonly List<DiscMapRange> _ranges;
    private readonly List<double> _cleanedRings = [];

    private DiscCopyMap(long sectors, string identity, List<DiscMapRange> ranges)
    {
        Sectors = sectors;
        Identity = identity;
        _ranges = ranges;
    }

    public long Sectors { get; }

    /// <summary>Names the disc (<see cref="DiscIdentity.Format"/>), so a copy only goes on with the same disc.</summary>
    public string Identity { get; }

    /// <summary>The pass after the first that is under way: 0 during the first pass, 1 for the first sweep, and so on.</summary>
    public int Sweep { get; set; }

    /// <summary>
    /// How far that sweep got: the next sector going forwards, or the end of what is left going backwards; -1 when it has
    /// not started. A stopped copy goes on from here.
    /// </summary>
    public long SweepPosition { get; set; } = -1;

    /// <summary>How many times the disc was cleaned during this copy.</summary>
    public int Cleanings { get; private set; }

    /// <summary>Where the disc was cleaned: the distances from the centre (mm) of its trouble at each cleaning.</summary>
    public IReadOnlyList<double> CleanedRingsMm => _cleanedRings;

    /// <summary>Every stretch, in order, covering the whole disc.</summary>
    public IReadOnlyList<DiscMapRange> Ranges => _ranges;

    public long ReadSectors => _ranges.Where(r => r.State.IsRead()).Sum(r => r.Count);

    /// <summary>Sectors the first pass reached but that are not in the copy yet.</summary>
    public long MissingSectors => _ranges.Where(r => r.State.IsMissing()).Sum(r => r.Count);

    public long UntriedSectors => Count(DiscSectorState.Untried);

    public bool IsComplete => ReadSectors == Sectors;

    public static DiscCopyMap New(long sectors, string identity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectors);
        return new DiscCopyMap(sectors, identity, [new DiscMapRange(0, sectors, DiscSectorState.Untried)]);
    }

    public static DiscCopyMap Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Reads a map file's text. Throws <see cref="InvalidDataException"/> when it is not a whole, valid map.</summary>
    public static DiscCopyMap Parse(string text)
    {
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToList();
        if (lines.Count < 6 || lines[0] != Header)
        {
            throw new InvalidDataException("This is not a disc map, or it is cut short.");
        }

        var sectors = Number(Field(lines[1], "sectors"));
        var identity = Field(lines[2], "disc");
        var sweep = Field(lines[3], "sweep").Split(' ');
        var cleanings = Field(lines[4], "cleanings");
        if (sweep.Length != 2)
        {
            throw new InvalidDataException("The disc map's sweep line is not valid.");
        }

        var ranges = new List<DiscMapRange>();
        var expected = 0L;
        var cleanedAt = new List<double>();
        var first = 5;
        if (lines[5].StartsWith("cleaned-at ", StringComparison.Ordinal))
        {
            foreach (var mm in lines[5]["cleaned-at ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                cleanedAt.Add(double.TryParse(mm, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : throw new InvalidDataException($"The disc map has '{mm}' where a distance should be."));
            }

            first = 6;
        }

        foreach (var line in lines.Skip(first))
        {
            var parts = line.Split(' ');
            if (parts.Length != 3 || parts[2].Length != 1 || !DiscSectorStates.TryFromCode(parts[2][0], out var state))
            {
                throw new InvalidDataException($"The disc map has a line it can't read: {line}");
            }

            var start = Number(parts[0]);
            var count = Number(parts[1]);
            if (start != expected || count <= 0)
            {
                throw new InvalidDataException("The disc map's stretches don't follow one another.");
            }

            ranges.Add(new DiscMapRange(start, count, state));
            expected = start + count;
        }

        if (sectors <= 0 || expected != sectors)
        {
            throw new InvalidDataException("The disc map doesn't cover the whole disc.");
        }

        var map = new DiscCopyMap(sectors, identity, [])
        {
            Sweep = (int)Number(sweep[0]),
            SweepPosition = long.Parse(sweep[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
            Cleanings = (int)Number(cleanings),
        };
        foreach (var range in ranges)
        {
            map.Append(range);
        }

        map._cleanedRings.AddRange(cleanedAt);
        return map;
    }

    public string Format()
    {
        var text = new StringBuilder();
        text.Append(Header).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"sectors {Sectors}\n");
        text.Append(CultureInfo.InvariantCulture, $"disc {Identity}\n");
        text.Append(CultureInfo.InvariantCulture, $"sweep {Sweep} {SweepPosition}\n");
        text.Append(CultureInfo.InvariantCulture, $"cleanings {Cleanings}\n");
        if (_cleanedRings.Count > 0)
        {
            text.Append("cleaned-at ").Append(string.Join(' ', _cleanedRings.Select(mm => mm.ToString("0.0", CultureInfo.InvariantCulture)))).Append('\n');
        }
        foreach (var range in _ranges)
        {
            text.Append(CultureInfo.InvariantCulture, $"{range.Start} {range.Count} {(char)range.State}\n");
        }

        return text.ToString();
    }

    /// <summary>Writes the map to a new file, then puts it in place, so a stop at any moment never leaves half a map.</summary>
    public void Save(string path)
    {
        var temporary = path + ".new";
        File.WriteAllText(temporary, Format());
        File.Move(temporary, path, overwrite: true);
    }

    public long Count(DiscSectorState state) => _ranges.Where(r => r.State == state).Sum(r => r.Count);

    public DiscSectorState StateAt(long sector)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sector);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sector, Sectors);
        return _ranges[IndexOf(sector)].State;
    }

    /// <summary>The first sector in the copy, or -1 when none is.</summary>
    public long FirstReadSector() => _ranges.FirstOrDefault(r => r.State.IsRead()) is { Count: > 0 } range ? range.Start : -1;

    /// <summary>
    /// Starts the passes over everything still missing, from the first sweep in big pieces: after the disc was cleaned
    /// (<paramref name="cleaned"/>), or when it is in another drive.
    /// </summary>
    public void StartOver(bool cleaned, IReadOnlyList<double>? troubleRingsMm = null)
    {
        Sweep = 0;
        SweepPosition = -1;
        if (cleaned)
        {
            Cleanings++;
            foreach (var mm in troubleRingsMm ?? [])
            {
                // A ring cleaned again is still one ring.
                if (!_cleanedRings.Any(known => Math.Abs(known - mm) <= 1.5))
                {
                    _cleanedRings.Add(Math.Round(mm, 1));
                }
            }
        }
    }

    public void Mark(long start, long count, DiscSectorState state)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + count, Sectors);
        var end = start + count;
        var first = IndexOf(start);
        var last = IndexOf(end - 1);
        var replacement = new List<DiscMapRange>(3);
        if (_ranges[first].Start < start)
        {
            replacement.Add(_ranges[first] with { Count = start - _ranges[first].Start });
        }

        replacement.Add(new DiscMapRange(start, count, state));
        if (_ranges[last].End > end)
        {
            replacement.Add(new DiscMapRange(end, _ranges[last].End - end, _ranges[last].State));
        }

        _ranges.RemoveRange(first, last - first + 1);
        _ranges.InsertRange(first, replacement);

        // Join equal neighbours around what changed.
        var from = Math.Max(first - 1, 0);
        var to = Math.Min(first + replacement.Count, _ranges.Count - 1);
        for (var i = to; i > from; i--)
        {
            if (_ranges[i - 1].State == _ranges[i].State)
            {
                _ranges[i - 1] = _ranges[i - 1] with { Count = _ranges[i - 1].Count + _ranges[i].Count };
                _ranges.RemoveAt(i);
            }
        }
    }

    private void Append(DiscMapRange range)
    {
        if (_ranges.Count > 0 && _ranges[^1].State == range.State)
        {
            _ranges[^1] = _ranges[^1] with { Count = _ranges[^1].Count + range.Count };
        }
        else
        {
            _ranges.Add(range);
        }
    }

    private int IndexOf(long sector)
    {
        int low = 0, high = _ranges.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (_ranges[middle].Start <= sector)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    private static string Field(string line, string name) =>
        line.StartsWith(name + " ", StringComparison.Ordinal)
            ? line[(name.Length + 1)..]
            : throw new InvalidDataException($"The disc map has no '{name}' line where it should be.");

    private static long Number(string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"The disc map has '{text}' where a number should be.");
}
