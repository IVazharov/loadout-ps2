using Loadout.Core;

namespace Loadout.Artwork;

public enum ArtworkFormat
{
    Png,
    Jpeg,
}

/// <summary>
/// Names checked on 2026-10-09.
/// The OPL 0.9.3 art guide (ps2homebrew.org, Using ARTs) uses
/// <c>&lt;game_ID&gt;_COV</c>, <c>_ICO</c>, <c>_BG</c>, <c>_SCR1</c>, and <c>_SCR2</c>.
/// Its examples are <c>SLES_544.39_COV.jpg</c>, <c>SLES_544.39_ICO.png</c>,
/// <c>SLES_544.39_BG.jpg</c>, <c>SLES_544.39_SCR1.jpg</c>, and <c>SLES_544.39_SCR2.jpg</c>.
/// Icon art is PNG. The guide allows JPG or PNG for the other four.
/// Current OPL master <c>4cc74baabb73be9ca43f817474b321cd70fbcc2d</c> still differs.
/// <c>hddGetImage</c> builds <c>{prefix}{folder}/{startup}_{suffix}</c> and calls
/// <c>texDiscoverLoad</c> with tex id <c>-1</c>, which opens only <c>.png</c>.
/// <c>misc/conf_theme_OPL.cfg</c> on that commit uses <c>pattern=SCR</c> and
/// <c>pattern=SCR2</c>, not <c>SCR1</c>. The first screenshot is therefore uploaded
/// as <c>&lt;id&gt;_SCR.png</c>. The same bytes are also uploaded as
/// <c>&lt;id&gt;_SCR1.png</c> because the 0.9.3 guide still asks for <c>_SCR1</c>.
/// A JPEG first screenshot keeps the guide name <c>_SCR1.jpg</c>. Current master does not open JPEG.
/// The Luden02 database keeps PNG files under <c>PS2/&lt;id&gt;/</c> with suffixes
/// <c>COV</c>, <c>ICO</c>, <c>BG_00</c>, <c>SCR_00</c>, and <c>SCR_01</c>.
/// </summary>
public static class OplArtNames
{
    public const string GuideCoverExample = "SLES_544.39_COV.jpg";

    public const string GuideIconExample = "SLES_544.39_ICO.png";

    public const string GuideBackgroundExample = "SLES_544.39_BG.jpg";

    public const string GuideScreenshot1Example = "SLES_544.39_SCR1.jpg";

    public const string GuideScreenshot2Example = "SLES_544.39_SCR2.jpg";

    /// <summary>First screenshot name current OPL master loads. PNG only.</summary>
    public const string CurrentLoaderScreenshotExample = "SLES_544.39_SCR.png";

    /// <summary>Checked against OPL master <c>4cc74baabb73be9ca43f817474b321cd70fbcc2d</c>.</summary>
    public const string CurrentOplMasterCommit = "4cc74baabb73be9ca43f817474b321cd70fbcc2d";

    /// <summary>Suffixes current OPL master appends after the startup name. PNG only.</summary>
    public static readonly IReadOnlyList<string> CurrentLoaderSuffixes = ["COV", "ICO", "BG", "SCR", "SCR2"];

    public static string FileName(DiscId discId, OplArtKind kind, ArtworkFormat format)
    {
        if (kind == OplArtKind.Icon && format != ArtworkFormat.Png)
        {
            throw new ArgumentException("The OPL icon file is PNG.", nameof(format));
        }

        var suffix = kind switch
        {
            OplArtKind.Cover => "COV",
            OplArtKind.Icon => "ICO",
            OplArtKind.Background => "BG",
            // Current master looks for _SCR.png. The 0.9.3 guide's JPEG name stays _SCR1.jpg.
            OplArtKind.Screenshot1 => format == ArtworkFormat.Png ? "SCR" : "SCR1",
            OplArtKind.Screenshot2 => "SCR2",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return Build(discId, suffix, format);
    }

    /// <summary>
    /// Remote names for one cached file. The first PNG screenshot is
    /// <c>_SCR.png</c> and <c>_SCR1.png</c>. Every other kind is one name.
    /// </summary>
    public static IReadOnlyList<string> UploadFileNames(DiscId discId, OplArtKind kind, ArtworkFormat format)
    {
        if (kind == OplArtKind.Screenshot1 && format == ArtworkFormat.Png)
        {
            return [Build(discId, "SCR", ArtworkFormat.Png), Build(discId, "SCR1", ArtworkFormat.Png)];
        }

        return [FileName(discId, kind, format)];
    }

    public static IReadOnlyList<string> LookupFileNames(DiscId discId, OplArtKind kind, ArtworkFormat format)
        => UploadFileNames(discId, kind, format);

    public static string QueueKey(OplArtKind kind, string remoteFileName, bool primary)
    {
        if (primary)
        {
            return kind.ToString();
        }

        return kind + ":" + SuffixOf(remoteFileName);
    }

    public static string SuffixOf(string remoteFileName)
    {
        var dot = remoteFileName.LastIndexOf('.');
        var stem = dot > 0 ? remoteFileName[..dot] : remoteFileName;
        var mark = stem.LastIndexOf('_');
        return mark < 0 ? stem : stem[(mark + 1)..];
    }

    private static string Build(DiscId discId, string suffix, ArtworkFormat format)
    {
        var extension = format == ArtworkFormat.Png ? "png" : "jpg";
        return discId.OplFileStem + "_" + suffix + "." + extension;
    }
}

/// <summary>
/// Paths inside the Luden02 <c>psx-ps2-opl-art-database</c> tree.
/// <c>PS2/SLES_538.86/</c> contains <c>SLES_538.86_COV.png</c>, <c>_ICO.png</c>,
/// <c>_BG_00.png</c>, and <c>_SCR_00.png</c>. Those are not the OPL ART names.
/// </summary>
public static class Luden02ArtworkLayout
{
    public const string DefaultBaseUrl = "https://raw.githubusercontent.com/Luden02/psx-ps2-opl-art-database/main/";

    public static string SourceFileName(DiscId discId, OplArtKind kind)
    {
        var suffix = kind switch
        {
            OplArtKind.Cover => "COV",
            OplArtKind.Icon => "ICO",
            OplArtKind.Background => "BG_00",
            OplArtKind.Screenshot1 => "SCR_00",
            OplArtKind.Screenshot2 => "SCR_01",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return discId.OplFileStem + "_" + suffix + ".png";
    }

    public static string RelativePath(DiscId discId, OplArtKind kind)
        => "PS2/" + discId.OplFileStem + "/" + SourceFileName(discId, kind);
}
