namespace Loadout.Discs;

public class DiscCopierTests
{
    [Fact]
    public async Task ACleanDiscIsCopiedInTheFirstPass()
    {
        using var run = new CopyRun(2000);

        Assert.Equal(DiscCopyOutcome.Complete, await run.RunAsync());

        Assert.True(run.Map.IsComplete);
        Assert.Equal([new DiscMapRange(0, 2000, DiscSectorState.ReadNormally)], run.Map.Ranges);
        Assert.Equal(0, run.Drive.FailedReads);
        run.AssertImageHasEveryReadSector();
        Assert.Contains(run.Notes, n => n.Kind == DiscCopyNoteKind.Complete);
        Assert.Equal(DiscCopyPhase.Complete, run.Statuses[^1].Phase);
        Assert.Equal(2000, run.Statuses[^1].ReadSectors);
    }

    [Fact]
    public async Task WeakSectorsAreReadOnALaterPassInSmallerPieces()
    {
        using var run = new CopyRun(4000);
        run.Drive.Weak[700] = 1;
        foreach (var s in new long[] { 1500, 1501, 1502, 1503 })
        {
            run.Drive.Weak[s] = 2;
        }

        Assert.Equal(DiscCopyOutcome.Complete, await run.RunAsync());

        run.AssertImageHasEveryReadSector();
        Assert.True(run.Map.IsComplete);
        Assert.DoesNotContain(run.Map.Ranges, r => !r.State.IsRead());
        Assert.Contains(run.Map.Ranges, r => r.State == DiscSectorState.ReadLater);
        Assert.Contains(run.Map.Ranges, r => r.State is DiscSectorState.ReadInPieces or DiscSectorState.ReadBySector);
        Assert.True(run.Copier!.Rested > TimeSpan.Zero);
        Assert.Equal(run.Drive.FailedReads, run.Copier.Failures);
    }

    [Fact]
    public async Task TheFirstPassJumpsAheadAfterAFailedRead()
    {
        using var run = new CopyRun(10_000);
        run.Drive.Dead.Add(1000);
        run.StopWhen = note => note.Kind == DiscCopyNoteKind.SweepStarted;

        Assert.Equal(DiscCopyOutcome.Stopped, await run.RunAsync());

        // The piece holding sector 1000 failed, and the next 1 MB was jumped over; everything else was read.
        Assert.Equal(DiscSectorState.Failed, run.Map.StateAt(1000));
        Assert.Equal(128, run.Map.Count(DiscSectorState.Failed));
        Assert.Equal(512, run.Map.Count(DiscSectorState.Jumped));
        Assert.Equal(DiscSectorState.Jumped, run.Map.StateAt(1024));
        Assert.Equal(DiscSectorState.ReadNormally, run.Map.StateAt(1536));
        Assert.Equal(1, run.Drive.FailedReads);
        Assert.Contains(run.Notes, n => n.Kind == DiscCopyNoteKind.JumpedAhead && n.Sector == 1024 && n.Count == 512);
    }

