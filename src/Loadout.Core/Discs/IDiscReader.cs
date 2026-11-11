namespace Loadout.Discs;

/// <summary>What kind of disc is in a drive.</summary>
public enum DiscMedia
{
    Unknown,
    Cd,
    Dvd,
}

/// <summary>A disc in a drive, read as 2048-byte sectors. Tests use a fake one.</summary>
public interface IDiscReader : IDisposable
{
    /// <summary>The drive as Windows names it, such as E:.</summary>
    string Name { get; }

    /// <summary>The disc's length in 2048-byte sectors.</summary>
    long Sectors { get; }

    DiscMedia Media { get; }

    /// <summary>A DVD's layers (1 or 2), or 0 when the drive did not say.</summary>
    int Layers { get; }

    /// <summary>Sectors on layer 0 of a two-layer DVD, or 0.</summary>
    long Layer0Sectors { get; }

    /// <summary>
    /// Reads whole sectors once, with no retry of its own. Returns false with the drive's message when any of them could
    /// not be read; the destination then holds nothing useful.
    /// </summary>
    bool TryRead(long sector, Span<byte> destination, out string? failure);

    /// <summary>
    /// The bytes a backup keeps of each sector: 2048 for a DVD (its .iso), 2352 for a CD, raw, as Redump keeps it in a .bin.
    /// </summary>
    int CopySectorSize { get; }

    /// <summary>Reads whole sectors once as the backup keeps them (<see cref="CopySectorSize"/> bytes each), with no retry.</summary>
    bool TryReadForCopy(long sector, Span<byte> destination, out string? failure);
}

public static class DiscSectors
{
    public const int Size = 2048;

    /// <summary>The disc's primary volume descriptor, which every PS2 disc has.</summary>
    public const long VolumeDescriptor = 16;
}
