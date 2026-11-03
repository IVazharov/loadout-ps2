namespace Loadout.SpeedTest;

/// <summary>
/// Checks a kept disk run in a raw PS2 disk image, such as PCSX2's DEV9hdd.raw:
/// the data must sit right after the test partition's 4 MB header area and match
/// what a TCP run sends, byte for byte.
/// </summary>
public static class DiskImageCheck
{
    public const int SectorBytes = 512;

    /// <summary>The first data sector of a main APA partition, after its header area.</summary>
    public const long DataStartSector = 0x2000;

    /// <summary>
    /// Returns the offset of the first byte that differs from the sent data, or null when all
    /// <paramref name="totalBytes"/> match. <paramref name="partitionStartSector"/> is the
    /// partition's start as PFS Shell lists it. <paramref name="seed"/> must match the run's.
    /// </summary>
    public static long? FirstMismatch(string imagePath, long partitionStartSector, long totalBytes, int seed = SpeedTestClient.DefaultSeed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(partitionStartSector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        var block = SpeedTestClient.TcpBlock(seed);
        var actual = new byte[block.Length];
        using var image = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        image.Position = (partitionStartSector + DataStartSector) * SectorBytes;
        for (long done = 0; done < totalBytes; done += block.Length)
        {
            var count = (int)Math.Min(block.Length, totalBytes - done);
            image.ReadExactly(actual, 0, count);
            var differs = actual.AsSpan(0, count).CommonPrefixLength(block.AsSpan(0, count));
            if (differs < count)
            {
                return done + differs;
            }
        }

        return null;
    }
}
