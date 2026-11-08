namespace Loadout.Updates;

public class ReleaseNotesTextTests
{
    [Fact]
    public void AChangelogSectionBecomesHeadingsAndBullets()
    {
        const string notes = "### Added\r\n\r\n- An About page (Ctrl+6) with **Check for updates**.\r\n- Uses `conf_hdd.cfg`, see [the readme](https://github.com/x/y#z).\r\n\r\n### Fixed\r\n\r\n- One fix\r\n  that wraps.\r\n";

        var lines = ReleaseNotesText.Lines(notes);

        Assert.Equal(
            [
                new NoteLine(NoteLineKind.Heading, "Added"),
                new NoteLine(NoteLineKind.Bullet, "An About page (Ctrl+6) with Check for updates."),
                new NoteLine(NoteLineKind.Bullet, "Uses conf_hdd.cfg, see the readme."),
                new NoteLine(NoteLineKind.Heading, "Fixed"),
                new NoteLine(NoteLineKind.Bullet, "One fix that wraps."),
            ],
            lines);
    }

    [Fact]
    public void PlainTextStaysAndEmptyNotesGiveNoLines()
    {
        Assert.Equal([new NoteLine(NoteLineKind.Text, "Just a line.")], ReleaseNotesText.Lines("Just a line."));
        Assert.Empty(ReleaseNotesText.Lines(""));
        Assert.Empty(ReleaseNotesText.Lines(null));
    }
}
