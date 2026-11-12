using System.Diagnostics;
using System.Globalization;

namespace Loadout.Discs;

/// <summary>
/// One simulated disc drive, S: unless named otherwise, for trying Back up discs without a scratched disc: it reads a
/// disc image (2048-byte
/// sectors, such as a copy that matches Redump) the way the drive the copy was built on reads a scratched disc, only
/// faster. A dirty stretch fails every read, slowly, and the drive then refuses to read for a moment; around it a weak
/// band fails once and then reads. A cleaning clears the outer quarters of each dirty stretch, a second one the rest, so
/// a copy needs two cleanings to complete. Eject opens the tray, which closes by itself a few seconds later with the
/// disc cleaned. Given several images, the drive holds them in turn: each time the tray closes, the next disc is in it
/// (uncleaned), as when a pile of discs is backed up with Keep going. With <see cref="SpinUp"/>, the drive refuses to
/// read for that long after a disc goes in, as a real drive does while it spins the disc up.
/// </summary>
public sealed class SimulatedDiscDrives : IDiscDrives
{
    public const string DriveName = "S:";

    private readonly object _gate = new();
    private readonly IReadOnlyList<string> _images;
    private readonly long[] _sizes;
    private int _disc;
    private TimeSpan _spinUpFrom;
    private readonly List<(long From, long To)> _dirty;
    private readonly HashSet<long> _weakBlocksFailed = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _refuseUntil;
    private TimeSpan? _trayClosesAt;
    private int _inserted;

    /// <param name="image">The disc image, 2048-byte sectors.</param>
    /// <param name="dirty">Stretches of sectors [From, To) that fail until cleaned.</param>
    public SimulatedDiscDrives(string image, IReadOnlyList<(long From, long To)> dirty)
        : this([image], dirty)
    {
    }

    /// <param name="images">The discs the drive holds in turn, the first one in it now.</param>
    /// <param name="dirty">Stretches of sectors [From, To) that fail until cleaned, on every disc.</param>
    /// <param name="drive">The drive's name, such as S:.</param>
    public SimulatedDiscDrives(IReadOnlyList<string> images, IReadOnlyList<(long From, long To)> dirty, string drive = DriveName)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(dirty);
        ArgumentException.ThrowIfNullOrEmpty(drive);
        if (images.Count == 0)
        {
            throw new ArgumentException("A simulated drive needs a disc image.", nameof(images));
        }

        _sizes = new long[images.Count];
        for (var i = 0; i < images.Count; i++)
        {
            var length = new FileInfo(images[i]).Length;
            if (length == 0 || length % DiscSectors.Size != 0)
            {
                throw new InvalidDataException($"{images[i]} is not a disc image of 2048-byte sectors.");
            }

            _sizes[i] = length / DiscSectors.Size;
        }

