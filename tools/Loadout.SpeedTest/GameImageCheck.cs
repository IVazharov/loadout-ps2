using System.Buffers.Binary;
using System.Text;
using Loadout.Bay;

namespace Loadout.SpeedTest;

/// <summary>
/// Checks an installed game in a raw PS2 disk image, such as PCSX2's DEV9hdd.raw: finds the
/// game's main partition by name in the APA partition list, reads its HDLoader header, and
/// compares every part the header names with the game's sectors, byte for byte.
/// </summary>
public static class GameImageCheck
{
    private const int SectorBytes = 512;
    private const uint ApaMagic = 0x00415041; // "APA\0"
    private const ushort ApaFlagSub = 0x0001;
    private const long HeaderOffset = 0x100000;

    // Reads on an open partition start 4 KB in, after the APA header, so the HDLoader header
    // is 1 MB + 4 KB from the partition's first sector.
    private const long HeaderStartSectors = 8;

    /// <summary>The first sector of the main partition with this name, or null when there is none.</summary>
    public static long? FindPartition(FileStream image, string partitionName)
    {
        var header = new byte[SectorBytes];
        long sector = 0;
        do
        {
            image.Position = sector * SectorBytes;
            image.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != ApaMagic)
            {
                throw new InvalidDataException($"No APA partition header at sector {sector}.");
            }

            var id = header.AsSpan(16, 32);
            var end = id.IndexOf((byte)0);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(74));
            if ((flags & ApaFlagSub) == 0 && Encoding.ASCII.GetString(end >= 0 ? id[..end] : id) == partitionName)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(64));
            }

            sector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        }
        while (sector != 0);

        return null;
    }

    /// <summary>Reads the game's HDLoader header, or null when the partition holds none.</summary>
    public static HdlGameHeader? ReadHeader(FileStream image, long partitionStart)
    {
        var header = new byte[HdlGameHeader.Bytes];
        image.Position = ((partitionStart + HeaderStartSectors) * SectorBytes) + HeaderOffset;
        image.ReadExactly(header);
        return HdlGameHeader.TryRead(header);
    }

    /// <summary>
    /// Returns the game byte where the disk first differs from <paramref name="sectors"/>, or null
    /// when every part matches. The parts must cover the game from its first sector, in order.
    /// </summary>
    public static long? FirstMismatch(FileStream image, HdlGameHeader header, Stream sectors)
    {
        var expected = new byte[1024 * 1024];
        var actual = new byte[expected.Length];
        long game = 0;
        foreach (var part in header.Parts)
        {
            if ((long)part.GameSector * 2048 != game)
            {
                return game;
            }

            image.Position = (long)part.DiskSector * SectorBytes;
            for (long done = 0; done < part.Bytes;)
            {
                var count = (int)Math.Min(expected.Length, part.Bytes - done);
                sectors.ReadExactly(expected, 0, count);
                image.ReadExactly(actual, 0, count);
                var same = actual.AsSpan(0, count).CommonPrefixLength(expected.AsSpan(0, count));
                if (same < count)
                {
                    return game + same;
                }

                done += count;
                game += count;
            }
        }

        return null;
    }
}