    [Fact]
    public async Task ADeadSectorNeverStopsTheCopyByItself()
    {
        using var run = new CopyRun(4000);
        run.Drive.Dead.Add(1000);
        run.StopWhen = note => note.Kind == DiscCopyNoteKind.SweepStarted && run.Map.Sweep >= 5;

        Assert.Equal(DiscCopyOutcome.Stopped, await run.RunAsync());

        Assert.Equal(1, run.Map.MissingSectors);
        Assert.Equal(DiscSectorState.Failed, run.Map.StateAt(1000));
        run.AssertImageHasEveryReadSector();
        Assert.Contains(run.Notes, n => n.Kind == DiscCopyNoteKind.SweepStarted && n.Message.Contains("from the end backwards", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeadBlockIsLeftForTheNextPassAfterThreeFailedSectors()
    {
        using var run = new CopyRun(4000);
        for (var s = 1008L; s < 1024; s++)
        {
            run.Drive.Dead.Add(s);
        }

        run.StopWhen = note => note.Kind == DiscCopyNoteKind.SweepStarted && run.Map.Sweep == 2;

        Assert.Equal(DiscCopyOutcome.Stopped, await run.RunAsync());

        var singlesInBlock = run.Drive.Log.Count(r => r.Count == 1 && r.Sector is >= 1008 and < 1024);
        Assert.Equal(3, singlesInBlock);
        Assert.Contains(run.Notes, n => n.Kind == DiscCopyNoteKind.DeadBlock && n.Sector == 1011 && n.Count == 13);
        Assert.Equal(16, run.Map.MissingSectors);
    }

    [Fact]
    public async Task AStoppedCopyGoesOnWithoutReadingAgainWhatItHas()
    {
        var clock = new FakeDiscClock();
        var drive = new FakeDiscDrive(5000, clock);
        drive.Weak[3000] = 1;
        using var image = new MemoryStream();
        using var first = new CopyRun(drive, clock, image, DiscCopyMap.New(5000, "test"));
        drive.AfterRead = reads =>
        {
            if (reads == 12)
            {
                first.Stop();
            }
        };

        Assert.Equal(DiscCopyOutcome.Stopped, await first.RunAsync());
        var saved = first.SavedText!;
        Assert.InRange(DiscCopyMap.Parse(saved).ReadSectors, 1000, 2000);

        drive.AfterRead = null;
        var readsBefore = drive.Log.Count;
        using var second = new CopyRun(drive, clock, image, DiscCopyMap.Parse(saved));
        Assert.Equal(DiscCopyOutcome.Complete, await second.RunAsync());

        second.AssertImageHasEveryReadSector();
        var readAgain = drive.Log.Skip(readsBefore).Where(r => r.Ok && !(r.Count == 1 && r.Sector == DiscSectors.VolumeDescriptor)).Sum(r => (long)r.Count);
        Assert.Equal(5000 - DiscCopyMap.Parse(saved).ReadSectors, readAgain);
    }

    [Fact]
    public async Task AStoppedSweepGoesOnFromWhereItWas()
    {
        var clock = new FakeDiscClock();
        var drive = new FakeDiscDrive(4000, clock);
        drive.Dead.Add(100);
        drive.Dead.Add(3900);
        using var image = new MemoryStream();
        using var first = new CopyRun(drive, clock, image, DiscCopyMap.New(4000, "test"));
        var inSecondSweep = 0;
        first.StopWhen = note => note.Kind == DiscCopyNoteKind.ReadFailed && first.Map.Sweep == 2 && ++inSecondSweep == 2;

        Assert.Equal(DiscCopyOutcome.Stopped, await first.RunAsync());
        var saved = DiscCopyMap.Parse(first.SavedText!);
        Assert.Equal(2, saved.Sweep);
        var position = saved.SweepPosition;
        Assert.True(position >= 0);

        using var second = new CopyRun(drive, clock, image, saved);
        second.StopWhen = note => note.Kind == DiscCopyNoteKind.SweepStarted && second.Map.Sweep == 3;
        Assert.Equal(DiscCopyOutcome.Stopped, await second.RunAsync());

        var resumed = Assert.Single(second.Notes, n => n.Kind == DiscCopyNoteKind.SweepStarted && n.Message.StartsWith("Pass 3", StringComparison.Ordinal));
        Assert.Contains($"going on from sector {position}", resumed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStallIsToldAndCleaningLetsTheRestBeRead()
    {
        var clock = new FakeDiscClock();
        var drive = new FakeDiscDrive(6000, clock);
        for (var s = 2000L; s < 2100; s++)
        {
            drive.Dirty.Add(s);
        }

        using var image = new MemoryStream();
        using var first = new CopyRun(drive, clock, image, DiscCopyMap.New(6000, "test"));
        first.StopWhen = note => note.Kind == DiscCopyNoteKind.Stalled;

        Assert.Equal(DiscCopyOutcome.Stopped, await first.RunAsync());
        Assert.Contains(first.Statuses, s => s.Stalled);
        Assert.All(Enumerable.Range(2000, 100), s => Assert.False(first.Map.StateAt(s).IsRead()));

        drive.Clean();
        var map = DiscCopyMap.Parse(first.SavedText!);
        map.StartOver(cleaned: true);
        using var second = new CopyRun(drive, clock, image, map);
        Assert.Equal(DiscCopyOutcome.Complete, await second.RunAsync());

        second.AssertImageHasEveryReadSector();
        Assert.Equal(1, second.Map.Cleanings);
        Assert.Equal(DiscSectorState.ReadAfterCleaning, second.Map.StateAt(2050));
        Assert.Equal(DiscSectorState.ReadNormally, second.Map.StateAt(1000));
        Assert.DoesNotContain(second.Statuses, s => s.Stalled);
    }

    [Fact]
    public async Task ADriveThatStillRefusesReadsIsWaitedForAtTheStart()
    {
        using var run = new CopyRun(1000);
        run.Drive.RefuseFor(TimeSpan.FromSeconds(45));

        Assert.Equal(DiscCopyOutcome.Complete, await run.RunAsync());

        Assert.Contains(run.Notes, n => n.Kind == DiscCopyNoteKind.WaitingForDrive);
        Assert.Contains(run.Statuses, s => s.Phase == DiscCopyPhase.WaitingForDrive);
        Assert.Equal(0, run.Copier!.Failures);
        run.AssertImageHasEveryReadSector();
    }

    [Fact]
    public async Task RestsGetShorterWhileTheDriveIsBackSooner()
    {
        var clock = new FakeDiscClock();
        var drive = new FakeDiscDrive(20_000, clock) { Lockout = TimeSpan.FromSeconds(4) };
        foreach (var s in new long[] { 1000, 5000, 9000, 13_000, 17_000 })
        {
            drive.Weak[s] = 1;
        }

        using var image = new MemoryStream();
        using var run = new CopyRun(drive, clock, image, DiscCopyMap.New(20_000, "test"));

        Assert.Equal(DiscCopyOutcome.Complete, await run.RunAsync());

        // Each rest is the first wait, as the drive was back by then: 5 s less each time, down to 10 s.
        Assert.Equal([30, 25, 20, 15, 10], RestSeconds(run));
    }

    [Fact]
    public async Task ADriveThatNeedsLongerRestsGetsThem()
    {
        var clock = new FakeDiscClock();
        var drive = new FakeDiscDrive(20_000, clock) { Lockout = TimeSpan.FromSeconds(45) };
        foreach (var s in new long[] { 1000, 5000, 9000 })
        {
            drive.Weak[s] = 1;
        }

        using var image = new MemoryStream();
        using var run = new CopyRun(drive, clock, image, DiscCopyMap.New(20_000, "test"));

        Assert.Equal(DiscCopyOutcome.Complete, await run.RunAsync());

        // The drive is back 45 s after a failure. The first rest checks at 30, 40 and 50 s; the next first wait is 40 s
        // (checks at 40 and 50 s); after that it is 45 s, which is enough, and never goes below it again.
        Assert.Equal([50, 50, 45], RestSeconds(run));
    }

    private static List<double> RestSeconds(CopyRun run) =>
        [.. run.Notes.Where(n => n.Kind == DiscCopyNoteKind.Rested).Select(n => Math.Round(n.Took.TotalSeconds))];

    [Fact]
    public void AMapForAnotherDiscIsRefused()
    {
        var clock = new FakeDiscClock();
        using var drive = new FakeDiscDrive(1000, clock);
        using var image = new MemoryStream();

        Assert.Throws<ArgumentException>(() => new DiscCopier(drive, image, DiscCopyMap.New(999, "test"), _ => { }));
    }

    /// <summary>A copy of a fake disc, with what it noted, reported and saved.</summary>
    private sealed class CopyRun : IDisposable
    {
        private readonly FakeDiscClock _clock;
        private readonly Stream _image;
        private readonly MemoryStream? _ownImage;
        private readonly CancellationTokenSource _stop = new();

        public CopyRun(long sectors)
        {
            _clock = new FakeDiscClock();
            Drive = new FakeDiscDrive(sectors, _clock);
            _ownImage = new MemoryStream();
            _image = _ownImage;
            Map = DiscCopyMap.New(sectors, "test");
        }

        public CopyRun(FakeDiscDrive drive, FakeDiscClock clock, Stream image, DiscCopyMap map)
        {
            _clock = clock;
            Drive = drive;
            _image = image;
            Map = map;
        }

        public FakeDiscDrive Drive { get; }

        public DiscCopyMap Map { get; }

        public DiscCopier? Copier { get; private set; }

        public List<DiscCopyNote> Notes { get; } = [];

        public List<DiscCopyStatus> Statuses { get; } = [];

        public string? SavedText { get; private set; }

        public Func<DiscCopyNote, bool>? StopWhen { get; set; }

        public void Stop() => _stop.Cancel();

        public void Dispose()
        {
            _stop.Dispose();
            _ownImage?.Dispose();
        }

        public async Task<DiscCopyOutcome> RunAsync()
        {
            Copier = new DiscCopier(Drive, _image, Map, map => SavedText = map.Format(), clock: _clock)
            {
                Note = note =>
                {
                    Notes.Add(note);
                    if (StopWhen?.Invoke(note) == true)
                    {
                        _stop.Cancel();
                    }
                },
                Progress = new Recorder(Statuses),
            };
            return await Copier.RunAsync(_stop.Token);
        }

        public void AssertImageHasEveryReadSector()
        {
            Assert.Equal(Map.Sectors * DiscSectors.Size, _image.Length);
            var bytes = new byte[DiscSectors.Size];
            foreach (var range in Map.Ranges.Where(r => r.State.IsRead()))
            {
                for (var s = range.Start; s < range.End; s++)
                {
                    _image.Position = s * DiscSectors.Size;
                    _image.ReadExactly(bytes);
                    Assert.True(bytes.AsSpan().SequenceEqual(Drive.Expected(s, 1)), $"sector {s} differs");
                }
            }
        }
    }

    /// <summary>Records statuses as they come, on the copier's thread (Progress&lt;T&gt; would post them later).</summary>
    private sealed class Recorder(List<DiscCopyStatus> statuses) : IProgress<DiscCopyStatus>
    {
        public void Report(DiscCopyStatus value) => statuses.Add(value);
    }
}
