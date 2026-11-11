using System.Buffers.Binary;
using System.Text;

namespace Loadout.Discs;

public class IsoFileTreeTests
{
    private const int Sector = DiscSectors.Size;

    [Fact]
    public void EveryFileIsListedWithWhereItIs()
    {
        var list = IsoFileTree.Read(From(BuildIso()), 32);

        Assert.True(list.Complete);
        Assert.Equal(
            [
                new IsoFile("SYSTEM.CNF", 20, 1, 60),
                new IsoFile("MAIN.ELF", 21, 2, 3000),
                new IsoFile("DATA/MOVIE.PSS", 23, 5, (5 * Sector) - 100),
                new IsoFile("DATA/SOUND.BIN", 28, 3, 3 * Sector),
            ],
            list.Files);
    }

    [Fact]
    public void TheRootFolderAloneCanBeRead()
    {
        var list = IsoFileTree.Read(From(BuildIso()), 32, rootOnly: true);

        Assert.Equal(["SYSTEM.CNF", "MAIN.ELF"], list.Files.Select(f => f.Path));
    }

    [Fact]
    public void AFolderNotReadYetLeavesTheListIncomplete()
    {
        var iso = BuildIso();
        var list = IsoFileTree.Read((sector, destination) => sector != 19 && From(iso)(sector, destination), 32);

        Assert.False(list.Complete);
        Assert.Equal(["SYSTEM.CNF", "MAIN.ELF"], list.Files.Select(f => f.Path));
    }

    [Fact]
    public void ADiscWithoutAVolumeDescriptorHasNoFiles()
    {
        var list = IsoFileTree.Read(From(new byte[32 * Sector]), 32);

        Assert.False(list.Complete);
        Assert.Empty(list.Files);
    }

    [Fact]
    public void TheFilesInMissingStretchesAreNamed()
    {
        var list = IsoFileTree.Read(From(BuildIso()), 32);

        var (files, outside) = IsoFileTree.FilesIn(list, [(24, 2), (29, 10), (0, 18)]);

        Assert.Equal(["DATA/MOVIE.PSS 2", "DATA/SOUND.BIN 2"], files.Select(f => $"{f.File.Path} {f.Sectors}"));
        Assert.Equal(8 + 18, outside);
    }

    private static IsoFileTree.SectorSource From(byte[] image) => (sector, destination) =>
    {
        if ((sector + 1) * Sector > image.Length)
        {
            return false;
        }

        image.AsSpan((int)(sector * Sector), Sector).CopyTo(destination);
        return true;
    };

    /// <summary>
    /// A 32-sector disc: 16 volume descriptor, 18 root folder, 19 DATA folder, 20 SYSTEM.CNF, 21-22 MAIN.ELF,
    /// 23-27 DATA/MOVIE.PSS, 28-30 DATA/SOUND.BIN.
    /// </summary>
    private static byte[] BuildIso()
    {
        var iso = new byte[32 * Sector];
        var pvd = iso.AsSpan(16 * Sector, Sector);
        pvd[0] = 1;
        "CD001"u8.CopyTo(pvd[1..]);
        pvd[6] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(pvd[80..], 32);
        Record([0], 18, Sector, folder: true).CopyTo(pvd[156..]);
        iso[17 * Sector] = 255;

        Folder(iso, 18, Record([0], 18, Sector, true), Record([1], 18, Sector, true), Record("DATA", 19, Sector, true), Record("MAIN.ELF;1", 21, 3000, false), Record("SYSTEM.CNF;1", 20, 60, false));
        Folder(iso, 19, Record([0], 19, Sector, true), Record([1], 18, Sector, true), Record("MOVIE.PSS;1", 23, (5 * Sector) - 100, false), Record("SOUND.BIN;1", 28, 3 * Sector, false));
        Encoding.ASCII.GetBytes("BOOT2 = cdrom0:\\SLES_505.10;1\nVER = 1.00\nVMODE = PAL\n").CopyTo(iso, 20 * Sector);
        return iso;
    }

    private static void Folder(byte[] iso, int sector, params byte[][] records)
    {
        var at = sector * Sector;
        foreach (var record in records)
        {
            record.CopyTo(iso, at);
            at += record.Length;
        }
    }

    private static byte[] Record(string name, uint extent, uint length, bool folder) => Record(Encoding.ASCII.GetBytes(name), extent, length, folder);

    private static byte[] Record(byte[] name, uint extent, uint length, bool folder)
    {
        var size = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
        var record = new byte[size];
        record[0] = (byte)size;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(2), extent);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(6), extent);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(10), length);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(14), length);
        record[25] = folder ? (byte)2 : (byte)0;
        record[32] = (byte)name.Length;
        name.CopyTo(record, 33);
        return record;
    }
}
