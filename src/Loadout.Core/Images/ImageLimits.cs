namespace Loadout.Images;

/// <summary>
/// Image layouts Loadout can install. <see cref="IsoUserDataStream"/> reads one
/// image file: 2048-byte headerless sectors, or 2352-byte MODE1 (user data at
/// +16) or MODE2 (user data at +24) sectors, each reduced to 2048 bytes. A PS2
/// game partition holds one data track, so CD-DA audio cannot be kept.
/// </summary>
public static class ImageLimits
{
    public const string MultiTrack =
        "Unsupported: this multi-track image has audio tracks, and a PS2 game partition holds only the data track. "
        + "The audio tracks were not removed.";

    public const string MultipleFiles =
        "Unsupported: the CUE sheet lists more than one FILE, and Loadout reads one image file. The tracks were not merged or stripped.";

    public const string NotBinary =
        "Unsupported: Loadout only reads a BINARY image file. This CUE was not rewritten.";

    public const string BadMode =
        "Unsupported: Loadout only reads 2048-byte headerless sectors and 2352-byte MODE1 or MODE2 sectors. This layout was not rewritten.";

    public const string DataNotAtStart =
        "Unsupported: the data track does not start at the first byte of the file. This CUE was not rewritten.";
}
