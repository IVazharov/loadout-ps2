using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Loadout.SpeedTest;

/// <summary>What is known about one stretch of a disc being copied.</summary>
public enum SectorState
{
    /// <summary>Not read yet.</summary>
    Untried = '?',

    /// <summary>Failed, or jumped over, in the fast pass.</summary>
    Skipped = '/',

    /// <summary>Failed in the patient pass, as a whole chunk.</summary>
    Failed = '*',

    /// <summary>Failed in small blocks; left for single-sector retries.</summary>
    Bad = '-',

    /// <summary>Read and written to the image.</summary>
    Good = '+',
}

/// <summary>
/// The state of every sector of a disc copy, as a short list of stretches. It is saved beside the image after each change
/// that matters, so a copy can stop at any time and go on later, even in another drive.
/// </summary>
public sealed class DiscCopyMap
{
    private List<(long Start, long Count, SectorState State)> _ranges;

    private DiscCopyMap(long sectors, string identity, List<(long, long, SectorState)> ranges)
    {
        Sectors = sectors;
        Identity = identity;
        _ranges = ranges;
    }

    public long Sectors { get; }

    /// <summary>Names the disc, so a copy only resumes on the same one.</summary>
    public string Identity { get; }

    /// <summary>The adaptive sweep under way (0 before the first), so a stopped copy goes on where it was.</summary>
    public int Sweep { get; set; }

    /// <summary>How far that sweep got: the next sector forwards, or the end of what is left backwards; -1 at its start.</summary>
    public long SweepPosition { get; set; } = -1;

    public static DiscCopyMap New(long sectors, string identity) => new(sectors, identity, [(0, sectors, SectorState.Untried)]);

    public static DiscCopyMap? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var lines = File.ReadAllLines(path);
        var sectors = long.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture);
        var identity = lines[1]["disc ".Length..];
        var sweep = lines.FirstOrDefault(line => line.StartsWith("sweep ", StringComparison.Ordinal))?.Split(' ');
        var ranges = lines.Skip(2).Where(line => line.Length > 0 && char.IsAsciiDigit(line[0])).Select(line =>
        {
            var parts = line.Split(' ');
            return (long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture), (SectorState)parts[2][0]);
        }).ToList();
        return new DiscCopyMap(sectors, identity, ranges)
        {
            Sweep = sweep is null ? 0 : int.Parse(sweep[1], CultureInfo.InvariantCulture),
            SweepPosition = sweep is null ? -1 : long.Parse(sweep[2], CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Writes the map to a new file and then puts it in place, so a stopped copy never leaves half a map.</summary>
    public void Save(string path)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"sectors {Sectors}\n");
        text.Append(CultureInfo.InvariantCulture, $"disc {Identity}\n");
        text.Append(CultureInfo.InvariantCulture, $"sweep {Sweep} {SweepPosition}\n");
        foreach (var (start, count, state) in _ranges)
        {
            text.Append(CultureInfo.InvariantCulture, $"{start} {count} {(char)state}\n");
        }

        var temporary = path + ".new";
        File.WriteAllText(temporary, text.ToString());
        File.Move(temporary, path, overwrite: true);
    }

    public long Count(SectorState state) => _ranges.Where(r => r.State == state).Sum(r => r.Count);

    /// <summary>The stretches in a state, as they are now.</summary>
    public List<(long Start, long Count)> Ranges(SectorState state) => [.. _ranges.Where(r => r.State == state).Select(r => (r.Start, r.Count))];

    /// <summary>
    /// Puts every stretch not read yet back to skipped and the sweeps back to the first, so they start over on them in big
    /// pieces (after cleaning the disc, or in another drive).
    /// </summary>
    public void RetryAll()
    {
        Sweep = 0;
        SweepPosition = -1;
        foreach (var state in new[] { SectorState.Failed, SectorState.Bad })
        {
            foreach (var (start, count) in Ranges(state))
            {
                Mark(start, count, SectorState.Skipped);
            }
        }
    }

    public void Mark(long start, long count, SectorState state)
    {
        var end = start + count;
        var result = new List<(long Start, long Count, SectorState State)>(_ranges.Count + 2);
        foreach (var (s, c, st) in _ranges)
        {
            var e = s + c;
            if (e <= start || s >= end)
            {
                result.Add((s, c, st));
                continue;
            }

            if (s < start)
            {
                result.Add((s, start - s, st));
            }

            if (e > end)
            {
                result.Add((end, e - end, st));
            }
        }

        result.Add((start, count, state));
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(long Start, long Count, SectorState State)>(result.Count);
        foreach (var range in result)
        {
            if (merged.Count > 0 && merged[^1].State == range.State && merged[^1].Start + merged[^1].Count == range.Start)
            {
                merged[^1] = (merged[^1].Start, merged[^1].Count + range.Count, range.State);
            }
            else
            {
                merged.Add(range);
            }
        }

        _ranges = merged;
    }
}

