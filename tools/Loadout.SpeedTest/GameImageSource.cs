using Loadout.Bay;
using Loadout.Core;
using Loadout.Images;
using Loadout.Metadata;
using Loadout.Persistence;

namespace Loadout.SpeedTest;

/// <summary>A game image read the way the Install page reads it, ready to send to Loadout Bay.</summary>
public sealed record GameImageSource(BayInstallRequest Request, string DataFile)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>The game's 2048-byte sectors, from the first.</summary>
    public Stream Open() => IsoUserDataStream.Open(DataFile);

    /// <summary>Scans one image. Prints why and returns null when it cannot be installed.</summary>
    public static async Task<GameImageSource?> ScanAsync(string imagePath, string? title)
    {
        var work = Path.Combine(Path.GetTempPath(), "Loadout.SpeedTest");
        var options = new DiscScanOptions(Path.Combine(work, "cache"));
        var scan = await DiscScanner.ScanAsync([new ScanInput(imagePath, ScanDepth.File)], null, options);

        // As on the Install page, the OPL database's title replaces the one read from the disc.
        var catalog = new OplCfgTitleCatalog(Http, new DiscTitleCache(Path.Combine(work, "titles.sqlite")));
        var items = await QueueTitleLookup.ApplyAsync(scan.Items, catalog);
        var item = items.SingleOrDefault();
        if (item is null || item.DataFile is null || item.Status is not (QueueItemStatus.Ready or QueueItemStatus.NeedsMetadataReview))
        {
            Console.Error.WriteLine($"{imagePath} cannot be installed: {item?.StatusLabel ?? "not a game image"}. {item?.FriendlyDetail}");
            return null;
        }

        if (!DiscId.TryParse(item.DiscId, out var discId))
        {
            Console.Error.WriteLine($"{imagePath} has no disc ID, so it cannot be named the way HDLGameInstaller names games.");
            return null;
        }

        return new GameImageSource(
            new BayInstallRequest(
                discId.Canonical,
                discId.StartupFileName,
                title ?? item.DetectedTitle,
                item.Layer0Sectors,
                item.Layer1Sectors,
                item.HdlDiscType),
            item.DataFile);
    }
}
