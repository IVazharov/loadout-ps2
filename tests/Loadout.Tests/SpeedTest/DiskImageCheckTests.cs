namespace Loadout.SpeedTest;

public sealed class DiskImageCheckTests : IDisposable
{
    private const long StartSector = 3;

    private readonly string _root = Directory.CreateTempSubdirectory("loadout-image-").FullName;

    private string Image => Path.Combine(_root, "disk.raw");

    public void Dispose() => TempDirectory.Delete(_root);

    [Fact]
    public void DataAfterTheHeaderAreaThatMatchesTheSentBlocksPasses()
    {
        var total = 3 * SpeedTestClient.TcpBlock().Length / 2;
        WriteImage(total, corruptAt: null);

        Assert.Null(DiskImageCheck.FirstMismatch(Image, StartSector, total));
    }

    [Fact]
    public void DataFromAnotherSeedFailsAtTheFirstByte()
    {
        var total = SpeedTestClient.TcpBlock().Length;
        WriteImage(total, corruptAt: null);

        Assert.Equal(0, DiskImageCheck.FirstMismatch(Image, StartSector, total, seed: 2));
    }

    [Fact]
    public void TheFirstChangedByteIsReported()
    {
        var total = 2 * SpeedTestClient.TcpBlock().Length;
        WriteImage(total, corruptAt: 1_500_000);

        Assert.Equal(1_500_000, DiskImageCheck.FirstMismatch(Image, StartSector, total));
    }

    private void WriteImage(long total, long? corruptAt)
    {
        var block = SpeedTestClient.TcpBlock();
        var data = new byte[total];
        for (long i = 0; i < total; i++)
        {
            data[i] = block[i % block.Length];
        }

        if (corruptAt is long at)
        {
            data[at] ^= 0xFF;
        }

        using var image = File.Create(Image);
        image.Position = (StartSector + DiskImageCheck.DataStartSector) * DiskImageCheck.SectorBytes;
        image.Write(data);
    }
}