        _images = [.. images];
        Drive = drive.TrimEnd('\\').ToUpperInvariant();
        _dirty = [.. dirty.Where(d => d.From >= 0 && d.To > d.From && d.To <= _sizes.Max()).OrderBy(d => d.From)];
    }

    /// <summary>The drive's name, such as S:.</summary>
    public string Drive { get; }

    /// <summary>The disc in the drive now: its size in sectors.</summary>
    public long Sectors
    {
        get
        {
            lock (_gate)
            {
                return _sizes[_disc];
            }
        }
    }

    /// <summary>How long the drive refuses to read after a disc goes in, and when Loadout starts; none by default.</summary>
    public TimeSpan SpinUp { get; init; }

    private string CurrentImage
    {
        get
        {
            lock (_gate)
            {
                return _images[_disc];
            }
        }
    }

    /// <summary>Sectors either side of each dirty stretch that fail once.</summary>
    public int WeakBand { get; init; } = 2048;

    /// <summary>How long a failed read takes (the real drive: 7.5 s).</summary>
    public TimeSpan FailureCost { get; init; } = TimeSpan.FromSeconds(0.4);

    /// <summary>How long the drive then refuses every read (the real drive: about 30 s).</summary>
    public TimeSpan Lockout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How fast it reads; 0 for as fast as the image file.</summary>
    public double BytesPerSecond { get; init; } = 40_000_000;

    /// <summary>How long the tray stays open after Eject before it closes by itself with the disc cleaned.</summary>
    public TimeSpan TrayOpenFor { get; init; } = TimeSpan.FromSeconds(8);

    public int Cleanings { get; private set; }

    public bool IsSimulated => true;

    /// <summary>The copy's numbers scaled down as the drive is: shorter rests, and a stall noticed after a minute.</summary>
    public DiscCopierSettings? CopierSettings { get; init; } = new()
    {
        FirstRest = TimeSpan.FromSeconds(2),
        ShortestRest = TimeSpan.FromSeconds(1),
        LongestRest = TimeSpan.FromSeconds(5),
        RestChange = TimeSpan.FromSeconds(0.5),
        RestCheckEvery = TimeSpan.FromSeconds(1),
        RestLimit = TimeSpan.FromSeconds(20),
        StartWait = TimeSpan.FromSeconds(10),
        StallTime = TimeSpan.FromMinutes(1),
        SaveEvery = TimeSpan.FromSeconds(2),
    };

    /// <summary>
    /// Two dirty stretches of <paramref name="length"/> sectors where a scratched DVD often has them: about a third and
    /// four fifths of the way out (where NFS ProStreet's were).
    /// </summary>
    public static IReadOnlyList<(long From, long To)> DefaultDirt(long sectors, long length = 400)
    {
        var first = sectors * 314 / 1000 / 16 * 16;
        var second = sectors * 820 / 1000 / 16 * 16;
        return [(first, Math.Min(sectors, first + length)), (second, Math.Min(sectors, second + length))];
    }

    /// <summary>Reads stretches such as "568320-568720,1482880-1483280"; empty text gives none.</summary>
    public static IReadOnlyList<(long From, long To)> ParseDirt(string? text)
    {
        var dirt = new List<(long From, long To)>();
        foreach (var part in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split('-');
            if (ends.Length != 2
                || !long.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                || !long.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out var to)
                || to <= from)
            {
                throw new FormatException($"\"{part}\" is not a stretch of sectors such as 568320-568720.");
            }

            dirt.Add((from, to));
        }

        return dirt;
    }

    public IReadOnlyList<string> List() => [Drive];

    public string Fingerprint(string drive)
    {
        lock (_gate)
        {
            CloseTrayIfDue();
            return !IsThis(drive) || _trayClosesAt is not null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"simulated|{Drive}|{_disc}|{Sectors}|{_inserted}");
        }
    }

    public DiscInDrive? Look(string drive)
    {
        try
        {
            using var reader = Open(drive);
            return new DiscInDrive(Drive, DiscMedia.Dvd, reader.Sectors, 1, 0, null, DiscIdentity.Read(reader));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public IDiscReader Open(string drive)
    {
        lock (_gate)
        {
            CloseTrayIfDue();
            if (!IsThis(drive) || _trayClosesAt is not null)
            {
                throw new IOException($"There is no disc in {drive}.");
            }
        }

        return new Reader(this);
    }

    public bool TryEject(string drive)
    {
        lock (_gate)
        {
            if (!IsThis(drive))
            {
                return false;
            }

            _trayClosesAt = _clock.Elapsed + TrayOpenFor;
            return true;
        }
    }

    /// <summary>Whether a read of these sectors fails now. A read that touches the weak band fails once there.</summary>
    internal bool Fails(long sector, int count, out string? failure)
    {
        lock (_gate)
        {
            if (_clock.Elapsed < _refuseUntil || _clock.Elapsed < _spinUpFrom + SpinUp)
            {
                failure = "The device is not ready.";
                return true;
            }

            var end = sector + count;
            var bad = false;
            foreach (var (from, to) in _dirty)
            {
                // A dirty stretch: its outer quarters clear with the first cleaning, the middle with the second.
                var quarter = Math.Max(1, (to - from) / 4);
                var stillDirty = Cleanings switch
                {
                    0 => (from, to),
                    1 => (from + quarter, to - quarter),
                    _ => (to, to),
                };
                if (sector < stillDirty.Item2 && end > stillDirty.Item1)
                {
                    bad = true;
                }

                // The weak band either side fails once per 16-sector block, then reads.
                foreach (var (bandFrom, bandTo) in new[] { (from - WeakBand, from), (to, to + WeakBand) })
                {
                    for (var block = Math.Max(sector, bandFrom) / 16; block * 16 < Math.Min(end, bandTo); block++)
                    {
                        bad |= _weakBlocksFailed.Add(block);
                    }
                }
            }

            if (!bad)
            {
                failure = null;
                return false;
            }

            _refuseUntil = _clock.Elapsed + FailureCost + Lockout;
            failure = "Data error (cyclic redundancy check).";
        }

        Thread.Sleep(FailureCost);
        return true;
    }

    private bool IsThis(string drive) => string.Equals(drive.TrimEnd('\\'), Drive, StringComparison.OrdinalIgnoreCase);

    // The tray closes by itself: with the disc cleaned, or with the next disc in it when the drive holds several.
    private void CloseTrayIfDue()
    {
        if (_trayClosesAt is { } at && _clock.Elapsed >= at)
        {
            _trayClosesAt = null;
            if (_images.Count > 1)
            {
                _disc = (_disc + 1) % _images.Count;
                Cleanings = 0;
                _weakBlocksFailed.Clear();
            }
            else
            {
                Cleanings++;
            }

            _inserted++;
            _refuseUntil = TimeSpan.Zero;
            _spinUpFrom = _clock.Elapsed;
        }
    }

    private sealed class Reader(SimulatedDiscDrives drives) : IDiscReader
    {
        private readonly FileStream _file = new(drives.CurrentImage, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        private readonly long _sectors = drives.Sectors;

        public string Name => drives.Drive;

        public long Sectors => _sectors;

        public DiscMedia Media => DiscMedia.Dvd;

        public int Layers => 1;

        public long Layer0Sectors => 0;

        public int CopySectorSize => DiscSectors.Size;

        public bool TryReadForCopy(long sector, Span<byte> destination, out string? failure) => TryRead(sector, destination, out failure);

        public bool TryRead(long sector, Span<byte> destination, out string? failure)
        {
            var count = destination.Length / DiscSectors.Size;
            if (sector < 0 || sector + count > _sectors)
            {
                failure = "The sector is past the end of the disc.";
                return false;
            }

            if (drives.Fails(sector, count, out failure))
            {
                return false;
            }

            var started = Stopwatch.GetTimestamp();
            _file.Position = sector * DiscSectors.Size;
            _file.ReadExactly(destination[..(count * DiscSectors.Size)]);
            if (drives.BytesPerSecond > 0)
            {
                var due = TimeSpan.FromSeconds(destination.Length / drives.BytesPerSecond) - Stopwatch.GetElapsedTime(started);
                if (due > TimeSpan.Zero)
                {
                    Thread.Sleep(due);
                }
            }

            return true;
        }

        public void Dispose() => _file.Dispose();
    }
}
