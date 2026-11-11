using System.Globalization;

namespace Loadout.Discs;

/// <summary>
/// Copies a disc to an image file as disc-rescue tools do, so a scratched disc gives up all it can, and never gives up by
/// itself: it goes on until every sector is in the copy or it is stopped.
/// <list type="number">
/// <item>The first pass reads everything in order, in big pieces. After a failed read it jumps ahead, 1 MB and then twice as
/// far each time up to 64 MB, so a weak area costs a few failures, not hundreds.</item>
/// <item>Later passes (sweeps) go over everything still missing, with no limit: big pieces while reads go well, smaller ones
/// at a failure (the failed piece is read again at once in smaller pieces), bigger again after a run of good reads. A
/// single sector that fails is left for the next sweep. Pieces line up with the DVD's 16-sector error-correction blocks;
/// when single sectors keep failing in one block, the rest of the block is left for the next sweep. Even sweeps go from
/// the end backwards, so the drive meets the bad spots from the other side.</item>
/// </list>
/// After every failed read the drive rests until a sector it read before reads again: the drive the copy was built on
/// refuses every read for half a minute or so after a failure. The first wait adapts: shorter while the drive is back by
/// then, longer when it is not, and never again as short as a wait that was too short.
/// <para>The map keeps the pass and how far it got, and is saved often (the image first), so a copy can be stopped at any
/// time and go on later, after cleaning the disc or in another drive. When the later passes read under 1 MB in 10 minutes,
/// the copy says it has stalled, as cleaning the disc then helps most.</para>
/// </summary>
public sealed class DiscCopier
{
    private readonly IDiscReader _drive;
    private readonly Stream _image;
    private readonly DiscCopyMap _map;
    private readonly Action<DiscCopyMap> _saveMap;
    private readonly DiscCopierSettings _settings;
    private readonly IDiscCopyClock _clock;
    private readonly byte[] _buffer;
    private readonly int _sectorSize;
    private readonly byte[] _probe = new byte[DiscSectors.Size];
    private TimeSpan _lastSave;
    private TimeSpan _lastStatus;
    private TimeSpan _firstRest;
    private TimeSpan _restFloor;
    private double _bytesPerSecond;
    private TimeSpan _stallSince;
    private long _stallRead;
    private bool _stalled;
    private DiscCopyPhase _phase = (DiscCopyPhase)(-1);
    private long _sector;
    private int _piece;
    private bool _backwards;

    public DiscCopier(IDiscReader drive, Stream image, DiscCopyMap map, Action<DiscCopyMap> saveMap, DiscCopierSettings? settings = null, IDiscCopyClock? clock = null)
    {
        if (map.Sectors != drive.Sectors)
        {
            throw new ArgumentException($"The map is for a disc of {map.Sectors} sectors; the one in {drive.Name} has {drive.Sectors}.", nameof(map));
        }

        _drive = drive;
        _image = image;
        _map = map;
        _saveMap = saveMap;
        _settings = settings ?? new DiscCopierSettings();
        _clock = clock ?? new SystemDiscCopyClock();
        _sectorSize = drive.CopySectorSize;
        _buffer = GC.AllocateUninitializedArray<byte>(Math.Max(_settings.FirstPassPiece, _settings.PieceSizes[0]) * _sectorSize, pinned: true);
        _firstRest = _settings.FirstRest;
        _restFloor = _settings.ShortestRest;
    }

    /// <summary>Called for each event worth a line in the copy's report.</summary>
    public Action<DiscCopyNote>? Note { get; init; }

    /// <summary>Gets the copy's status a few times a second, and at each change of phase.</summary>
    public IProgress<DiscCopyStatus>? Progress { get; init; }

    /// <summary>
    /// For an install that follows the copy: told of each piece once it is in the file, and broken at the first failed
    /// read or when the copy stops, as the sectors after it then won't come in order.
    /// </summary>
    public DiscCopyFeed? Feed { get; init; }

    public int Failures { get; private set; }

    /// <summary>The time spent resting the drive.</summary>
    public TimeSpan Rested { get; private set; }

    /// <summary>Copies until every sector is in the image, or until stopped. Runs on the thread pool.</summary>
    public Task<DiscCopyOutcome> RunAsync(CancellationToken cancellationToken) => Task.Run(() => RunCoreAsync(cancellationToken), CancellationToken.None);