/// <summary>
/// Copies a disc to an image file the way disc-rescue tools do, so a scratched disc gives up what it can:
/// 1. fast: everything in order; on a failure, jump ahead in growing steps, so a weak area costs a few failures, not hundreds;
/// 2. adaptive sweeps over what is still missing: big pieces while reads go well, smaller ones at the first failure (the
///    failed piece is read again in smaller pieces at once), bigger again after a run of good reads. A single sector that
///    fails is marked bad and left for the next sweep. A failed read costs the same whatever its size (this drive: 7.5 s,
///    then a rest), so big pieces win wherever the disc allows, and small ones find the bad spots between them. Pieces
///    line up with the disc's 16-sector error-correction blocks, so a 32 KB piece never straddles two of them. Even
///    sweeps go from the end of each stretch backwards: the drive meets the bad spots from the other side.
/// After every failed read the drive is rested until a sector it read before reads again: this drive refuses every read for
/// half a minute or so after one fails. The first wait adapts: shorter while the drive is back by then, longer when it is
/// not, and never again as short as a wait that was too short. The map keeps the sweep and how far it got, so a stopped
/// copy goes on where it was.
/// </summary>
public sealed class DiscCopier(DiscDrive drive, FileStream image, DiscCopyMap map, string mapPath, Action<string> note)
{
    private const int Chunk = 128;
    private const int Sweeps = 3;
    private const int Block = 16;
    private const int DeadBlockAfter = 3;

    // Piece sizes in sectors (128 = 256 KB, 16 = one 32 KB DVD error-correction block, 1), and the good reads in a row
    // at each size before going one size up.
    private static readonly int[] Sizes = [128, 16, 1];
    private static readonly int[] UpAfter = [0, 8, 32];
    private const long FirstJump = 512;
    private const long LastJump = 32768;
    private static readonly TimeSpan FirstRest = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShortestRest = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LongestRest = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RestStep = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RestLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(5);

    private readonly byte[] _buffer = GC.AllocateUninitializedArray<byte>(Chunk * DiscDrive.SectorSize, pinned: true);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastSave;
    private TimeSpan _lastProgress;
    private int _size = Chunk;
    private TimeSpan _firstRest = FirstRest;
    private TimeSpan _restFloor = ShortestRest;
    private DateTime _stopAt = DateTime.MaxValue;

    public int Failures { get; private set; }

    public TimeSpan Resting { get; private set; }

    /// <summary>A sector that read before; after a failure the drive is rested until it reads again.</summary>
    public long KnownGood { get; init; } = 16;

    /// <summary>The adaptive sweep to start with: 2 starts from the end backwards, as after a stop partway through sweep 1.</summary>
    public int FirstSweep { get; init; } = 1;

    /// <summary>Runs the passes from the first one with work left. Returns false when it stopped at the time limit.</summary>
    public bool Run(TimeSpan? limit)
    {
        if (limit is { } span)
        {
            _stopAt = DateTime.UtcNow + span;
        }

        var finished = Fast() && Adaptive();
        Save(force: true);
        return finished;
    }

    private bool TimeUp() => DateTime.UtcNow >= _stopAt;

