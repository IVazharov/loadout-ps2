using System.Buffers.Binary;
using System.Text;

namespace Loadout.Vmc;

/// <summary>
/// An empty, formatted PS2 memory card image as OPL keeps a virtual memory card: 512-byte pages
/// without their ECC bytes, two pages to a 1 KiB cluster, and 16 pages to an erase block. The
/// layout is the one OPL's own <c>genvmc</c> writes for the same size; only the root directory's
/// times differ, as these are the real time.
/// </summary>
public static class VmcImage
{
    /// <summary>The sizes OPL's game menu offers, in MB.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [8, 16, 32, 64];

    /// <summary>The size of Sony's own cards, which every game's saves fit in.</summary>
    public const int DefaultSize = 8;

    private const int ClusterBytes = 1024;
    private const int PageBytes = 512;
    private const int PagesPerCluster = ClusterBytes / PageBytes;
    private const int PagesPerBlock = 16;
    private const int ClustersPerBlock = PagesPerBlock / PagesPerCluster;
    private const int FatEntriesPerCluster = ClusterBytes / 4;
    private const int ListEntries = 32;
    private const int EntryBytes = 512;

    // Directory entry modes, as Sony's memory card manager sets them.
    private const ushort Readable = 0x0001;
    private const ushort Writable = 0x0002;
    private const ushort Executable = 0x0004;
    private const ushort Directory = 0x0020;
    private const ushort Mode0400 = 0x0400;
    private const ushort Hidden = 0x2000;
    private const ushort Exists = 0x8000;

    private const uint FreeCluster = 0x7FFFFFFF;
    private const uint EndOfChain = 0xFFFFFFFF;

    /// <summary>The PS2 keeps memory card times in Japan time.</summary>
    private static readonly TimeSpan JapanTime = TimeSpan.FromHours(9);

    public static byte[] Create(int megabytes, DateTimeOffset now)
    {
        if (!Sizes.Contains(megabytes))
        {
            throw new ArgumentOutOfRangeException(nameof(megabytes), megabytes, "A memory card is 8, 16, 32, or 64 MB.");
        }

        var clusters = megabytes * 1024 * 1024 / ClusterBytes;
        var image = new byte[clusters * ClusterBytes];
        image.AsSpan().Fill(0xFF);

        // The FAT has an entry per cluster. The clusters that list where the FAT lies (indirect
        // FAT clusters) start in the second erase block, the FAT follows them, and the clusters
        // files get follow the FAT. The last two blocks are the backup blocks.
        var fatClusters = ((clusters * 4) - 1) / ClusterBytes + 1;
        var indirectClusters = ((fatClusters * 4) - 1) / ClusterBytes + 1;
        var firstIndirect = ClustersPerBlock;
        var firstFat = firstIndirect + indirectClusters;
        var firstAllocatable = firstFat + fatClusters;
        var blocks = clusters / ClustersPerBlock;
        var endAllocatable = (blocks - 2) * ClustersPerBlock;

        var indirect = Cluster(image, firstIndirect, indirectClusters);
        indirect.Clear();
        for (var i = 0; i < fatClusters; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(indirect[(i * 4)..], (uint)(firstFat + i));
        }

        // The root directory takes the first allocatable cluster; the others are free.
        var fat = Cluster(image, firstFat, fatClusters);
        for (var entry = 0; entry < endAllocatable - firstAllocatable; entry += FatEntriesPerCluster)
        {
            var cluster = fat.Slice(entry * 4, ClusterBytes);
            cluster.Clear();
            var count = Math.Min(endAllocatable - firstAllocatable - entry, FatEntriesPerCluster);
            for (var i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(cluster[(i * 4)..], entry + i == 0 ? EndOfChain : FreeCluster);
            }
        }

        var root = Cluster(image, firstAllocatable, 1);
        root.Clear();
        var time = now.ToOffset(JapanTime);
        WriteEntry(root, Exists | Mode0400 | Directory | Readable | Writable | Executable, time, ".");
        WriteEntry(root[EntryBytes..], Exists | Hidden | Mode0400 | Directory | Writable | Executable, time, "..");

        var superblock = image.AsSpan(0, 384);
        Encoding.ASCII.GetBytes("Sony PS2 Memory Card Format 1.2.0.0").CopyTo(superblock);
        superblock[35..40].Clear();
        BinaryPrimitives.WriteInt16LittleEndian(superblock[40..], PageBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(superblock[42..], PagesPerCluster);
        BinaryPrimitives.WriteUInt16LittleEndian(superblock[44..], PagesPerBlock);
        BinaryPrimitives.WriteUInt16LittleEndian(superblock[46..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[48..], (uint)clusters);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[52..], (uint)firstAllocatable);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[56..], (uint)(endAllocatable - firstAllocatable));
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[60..], 0); // root directory, relative to the first allocatable cluster
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[64..], (uint)(blocks - 1));
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[68..], (uint)(blocks - 2));
        superblock[72..80].Clear();
        for (var i = 0; i < ListEntries; i++)
        {
            // The indirect FAT clusters, then no bad blocks.
            BinaryPrimitives.WriteUInt32LittleEndian(superblock[(80 + (i * 4))..], i < indirectClusters ? (uint)(firstIndirect + i) : EndOfChain);
            BinaryPrimitives.WriteInt32LittleEndian(superblock[(208 + (i * 4))..], -1);
        }

        superblock[336] = 2; // a PS2 card
        superblock[337] = 0x2B; // its flags, as genvmc sets them
        BinaryPrimitives.WriteUInt16LittleEndian(superblock[338..], 0);

        // Fields the memory card manager keeps after the superblock proper, as genvmc writes them.
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[340..], ClusterBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[344..], FatEntriesPerCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[348..], ClustersPerBlock);
        BinaryPrimitives.WriteInt32LittleEndian(superblock[352..], 1); // formatted
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[356..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[360..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[364..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[368..], (uint)((clusters / 1000 * 1000) + 1)); // the most clusters in use
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[372..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[376..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(superblock[380..], -1);
        return image;
    }

    private static Span<byte> Cluster(byte[] image, int first, int count) => image.AsSpan(first * ClusterBytes, count * ClusterBytes);

    private static void WriteEntry(Span<byte> entry, int mode, DateTimeOffset time, string name)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)mode);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 2); // "." and ".."
        WriteTime(entry[8..], time);
        WriteTime(entry[24..], time);
        Encoding.ASCII.GetBytes(name).CopyTo(entry[64..]);
    }

    private static void WriteTime(Span<byte> field, DateTimeOffset time)
    {
        field[0] = 0;
        field[1] = (byte)time.Second;
        field[2] = (byte)time.Minute;
        field[3] = (byte)time.Hour;
        field[4] = (byte)time.Day;
        field[5] = (byte)time.Month;
        BinaryPrimitives.WriteUInt16LittleEndian(field[6..], (ushort)time.Year);
    }
}
