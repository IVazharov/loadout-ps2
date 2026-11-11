using System.Runtime.Versioning;

namespace Loadout.Discs;

/// <summary>A disc found in a drive: what it is and, for a PS2 disc, which game.</summary>
public sealed record DiscInDrive(string Drive, DiscMedia Media, long Sectors, int Layers, long Layer0Sectors, string? VolumeLabel, DiscIdentity? Identity)
{
    /// <summary>What the drive says about its read speed for this disc, or null when it doesn't say.</summary>
    public DriveSpeeds? Speeds { get; init; }

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

            return new DiscInDrive(reader.Name, reader.Media, reader.Sectors, reader.Layers, reader.Layer0Sectors, label, DiscIdentity.Read(reader))
            {
                Speeds = reader.ReadSpeeds(),
            };
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>The disc drives Back up discs uses: Windows' own, or a simulated one for testing (<see cref="SimulatedDiscDrives"/>).</summary>
public interface IDiscDrives
{
    /// <summary>The drives, such as "E:", whether or not they hold a disc.</summary>
    IReadOnlyList<string> List();

    /// <summary>A cheap sign of what is in a drive: empty when there is no disc. The disc is read again when it changes.</summary>
    string Fingerprint(string drive);

    /// <summary>Reads what disc is in a drive, or null when it has none it can read.</summary>
    DiscInDrive? Look(string drive);

    /// <summary>Opens the disc in a drive. Throws <see cref="IOException"/> when the drive has no readable disc.</summary>
    IDiscReader Open(string drive);

    /// <summary>Opens the drive's tray. Returns false when it would not.</summary>
    bool TryEject(string drive);

    /// <summary>The copy's numbers for these drives, or null for the defaults, which suit a real drive.</summary>
    DiscCopierSettings? CopierSettings { get; }

    /// <summary>True for a simulated drive, which Windows knows nothing about (so there is no AutoPlay to hold back).</summary>
    bool IsSimulated { get; }
}

/// <summary>The PC's own optical drives.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDiscDrives : IDiscDrives
{
    public DiscCopierSettings? CopierSettings => null;

    public bool IsSimulated => false;

    public IReadOnlyList<string> List() => DiscDrives.List();

    public string Fingerprint(string drive) => DiscDrives.Fingerprint(drive);

    public DiscInDrive? Look(string drive) => DiscDrives.Look(drive);

    public IDiscReader Open(string drive) => WindowsDiscDrive.Open(drive);

    public bool TryEject(string drive) => WindowsDiscDrive.TryEject(drive);
}