    private bool Fast()
    {
        if (map.Count(SectorState.Untried) == 0)
        {
            return true;
        }

        note($"fast pass: {Mb(map.Count(SectorState.Untried))} untried");
        var jump = FirstJump;
        var goodInARow = 0;
        foreach (var (start, count) in map.Ranges(SectorState.Untried))
        {
            var position = start;
            var end = start + count;
            while (position < end)
            {
                if (TimeUp())
                {
                    return false;
                }

                var n = (int)Math.Min(Chunk, end - position);
                if (Read(position, n, SectorState.Skipped))
                {
                    position += n;
                    if (++goodInARow >= 64)
                    {
                        jump = FirstJump;
                    }

                    Progress();
                    continue;
                }

                goodInARow = 0;
                var skip = Math.Min(jump, end - position - n);
                if (skip > 0)
                {
                    map.Mark(position + n, skip, SectorState.Skipped);
                    note($"  jumping {Mb(skip)} ahead, to sector {position + n + skip}");
                }

                position += n + skip;
                jump = Math.Min(jump * 2, LastJump);
                Save(force: true);
            }
        }

        return true;
    }

    /// <summary>
    /// Sweeps over every stretch still missing, changing the piece size as reads go (see the class comment). Each sector is
    /// tried at most once per sweep at the smallest size; the next sweep goes over the bad ones again.
    /// </summary>
    private bool Adaptive()
    {
        var resumeSweep = FirstSweep > 1 ? FirstSweep : Math.Max(map.Sweep, 1);
        for (var sweep = resumeSweep; sweep <= Math.Max(Sweeps, resumeSweep); sweep++)
        {
            var backwards = sweep % 2 == 0;
            var resumeAt = sweep == map.Sweep ? map.SweepPosition : -1;
            if (sweep != map.Sweep)
            {
                map.Sweep = sweep;
                map.SweepPosition = -1;
            }
            var targets = new[] { SectorState.Skipped, SectorState.Failed, SectorState.Bad }
                .SelectMany(map.Ranges)
                .OrderBy(r => backwards ? -r.Start : r.Start)
                .ToList();
            if (targets.Count == 0)
            {
                return true;
            }

            note($"adaptive sweep {sweep}{(backwards ? ", from the end backwards" : "")}: {Mb(targets.Sum(r => r.Count))} missing{(resumeAt >= 0 ? $", going on from sector {resumeAt}" : "")}");
            var level = 0;
            var good = 0;
            var deadInBlock = 0;
            var deadBlock = -1L;
            foreach (var (start, count) in targets)
            {
                // Still to read in this stretch: [low, high), less what this sweep did before a stop.
                var low = start;
                var high = start + count;
                if (resumeAt >= 0)
                {
                    if (backwards)
                    {
                        high = Math.Min(high, resumeAt);
                    }
                    else
                    {
                        low = Math.Max(low, resumeAt);
                    }
                }
                while (low < high)
                {
                    if (TimeUp())
                    {
                        return false;
                    }

                    var size = Sizes[level];
                    var smallest = level == Sizes.Length - 1;
                    var first = backwards ? Math.Max(low, (high - 1) / size * size) : low;
                    var n = (int)(backwards ? high - first : Math.Min(size - (low % size), high - low));
                    if (Read(first, n, smallest ? SectorState.Bad : SectorState.Skipped))
                    {
                        if (backwards)
                        {
                            high -= n;
                        }
                        else
                        {
                            low += n;
                        }

                        deadInBlock = 0;
                        if (level > 0 && ++good >= UpAfter[level])
                        {
                            level--;
                            good = 0;
                        }
                    }
                    else
                    {
                        good = 0;
                        if (!smallest)
                        {
                            level++;
                        }
                        else if (backwards)
                        {
                            high--;
                        }
                        else
                        {
                            low++;
                        }

                        // When single sectors keep failing in one error-correction block, the drive cannot correct the
                        // block at all: leave the rest of it for the next sweep instead of paying a rest for each sector.
                        if (smallest && first / Block != deadBlock)
                        {
                            deadBlock = first / Block;
                            deadInBlock = 0;
                        }

                        if (smallest && ++deadInBlock >= DeadBlockAfter)
                        {
                            var block = first / Block * Block;
                            var rest = backwards ? (Math.Max(low, block), high) : (low, Math.Min(high, block + Block));
                            if (rest.Item2 > rest.Item1)
                            {
                                map.Mark(rest.Item1, rest.Item2 - rest.Item1, SectorState.Bad);
                                note($"  block {block} looks dead: leaving its other {rest.Item2 - rest.Item1} sectors for the next sweep");
                                if (backwards)
                                {
                                    high = rest.Item1;
                                }
                                else
                                {
                                    low = rest.Item2;
                                }
                            }

                            deadInBlock = 0;
                        }
                    }

                    map.SweepPosition = backwards ? high : low;
                    _size = Sizes[level];
                    Progress();
                }
            }
        }

        return true;
    }

