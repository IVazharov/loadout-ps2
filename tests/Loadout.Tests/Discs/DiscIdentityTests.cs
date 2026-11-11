using Loadout.Core;

namespace Loadout.Discs;

public class DiscIdentityTests
{
    [Fact]
    public void TheDiscIdComesFromSystemCnfOnTheDisc()
    {
        using var drive = Disc("THE MUMMY", "BOOT2 = cdrom0:\\SLES_505.10;1\nVER = 1.00\n");

        var identity = DiscIdentity.Read(drive);

        Assert.NotNull(identity);
        Assert.Equal("SLES-50510", identity.DiscId?.Canonical);
        Assert.Equal(drive.Sectors, identity.Sectors);
        Assert.Matches("^[0-9a-f]{16}$", identity.VolumeHash);
    }

    [Fact]
    public void AnIdentityReadsBackFromItsLine()
    {
        using var drive = Disc("PROSTREET", "BOOT2 = cdrom0:\\SLES_550.02;1\n");
        var identity = DiscIdentity.Read(drive)!;

        Assert.True(DiscIdentity.TryParse(identity.Format(), out var copy));
        Assert.Equal(identity, copy);
        Assert.StartsWith("SLES-55002 ", identity.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADiscWithoutADiscIdStillHasAnIdentity()
    {
        using var drive = Disc("DEMO", systemCnf: null);

        var identity = DiscIdentity.Read(drive)!;

        Assert.Null(identity.DiscId);
        Assert.StartsWith("- ", identity.Format(), StringComparison.Ordinal);
        Assert.True(DiscIdentity.TryParse(identity.Format(), out var copy));
        Assert.Equal(identity, copy);
    }

    [Fact]
    public void TheSameDiscIsKnownAgainEvenIfItsIdCouldNotBeRead()
    {
        Assert.True(DiscId.TryParse("SLES-50510", out var id));
        var full = new DiscIdentity(id, 481_712, "0123456789abcdef");

        Assert.True(full.IsSameDisc(full with { DiscId = null }));
        Assert.False(full.IsSameDisc(full with { VolumeHash = "fedcba9876543210" }));
        Assert.False(full.IsSameDisc(full with { Sectors = 481_713 }));
        Assert.True(DiscId.TryParse("SLES-50511", out var other));
        Assert.False(full.IsSameDisc(full with { DiscId = other }));
    }

    [Fact]
    public void DifferentDiscsHaveDifferentHashes()
    {
        using var first = Disc("GAME ONE", "BOOT2 = cdrom0:\\SLUS_200.62;1\n");
        using var second = Disc("GAME TWO", "BOOT2 = cdrom0:\\SLUS_200.62;1\n");

        Assert.False(DiscIdentity.Read(first)!.IsSameDisc(DiscIdentity.Read(second)!));
    }

    [Fact]
    public void AnUnreadableVolumeDescriptorGivesNoIdentity()
    {
        using var drive = Disc("THE MUMMY", "BOOT2 = cdrom0:\\SLES_505.10;1\n");
        drive.Dead.Add(DiscSectors.VolumeDescriptor);

        Assert.Null(DiscIdentity.Read(drive));
    }

    [Theory]
    [InlineData("")]
    [InlineData("SLES-50510 481712")]
    [InlineData("SLES-5051 481712 abc")]
    [InlineData("SLES-50510 many abc")]
    [InlineData("SLES-50510 481712 ")]
    public void ABrokenLineIsNotAnIdentity(string text) => Assert.False(DiscIdentity.TryParse(text, out _));

    private static FakeDiscDrive Disc(string label, string? systemCnf)
    {
        var image = DiscImageBuilder.Build(label, systemCnf, extraSectors: 12);
        return new FakeDiscDrive(image.Length / DiscSectors.Size, new FakeDiscClock(), image);
    }
}
