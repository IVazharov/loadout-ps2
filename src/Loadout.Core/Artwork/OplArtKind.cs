namespace Loadout.Artwork;

/// <summary>Artwork files the OPL user guide says the loader displays.</summary>
public enum OplArtKind
{
    Cover,
    Icon,
    Background,
    Screenshot1,
    Screenshot2,
}

public static class OplArtCatalog
{
    public static readonly IReadOnlyList<OplArtKind> Kinds =
    [
        OplArtKind.Cover,
        OplArtKind.Icon,
        OplArtKind.Background,
        OplArtKind.Screenshot1,
        OplArtKind.Screenshot2,
    ];
}
