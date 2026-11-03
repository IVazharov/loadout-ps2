using System.Buffers.Binary;
using System.Text;

namespace Loadout.Bay;

/// <summary>
/// The HDLoader header of a game partition, 1024 bytes at 1 MB + 4 KB into the main partition.
/// </summary>
public sealed record HdlGameHeader(
    string Title,
    string StartupFileName,
    byte HdlCompatibilityFlags,
    byte CompatibilityModeFlags,
    byte TransferType,
    byte TransferMode,
    uint DiscType,
    uint Layer1Start,
    uint Sectors)
{
    public const int Bytes = 1024;

    public const uint Magic = 0xDEADFEED;

    private const int TitleOffset = 8;
    private const int TitleBytes = 160;
    private const int StartupOffset = 172;
    private const int StartupBytes = 60;
    private const int PartitionCountOffset = 240;
    private const int PartitionSpecsOffset = 244;
    private const int PartitionSpecBytes = 12;
    private const int MaxPartitionSpecs = 65;
    private const int CdSectorBytes = 2048;

    /// <summary>
    /// Where each part of the game lies on the disk, in game order: the main partition first,
    /// then its sub-partitions.
    /// </summary>
    public IReadOnlyList<HdlGamePart> Parts { get; init; } = [];

    /// <summary>Reads a header, or returns null when it does not start with the HDLoader magic number.</summary>
    public static HdlGameHeader? TryRead(ReadOnlySpan<byte> header)
    {
        if (header.Length != Bytes || BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
        {
            return null;
        }

        // The game size is the sum of its parts, each in bytes, counted in 2048-byte CD sectors.
        var parts = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(header[PartitionCountOffset..]), 0, MaxPartitionSpecs);
        uint sectors = 0;
        var specs = new HdlGamePart[parts];
        for (var i = 0; i < parts; i++)
        {
            var spec = header.Slice(PartitionSpecsOffset + (i * PartitionSpecBytes), PartitionSpecBytes);
            specs[i] = new HdlGamePart(
                BinaryPrimitives.ReadUInt32LittleEndian(spec),
                BinaryPrimitives.ReadUInt32LittleEndian(spec[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(spec[8..]));
            sectors += specs[i].Bytes / CdSectorBytes;
        }

        return new HdlGameHeader(
            Text(header.Slice(TitleOffset, TitleBytes)),
            Text(header.Slice(StartupOffset, StartupBytes)),
            header[168],
            header[169],
            header[170],
            header[171],
            BinaryPrimitives.ReadUInt32LittleEndian(header[236..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[232..]),
            sectors)
        {
            Parts = specs,
        };
    }

    // The parts are compared item by item, so two reads of the same header are equal.
    public bool Equals(HdlGameHeader? other) =>
        other is not null
        && Title == other.Title
        && StartupFileName == other.StartupFileName
        && HdlCompatibilityFlags == other.HdlCompatibilityFlags
        && CompatibilityModeFlags == other.CompatibilityModeFlags
        && TransferType == other.TransferType
        && TransferMode == other.TransferMode
        && DiscType == other.DiscType
        && Layer1Start == other.Layer1Start
        && Sectors == other.Sectors
        && Parts.SequenceEqual(other.Parts);

    public override int GetHashCode() => HashCode.Combine(Title, StartupFileName, Sectors, Parts.Count);

    private static string Text(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? field[..end] : field);
    }
}

/// <summary>
/// One part of a game (<c>part_specs</c>): its first game sector (2048 bytes), its first sector on the
/// disk (512 bytes, after the part's reserved area), and its size in bytes.
/// </summary>
public sealed record HdlGamePart(uint GameSector, uint DiskSector, uint Bytes);