    /// <summary>Reads sectors once and writes them to the image. On a failure marks them, rests the drive and returns false.</summary>
    private bool Read(long sector, int count, SectorState onFailure)
    {
        var data = _buffer.AsSpan(0, count * DiscDrive.SectorSize);
        var started = _clock.Elapsed;
        var error = drive.TryRead(sector, data);
        var took = _clock.Elapsed - started;
        if (error is null)
        {
            image.Position = sector * DiscDrive.SectorSize;
            image.Write(data);
            map.Mark(sector, count, SectorState.Good);
            if (took.TotalSeconds >= 2)
            {
                note(string.Create(CultureInfo.InvariantCulture, $"  slow: {count} at sector {sector} took {took.TotalSeconds:0.0} s"));
            }

            Save(force: false);
            return true;
        }

        Failures++;
        map.Mark(sector, count, onFailure);
        Save(force: true);
        note(string.Create(CultureInfo.InvariantCulture, $"  FAILED: {count} at sector {sector} in {took.TotalSeconds:0.0} s ({error})"));
        Rest();
        return false;
    }

    /// <summary>
    /// Waits until the drive reads a sector it read before, or gives up after a few minutes. When the drive is back at the
    /// first check, the next first wait is 5 s shorter; for each check it fails, 5 s longer.
    /// </summary>
    private void Rest()
    {
        var started = _clock.Elapsed;
        var waited = _firstRest;
        Thread.Sleep(_firstRest);
        var probe = _buffer.AsSpan(0, DiscDrive.SectorSize);
        var failedChecks = 0;
        while (drive.TryRead(KnownGood, probe) is not null)
        {
            failedChecks++;
            if (_clock.Elapsed - started > RestLimit)
            {
                note("  the drive still refuses to read; going on anyway");
                break;
            }

            Thread.Sleep(RestStep);
        }

        // A wait that was too short once is not tried again: the next first wait stays above it.
        if (failedChecks > 0)
        {
            _restFloor = Min(Max(_restFloor, waited + TimeSpan.FromSeconds(5)), LongestRest);
        }

        _firstRest = failedChecks == 0
            ? Max(_firstRest - TimeSpan.FromSeconds(5), _restFloor)
            : Min(Max(_firstRest + TimeSpan.FromSeconds(5 * failedChecks), _restFloor), LongestRest);
        var rested = _clock.Elapsed - started;
        Resting += rested;
        note(string.Create(CultureInfo.InvariantCulture, $"  rested {rested.TotalSeconds:0} s (first wait {waited.TotalSeconds:0} s, {failedChecks} failed checks; next first wait {_firstRest.TotalSeconds:0} s)"));
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private void Save(bool force)
    {
        if (!force && _clock.Elapsed - _lastSave < SaveEvery)
        {
            return;
        }

        // The image first, so the map never claims sectors the image does not hold yet.
        image.Flush(flushToDisk: false);
        map.Save(mapPath);
        _lastSave = _clock.Elapsed;
    }

    private void Progress()
    {
        if (_clock.Elapsed - _lastProgress < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _lastProgress = _clock.Elapsed;
        note($"  pieces of {_size}: good {Mb(map.Count(SectorState.Good))}, untried {Mb(map.Count(SectorState.Untried))}, skipped {Mb(map.Count(SectorState.Skipped))}, failed {Mb(map.Count(SectorState.Failed))}, bad {Mb(map.Count(SectorState.Bad))}; {Failures} failures, rested {Resting.TotalMinutes:0.0} min");
    }

    public static string Mb(long sectors) => string.Create(CultureInfo.InvariantCulture, $"{sectors * DiscDrive.SectorSize / SpeedTestWire.BytesPerMegabyte:0.0} MB");
}
