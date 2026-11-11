using Loadout.Core;
using Loadout.Images;

namespace Loadout.Discs;

/// <summary>Installing a disc on the PS2 from its backup: the Install page's row for it.</summary>
public static class DiscBackupInstall
{
    /// <summary>
    /// The Install page's row for a disc whose copy is under way, Ready to install from the copy as it is read. Null for a
    /// two-layer DVD: its layers are only known for sure from the copy's file once it is complete.
    /// </summary>
    public static QueuePreviewItem? FollowingCopy(DiscBackupFiles files, string title, DiscId discId, DiscMedia media, long sectors, int layers)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (layers > 1 || sectors <= 0 || sectors > uint.MaxValue)
        {
            return null;
        }

        var kind = media == DiscMedia.Cd ? MediaKind.Cd : MediaKind.Dvd5;
        return new QueuePreviewItem
        {
            SourceFile = files.Image,
            DataFile = files.Image,
            DiscId = discId.Canonical,
            Region = discId.Region,
            DetectedTitle = TitleText.FitQueueTitle(title, discId.Canonical),
            Media = kind,
            MediaLabel = QueuePreviewItem.DescribeMedia(kind, 1, 1),
            Layer0Sectors = (uint)sectors,
            HdlDiscType = kind == MediaKind.Cd ? HdlDiscTypes.Ps2Cd : HdlDiscTypes.Ps2Dvd,
            VolumeSetSize = 1,
            VolumeSequence = 1,
            Preliminary = QueueItemStatus.Ready,
            Status = QueueItemStatus.Ready,
        };
    }

    /// <summary>The file the Install page scans for a finished copy: the cue sheet of a CD, the image of a DVD.</summary>
    public static string ScanPath(DiscBackupFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.IsCd ? files.Cue : files.Image;
    }
}
