using System.Buffers.Binary;
using System.Text;

namespace Loadout.Fakes;

internal static class DiscImageBuilder
{
    public const int UserSectorSize = 2048;

    public const uint BaseSectors = 20;

    public static byte[] Build(
        string volumeLabel,
        string? systemCnf,
        ushort volumeSetSize = 1,
        ushort volumeSequence = 1,
        int extraSectors = 0,
        int layer1Sectors = 0)
    {
        var cnf = string.IsNullOrEmpty(systemCnf) ? Array.Empty<byte>() : Encoding.ASCII.GetBytes(systemCnf);
        if (cnf.Length > UserSectorSize)
        {
            throw new ArgumentException("SYSTEM.CNF must fit in one test sector.", nameof(systemCnf));
        }

        var fileRecord = cnf.Length == 0
            ? Array.Empty<byte>()
            : Record(19, (uint)cnf.Length, 0, Encoding.ASCII.GetBytes("SYSTEM.CNF;1"));
        var directoryLength = (uint)(34 + 34 + fileRecord.Length);
        var directory = new byte[directoryLength];
        var dot = Record(18, directoryLength, 2, [0]);
        var dotDot = Record(18, directoryLength, 2, [1]);
        dot.CopyTo(directory, 0);
        dotDot.CopyTo(directory, dot.Length);
        if (fileRecord.Length > 0)
        {
            fileRecord.CopyTo(directory, dot.Length + dotDot.Length);
        }

        var sectors = new byte[BaseSectors * UserSectorSize];
        WritePvd(
            sectors.AsSpan(16 * UserSectorSize, UserSectorSize),
            BaseSectors,
            volumeLabel,
            volumeSetSize,
            volumeSequence,
            Record(18, directoryLength, 2, [0]));
        directory.CopyTo(sectors.AsSpan(18 * UserSectorSize));
        if (cnf.Length > 0)
        {
            cnf.CopyTo(sectors.AsSpan(19 * UserSectorSize));
        }

        if (layer1Sectors > 0)
        {
            // As on a real DVD9, layer 1's volume starts 16 sectors before layer 0 ends, and its size counts them.
            var layer = new byte[layer1Sectors * UserSectorSize];
            WritePvd(layer.AsSpan(0, UserSectorSize), (uint)layer1Sectors + 16, string.Empty, 1, 1, Record(0, 0, 2, [0]));
            return Concat(sectors, layer);
        }

        if (extraSectors > 0)
        {
            return Concat(sectors, new byte[extraSectors * UserSectorSize]);
        }

        return sectors;
    }

    public static byte[] WrapMode2(byte[] userData)
    {
        if (userData.Length % UserSectorSize != 0)
        {
            throw new ArgumentException("User data must be whole sectors.", nameof(userData));
        }

        var count = userData.Length / UserSectorSize;
        var raw = new byte[count * 2352];
        for (var index = 0; index < count; index++)
        {
            var offset = index * 2352;
            raw[offset] = 0x00;
            for (var sync = 1; sync <= 10; sync++)
            {
                raw[offset + sync] = 0xFF;
            }

            raw[offset + 11] = 0x00;
            raw[offset + 15] = 0x02;
            Buffer.BlockCopy(userData, index * UserSectorSize, raw, offset + 24, UserSectorSize);
        }

        return raw;
    }

    public static byte[] AppendRawSectors(byte[] image, int sectors)
    {
        return Concat(image, new byte[sectors * 2352]);
    }

    private static byte[] Concat(byte[] left, byte[] right)
    {
        var combined = new byte[left.Length + right.Length];
        Buffer.BlockCopy(left, 0, combined, 0, left.Length);
        Buffer.BlockCopy(right, 0, combined, left.Length, right.Length);
        return combined;
    }

    private static void WritePvd(Span<byte> sector, uint volumeSpace, string label, ushort setSize, ushort sequence, byte[] root)
    {
        sector.Clear();
        sector[0] = 0x01;
        "CD001"u8.CopyTo(sector[1..]);
        sector[6] = 0x01;
        var labelBytes = Encoding.ASCII.GetBytes(label);
        if (labelBytes.Length > 32)
        {
            throw new ArgumentException("Volume label is longer than 32 bytes.", nameof(label));
        }

        labelBytes.CopyTo(sector[40..]);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[80..], volumeSpace);
        BinaryPrimitives.WriteUInt32BigEndian(sector[84..], volumeSpace);
        BinaryPrimitives.WriteUInt16LittleEndian(sector[120..], setSize);
        BinaryPrimitives.WriteUInt16BigEndian(sector[122..], setSize);
        BinaryPrimitives.WriteUInt16LittleEndian(sector[124..], sequence);
        BinaryPrimitives.WriteUInt16BigEndian(sector[126..], sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(sector[128..], UserSectorSize);
        BinaryPrimitives.WriteUInt16BigEndian(sector[130..], UserSectorSize);
        root.CopyTo(sector[156..]);
    }

    private static byte[] Record(uint extent, uint dataLength, byte flags, byte[] name)
    {
        var pad = name.Length % 2 == 0;
        var length = 33 + name.Length + (pad ? 1 : 0);
        var record = new byte[length];
        record[0] = (byte)length;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(2), extent);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(6), extent);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(10), dataLength);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(14), dataLength);
        record[25] = flags;
        record[32] = (byte)name.Length;
        name.CopyTo(record.AsSpan(33));
        return record;
    }
}