    private async Task<DiscCopyOutcome> RunCoreAsync(CancellationToken cancellationToken)
    {
        var size = _map.Sectors * _sectorSize;
        if (_image.Length < size)
        {
            _image.SetLength(size);
        }

        _stallSince = _clock.Elapsed;
        _stallRead = _map.ReadSectors;
        try
        {
            await WaitForDriveAsync(cancellationToken).ConfigureAwait(false);
            await FirstPassAsync(cancellationToken).ConfigureAwait(false);
            await SweepsAsync(cancellationToken).ConfigureAwait(false);
            Save(force: true);
            Tell(DiscCopyNoteKind.Complete, "Every sector is in the copy.");
            Report(DiscCopyPhase.Complete, force: true);
            return DiscCopyOutcome.Complete;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Feed?.Break();
            Save(force: true);
            Tell(DiscCopyNoteKind.Stopped, $"Stopped with {Mb(_map.ReadSectors)} of {Mb(_map.Sectors)} in the copy.");
            Report(DiscCopyPhase.Stopped, force: true);
            return DiscCopyOutcome.Stopped;
        }
    }

    private async Task WaitForDriveAsync(CancellationToken cancellationToken)
    {
        var started = _clock.Elapsed;
        var told = false;
        while (!Probe())
        {
            if (_clock.Elapsed - started >= _settings.StartWait)
            {
                Tell(DiscCopyNoteKind.DriveStillRefusing, "The drive still refuses to read; going on anyway.");
                return;
            }

            if (!told)
            {
                Tell(DiscCopyNoteKind.WaitingForDrive, "The drive refuses to read for now; waiting for it.");
                told = true;
            }

            Report(DiscCopyPhase.WaitingForDrive, force: true, restLeft: _settings.RestCheckEvery);
            await _clock.DelayAsync(_settings.RestCheckEvery, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FirstPassAsync(CancellationToken cancellationToken)
    {
        if (_map.Sweep > 0 || _map.UntriedSectors == 0)
        {
            return;
        }

        Tell(DiscCopyNoteKind.FirstPassStarted, $"First pass: {Mb(_map.UntriedSectors)} to read.");
        var piece = _settings.FirstPassPiece;
        var jump = _settings.FirstJump;
        var goodInARow = 0;
        foreach (var range in _map.Ranges.Where(r => r.State == DiscSectorState.Untried).ToList())
        {
            var position = range.Start;
            while (position < range.End)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var n = (int)Math.Min(piece - (position % piece), range.End - position);
                Report(DiscCopyPhase.FirstPass, sector: position, piece: piece);
                if (Read(position, n, DiscSectorState.ReadNormally))
                {
                    position += n;
                    if (++goodInARow >= _settings.GoodReadsBeforeJumpReset)
                    {
                        jump = _settings.FirstJump;
                    }

                    continue;
                }

                goodInARow = 0;
                var skip = Math.Min(jump, range.End - position - n);
                if (skip > 0)
                {
                    _map.Mark(position + n, skip, DiscSectorState.Jumped);
                    Tell(DiscCopyNoteKind.JumpedAhead, $"Jumped {Mb(skip)} ahead, to sector {position + n + skip}.", position + n, skip);
                }

                position += n + skip;
                jump = Math.Min(jump * 2, _settings.LastJump);
                Save(force: true);
                await RestAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SweepsAsync(CancellationToken cancellationToken)
    {
        var sizes = _settings.PieceSizes;
        var smallestLevel = sizes.Count - 1;
        for (var sweep = Math.Max(_map.Sweep, 1); ; sweep++)
        {
            var resumeAt = sweep == _map.Sweep ? _map.SweepPosition : -1;
            _map.Sweep = sweep;
            _map.SweepPosition = resumeAt;
            var backwards = sweep % 2 == 0;
            var targets = _map.Ranges.Where(r => r.State.IsMissing()).ToList();
            if (targets.Count == 0)
            {
                return;
            }

            if (backwards)
            {
                targets.Reverse();
            }

            Tell(DiscCopyNoteKind.SweepStarted, $"Pass {sweep + 1}{(backwards ? ", from the end backwards" : string.Empty)}: {Mb(targets.Sum(r => r.Count))} missing{(resumeAt >= 0 ? string.Create(CultureInfo.InvariantCulture, $", going on from sector {resumeAt}") : string.Empty)}.");
            var level = 0;
            var good = 0;
            var deadInBlock = 0;
            var deadBlock = -1L;
            foreach (var target in targets)
            {
                var low = target.Start;
                var high = target.End;
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
                    cancellationToken.ThrowIfCancellationRequested();
                    var size = sizes[level];
                    var first = backwards ? Math.Max(low, (high - 1) / size * size) : low;
                    var n = (int)(backwards ? high - first : Math.Min(size - (low % size), high - low));
                    Report(DiscCopyPhase.Sweep, sector: first, piece: size, sweep: sweep, backwards: backwards);
                    if (Read(first, n, ReadState(level)))
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
                        if (level > 0 && ++good >= _settings.GoodReadsBeforeBigger[level])
                        {
                            level--;
                            good = 0;
                        }
                    }
                    else
                    {
                        good = 0;
                        if (n > 1)
                        {
                            // Read it again in pieces smaller than the one that failed.
                            do
                            {
                                level++;
                            }
                            while (level < smallestLevel && sizes[level] >= n);
                        }
                        else
                        {
                            if (backwards)
                            {
                                high--;
                            }
                            else
                            {
                                low++;
                            }

                            // When single sectors keep failing in one error-correction block, the drive can't correct the
                            // block at all: leave the rest of it for the next sweep instead of resting after each sector.
                            if (first / _settings.Block != deadBlock)
                            {
                                deadBlock = first / _settings.Block;
                                deadInBlock = 0;
                            }

                            if (++deadInBlock >= _settings.DeadBlockAfter)
                            {
                                var block = first / _settings.Block * _settings.Block;
                                var (from, to) = backwards ? (Math.Max(low, block), high) : (low, Math.Min(high, block + _settings.Block));
                                if (to > from)
                                {
                                    _map.Mark(from, to - from, DiscSectorState.Failed);
                                    Tell(DiscCopyNoteKind.DeadBlock, $"The block at sector {block} looks dead: its other {to - from} sectors wait for the next pass.", from, to - from);
                                    if (backwards)
                                    {
                                        high = from;
                                    }
                                    else
                                    {
                                        low = to;
                                    }
                                }

                                deadInBlock = 0;
                            }
                        }

                        _map.SweepPosition = backwards ? high : low;
                        Save(force: true);
                        await RestAsync(cancellationToken).ConfigureAwait(false);
                    }

                    _map.SweepPosition = backwards ? high : low;
                }
            }

            _map.SweepPosition = -1;
        }
    }

    private DiscSectorState ReadState(int level) =>
        _map.Cleanings > 0 ? DiscSectorState.ReadAfterCleaning
        : level == 0 ? DiscSectorState.ReadLater
        : level == _settings.PieceSizes.Count - 1 ? DiscSectorState.ReadBySector
        : DiscSectorState.ReadInPieces;

    /// <summary>Reads sectors once and writes them to the image. On a failure, marks them failed and returns false.</summary>
    private bool Read(long sector, int count, DiscSectorState readAs)
    {
        var data = _buffer.AsSpan(0, count * _sectorSize);
        var started = _clock.Elapsed;
        var read = _drive.TryReadForCopy(sector, data, out var error);
        var took = _clock.Elapsed - started;
        if (read)
        {
            _image.Position = sector * _sectorSize;
            _image.Write(data);
            _map.Mark(sector, count, readAs);
            if (Feed is not null)
            {
                // Into the file before the install that follows reads it there.
                _image.Flush();
                Feed.Wrote(sector, count);
            }
            if (took > TimeSpan.Zero)
            {
                var speed = data.Length / took.TotalSeconds;
                _bytesPerSecond = _bytesPerSecond == 0 ? speed : (0.8 * _bytesPerSecond) + (0.2 * speed);
            }

            if (took >= _settings.SlowRead)
            {
                Tell(DiscCopyNoteKind.SlowRead, string.Create(CultureInfo.InvariantCulture, $"Slow read: {count} sectors at {sector} took {took.TotalSeconds:0.0} s."), sector, count, took);
            }

            CheckStall();
            Save(force: false);
            return true;
        }

        Failures++;
        Feed?.Break();
        _map.Mark(sector, count, DiscSectorState.Failed);
        Tell(DiscCopyNoteKind.ReadFailed, string.Create(CultureInfo.InvariantCulture, $"Failed to read {count} sectors at {sector} in {took.TotalSeconds:0.0} s: {error}"), sector, count, took);
        CheckStall();
        return false;
    }

    /// <summary>
    /// Waits until the drive reads a sector it read before, or goes on after <see cref="DiscCopierSettings.RestLimit"/>.
    /// When the drive is back at the first check, the next first wait is shorter; for each check it fails, longer.
    /// </summary>
    private async Task RestAsync(CancellationToken cancellationToken)
    {
        var started = _clock.Elapsed;
        var waited = _firstRest;
        Report(DiscCopyPhase.Resting, force: true, restLeft: waited);
        await _clock.DelayAsync(waited, cancellationToken).ConfigureAwait(false);
        var failedChecks = 0;
        while (!Probe())
        {
            failedChecks++;
            if (_clock.Elapsed - started >= _settings.RestLimit)
            {
                Tell(DiscCopyNoteKind.DriveStillRefusing, "The drive still refuses to read; going on anyway.");
                break;
            }

            Report(DiscCopyPhase.Resting, force: true, restLeft: _settings.RestCheckEvery);
            await _clock.DelayAsync(_settings.RestCheckEvery, cancellationToken).ConfigureAwait(false);
        }

        // A first wait that was too short once is not tried again.
        if (failedChecks > 0)
        {
            _restFloor = Min(Max(_restFloor, waited + _settings.RestChange), _settings.LongestRest);
        }

        _firstRest = failedChecks == 0
            ? Max(_firstRest - _settings.RestChange, _restFloor)
            : Min(Max(_firstRest + (_settings.RestChange * failedChecks), _restFloor), _settings.LongestRest);
        var rested = _clock.Elapsed - started;
        Rested += rested;
        Tell(DiscCopyNoteKind.Rested, string.Create(CultureInfo.InvariantCulture, $"Rested the drive {rested.TotalSeconds:0} s ({failedChecks} failed checks)."), took: rested);
    }

    /// <summary>Reads a sector the copy already has: true when the drive reads again.</summary>
    private bool Probe()
    {
        var known = _map.StateAt(DiscSectors.VolumeDescriptor).IsRead() ? DiscSectors.VolumeDescriptor : _map.FirstReadSector();
        return _drive.TryRead(known >= 0 ? known : DiscSectors.VolumeDescriptor, _probe, out _);
    }

    private void CheckStall()
    {
        var read = _map.ReadSectors;
        var now = _clock.Elapsed;
        if (read - _stallRead >= _settings.StallSectors || _map.Sweep == 0)
        {
            _stallSince = now;
            _stallRead = read;
            _stalled = false;
            return;
        }

        if (!_stalled && now - _stallSince >= _settings.StallTime)
        {
            _stalled = true;
            Tell(DiscCopyNoteKind.Stalled, string.Create(CultureInfo.InvariantCulture, $"Under {Mb(_settings.StallSectors)} read in {_settings.StallTime.TotalMinutes:0} minutes: cleaning the disc is likely to help."));
            Report(_phase, force: true);
        }
    }

    private void Save(bool force)
    {
        var now = _clock.Elapsed;
        if (!force && now - _lastSave < _settings.SaveEvery)
        {
            return;
        }

        // The image first, so the map never claims sectors the image doesn't hold yet.
        _image.Flush();
        _saveMap(_map);
        _lastSave = now;
    }

    private void Tell(DiscCopyNoteKind kind, string message, long sector = -1, long count = 0, TimeSpan took = default) =>
        Note?.Invoke(new DiscCopyNote(kind, message) { Sector = sector, Count = count, Took = took });

    private void Report(DiscCopyPhase phase, bool force = false, long sector = -1, int piece = 0, int sweep = -1, bool backwards = false, TimeSpan restLeft = default)
    {
        var now = _clock.Elapsed;
        var changed = phase != _phase;
        _phase = phase;
        if (sweep >= 0)
        {
            _backwards = backwards;
        }

        _sector = sector >= 0 ? sector : _sector;
        _piece = piece > 0 ? piece : _piece;
        if (Progress is null || (!force && !changed && now - _lastStatus < _settings.StatusEvery))
        {
            return;
        }

        // Built only when sent: it counts the map and copies its stretches for the page's disc map.
        _lastStatus = now;
        Progress.Report(new DiscCopyStatus
        {
            Phase = phase,
            Sweep = _map.Sweep,
            Backwards = _backwards,
            Sector = _sector,
            PieceSectors = _piece,
            TotalSectors = _map.Sectors,
            ReadSectors = _map.ReadSectors,
            MissingSectors = _map.MissingSectors,
            UntriedSectors = _map.UntriedSectors,
            BytesPerSecond = _bytesPerSecond,
            RestLeft = restLeft,
            Failures = Failures,
            Stalled = _stalled,
            Cleanings = _map.Cleanings,
            CleanedRingsMm = [.. _map.CleanedRingsMm],
            Ranges = [.. _map.Ranges],
        });
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static string Mb(long sectors) => string.Create(CultureInfo.InvariantCulture, $"{sectors * DiscSectors.Size / 1_000_000.0:0.0} MB");
}
