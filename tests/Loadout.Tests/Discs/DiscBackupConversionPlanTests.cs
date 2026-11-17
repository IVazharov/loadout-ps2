namespace Loadout.Discs;

/// <summary>Convert to CHD and Convert to ISO/BIN on several picked copies: what each takes, what it leaves out and why, the room it needs.</summary>
public sealed class DiscBackupConversionPlanTests : IDisposable
{
    private const long Sectors = 10;

    private readonly string _folder = Directory.CreateTempSubdirectory("loadout-conversion-plan-").FullName;

    public void Dispose() => TempDirectory.Delete(_folder);

    [Fact]
    public void ConvertToChdTakesTheImagesInPickedOrderAndSaysWhyTheOthersAreLeftOut()
    {
        var iso = Copy("Iso game", DiscBackupFormat.Iso);
        var chd = Copy("Chd game", DiscBackupFormat.Chd, bytesOnDisk: 9_000);
        var bin = Copy("Cd game", DiscBackupFormat.BinCue, cd: true);
        var half = Copy("Half", DiscBackupFormat.Iso, DiscBackupResult.Incomplete);
        var busy = Copy("Busy", DiscBackupFormat.Iso);

        var plan = DiscBackupConversionPlan.For([iso, chd, bin, half, busy], toChd: true, b => b == busy);

        Assert.Equal([iso, bin], plan.Convert);
        Assert.Equal(
            [(chd, ConversionSkip.AlreadyThere), (half, ConversionSkip.Incomplete), (busy, ConversionSkip.Busy)],
            plan.Skipped.Select(s => (s.Backup, s.Why)));

        // A chd is counted as big as its image, as one can be.
        Assert.Equal(iso.BytesOnDisk + bin.BytesOnDisk, plan.BytesNeeded);
    }

    [Fact]
    public void ConvertToIsoBinTakesTheChdsAndCountsTheirWholeImages()
    {
        var dvd = Copy("Dvd chd", DiscBackupFormat.Chd, bytesOnDisk: 9_000);
        var cd = Copy("Cd chd", DiscBackupFormat.Chd, cd: true, bytesOnDisk: 9_000);
        var iso = Copy("Iso game", DiscBackupFormat.Iso);
        var bin = Copy("Cd game", DiscBackupFormat.BinCue, cd: true);

        var plan = DiscBackupConversionPlan.For([iso, dvd, bin, cd], toChd: false, _ => false);

        Assert.Equal([dvd, cd], plan.Convert);
        Assert.Equal([iso, bin], plan.Skipped.Select(s => s.Backup));
        Assert.All(plan.Skipped, s => Assert.Equal(ConversionSkip.AlreadyThere, s.Why));
        Assert.Equal((Sectors * DiscSectors.Size) + (Sectors * 2352), plan.BytesNeeded);
    }

    [Fact]
    public void AnImageWhoseChdStillWaitsToGoIsFinishedWithoutRoom()
    {
        var files = new DiscBackupFiles(_folder, "Converted back");
        File.WriteAllText(files.Image, "iso");
        File.WriteAllText(files.Chd, "chd");
        File.WriteAllText(files.ImagePending, DiscBackupFiles.ImageMadeMark);
        var copy = Copy(files.Name, DiscBackupFormat.Iso);

        Assert.True(files.ChdWaitsToGo());
        Assert.False(files.ChdIsTheCopy());
        var plan = DiscBackupConversionPlan.For([copy], toChd: false, _ => false);
        Assert.Equal([copy], plan.Convert);
        Assert.Equal(0, plan.BytesNeeded);

        // Without the mark, the chd is the copy and nothing waits.
        File.Delete(files.ImagePending);
        Assert.False(files.ChdWaitsToGo());
        Assert.True(files.ChdIsTheCopy());
    }

    [Fact]
    public void NothingPickedPlansNothing()
    {
        var plan = DiscBackupConversionPlan.For([], toChd: true, _ => false);

        Assert.Empty(plan.Convert);
        Assert.Empty(plan.Skipped);
        Assert.Equal(0, plan.BytesNeeded);
    }

    private DiscBackup Copy(string name, DiscBackupFormat format, DiscBackupResult result = DiscBackupResult.MatchesRedump, bool cd = false, long? bytesOnDisk = null)
    {
        var backup = new DiscBackup(new DiscBackupFiles(_folder, name, cd), null, Sectors, Sectors, 0, result, DateTime.UtcNow) { Format = format };
        return backup with { BytesOnDisk = bytesOnDisk ?? backup.Bytes };
    }
}
