namespace Loadout.Discs;

public sealed class SimulatedDiscDrivesTests : IDisposable
{
    private const long Sectors = 4000;

    private static readonly DiscCopierSettings Quick = new()
    {
        FirstRest = TimeSpan.Zero,
        ShortestRest = TimeSpan.Zero,
        LongestRest = TimeSpan.Zero,
        RestChange = TimeSpan.Zero,
        RestCheckEvery = TimeSpan.FromMilliseconds(1),
        SaveEvery = TimeSpan.FromHours(1),
    };

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "loadout-sim-" + Guid.NewGuid().ToString("N"));
    private readonly string _image;
    private readonly byte[] _content = new byte[Sectors * DiscSectors.Size];

    public SimulatedDiscDrivesTests()
    {
        Directory.CreateDirectory(_folder);
        new Random(4000).NextBytes(_content);
        _image = Path.Combine(_folder, "disc.iso");
        File.WriteAllBytes(_image, _content);
    }

    public void Dispose() => TempDirectory.Delete(_folder);

    [Fact]
    public void ItReadsTheImageAndFailsOnDirtUntilTwoCleaningsClearIt()
    {
        var drives = Drives([(1000, 1100)]);
        using var reader = drives.Open("S:");
        var buffer = new byte[16 * DiscSectors.Size];

        Assert.True(reader.TryRead(16, buffer, out _));
        Assert.Equal(_content.AsSpan(16 * DiscSectors.Size, buffer.Length).ToArray(), buffer);

        // The weak band beside the dirt fails once, then reads.
        Assert.False(reader.TryRead(960, buffer, out _));
        Assert.True(reader.TryRead(960, buffer, out _));

        Assert.False(reader.TryRead(1000, buffer, out _));
        Assert.False(reader.TryRead(1048, buffer, out _));

        Eject(drives);
        Assert.Equal(1, drives.Cleanings);
        using var cleaned = drives.Open("S:");
        Assert.True(cleaned.TryRead(1000, buffer, out _));
        Assert.False(cleaned.TryRead(1048, buffer, out _));

        Eject(drives);
        using var clean = drives.Open("S:");
        Assert.True(clean.TryRead(1048, buffer, out _));
        Assert.Equal(_content.AsSpan(1048 * DiscSectors.Size, buffer.Length).ToArray(), buffer);
    }

    [Fact]
    public void AfterAFailedReadTheDriveRefusesForAMoment()
    {
        var drives = new SimulatedDiscDrives(_image, [(1000, 1100)]) { FailureCost = TimeSpan.Zero, Lockout = TimeSpan.FromMinutes(5), BytesPerSecond = 0 };
        using var reader = drives.Open("S:");
        var buffer = new byte[DiscSectors.Size];

        Assert.False(reader.TryRead(1000, buffer, out _));
        Assert.False(reader.TryRead(16, buffer, out var failure));
        Assert.Equal("The device is not ready.", failure);
        Assert.Null(drives.Look("S:")?.Identity);
    }

    [Fact]
    public void TheTrayOpensOnEjectAndClosesByItselfWithTheDiscCleaned()
    {
        var drives = new SimulatedDiscDrives(_image, []) { TrayOpenFor = TimeSpan.FromMinutes(5), BytesPerSecond = 0 };
        var before = drives.Fingerprint("S:");

        Assert.NotEmpty(before);
        Assert.Equal(["S:"], drives.List());
        Assert.True(drives.TryEject("S:"));
        Assert.Empty(drives.Fingerprint("S:"));
        Assert.Throws<IOException>(() => drives.Open("S:"));
        Assert.Null(drives.Look("S:"));
        Assert.False(drives.TryEject("E:"));
        Assert.Empty(drives.Fingerprint("E:"));
    }

    [Fact]
    public async Task ACopyOfTheSimulatedDiscCompletesAfterTwoCleaningsAndEqualsTheImage()
    {
        var drives = Drives([(1000, 1100), (3000, 3064)]);
        var map = DiscCopyMap.New(Sectors, "simulated");
        using var image = new MemoryStream();
        for (var run = 0; run < 3 && !map.IsComplete; run++)
        {
            using var stop = new CancellationTokenSource();
            using var reader = drives.Open("S:");
            var copier = new DiscCopier(reader, image, map, _ => { }, Quick)
            {
                // As a user would: clean once the passes stop getting anywhere.
                Note = note =>
                {
                    if (note.Kind == DiscCopyNoteKind.SweepStarted && map.Sweep >= 3)
                    {
                        stop.Cancel();
                    }
                },
            };
            if (await copier.RunAsync(stop.Token) == DiscCopyOutcome.Stopped)
            {
                Eject(drives);
                map.StartOver(cleaned: true);
            }
        }

        Assert.True(map.IsComplete);
        Assert.Equal(2, map.Cleanings);
        Assert.Equal(_content, image.ToArray());
        Assert.Contains(map.Ranges, r => r.State == DiscSectorState.ReadAfterCleaning);
        Assert.Contains(map.Ranges, r => r.State == DiscSectorState.ReadLater);
    }

    [Fact]
    public void DirtIsReadFromTheCommandLineOrPlacedWhereAScratchedDiscHasIt()
    {
        Assert.Equal([(568320L, 568720L), (1482880L, 1483280L)], SimulatedDiscDrives.ParseDirt("568320-568720, 1482880-1483280"));
        Assert.Empty(SimulatedDiscDrives.ParseDirt(""));
        Assert.Throws<FormatException>(() => SimulatedDiscDrives.ParseDirt("10-5"));

        var dirt = SimulatedDiscDrives.DefaultDirt(1_809_376);
        Assert.Equal(2, dirt.Count);
        Assert.InRange(dirt[0].From, 565_000, 570_000);
        Assert.InRange(dirt[1].From, 1_480_000, 1_486_000);
        Assert.All(dirt, d => Assert.Equal(0, d.From % 16));
    }

    private SimulatedDiscDrives Drives(IReadOnlyList<(long From, long To)> dirt) =>
        new(_image, dirt) { FailureCost = TimeSpan.Zero, Lockout = TimeSpan.Zero, BytesPerSecond = 0, TrayOpenFor = TimeSpan.Zero, WeakBand = 64 };

    // Ejects, and lets the tray close by itself: the drive notices when it is next looked at.
    private static void Eject(SimulatedDiscDrives drives)
    {
        Assert.True(drives.TryEject("S:"));
        Assert.NotEmpty(drives.Fingerprint("S:"));
    }
}
