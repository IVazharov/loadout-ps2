using System.Runtime.Versioning;

namespace Loadout.Discs;

/// <summary>A disc found in a drive: what it is and, for a PS2 disc, which game.</summary>
public sealed record DiscInDrive(string Drive, DiscMedia Media, long Sectors, int Layers, long Layer0Sectors, string? VolumeLabel, DiscIdentity? Identity)
{
    /// <summary>A PS2 game disc: its SYSTEM.CNF names the game's disc ID.</summary>
    public bool IsPs2Game => Identity?.DiscId is not null;

    public long Bytes => Sectors * DiscSectors.Size;
}

/// <summary>The PC's optical drives and the discs in them.</summary>
[SupportedOSPlatform("windows")]
public static class DiscDrives
{
    /// <summary>The optical drives, such as "E:", whether or not they hold a disc.</summary>
    public static IReadOnlyList<string> List() =>
        [.. DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom).Select(d => d.Name[..2].ToUpperInvariant())];

    /// <summary>
    /// A cheap sign of what is in a drive, from what Windows already knows: empty when there is no disc, and otherwise
    /// its label and size. Reading the disc itself (<see cref="Look"/>) waits until this changes.
    /// </summary>
    public static string Fingerprint(string drive)
    {
        try
        {
            var info = new DriveInfo(drive);
            return info.IsReady ? $"{info.VolumeLabel}|{info.TotalSize}" : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>Reads what disc is in a drive, or null when it has none it can read.</summary>
    public static DiscInDrive? Look(string drive)
    {
        try
        {
            using var reader = WindowsDiscDrive.Open(drive);
            string? label = null;
            try
            {
                label = new DriveInfo(drive).VolumeLabel;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A disc with no file system Windows knows has no label; it is still a disc.
            }

            return new DiscInDrive(reader.Name, reader.Media, reader.Sectors, reader.Layers, reader.Layer0Sectors, label, DiscIdentity.Read(reader));
        }
        catch (IOException)
        {
            return null;
        }
    }
}
