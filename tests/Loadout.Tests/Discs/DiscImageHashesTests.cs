using System.Text;

namespace Loadout.Discs;

public class DiscImageHashesTests
{
    [Fact]
    public async Task TheCheckStringHasItsWellKnownHashes()
    {
        var hashes = await DiscImageHashes.ComputeAsync(new MemoryStream(Encoding.ASCII.GetBytes("123456789")), null, CancellationToken.None);

        Assert.Equal(9, hashes.Size);
        Assert.Equal("cbf43926", hashes.Crc32);
        Assert.Equal("25f9e794323b453885f5181f1b624d0b", hashes.Md5);
        Assert.Equal("f7c3bc1d808e04732adf679965ccc34ca7ae3441", hashes.Sha1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(4099)]
    [InlineData((1 << 20) + 13)]
    public async Task TheFastCrcEqualsTheBitByBitOne(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        var reported = 0L;

        var hashes = await DiscImageHashes.ComputeAsync(new MemoryStream(data), new Sync<long>(n => reported = n), CancellationToken.None);

        Assert.Equal(SlowCrc32(data).ToString("x8", System.Globalization.CultureInfo.InvariantCulture), hashes.Crc32);
        Assert.Equal(length, hashes.Size);
        Assert.Equal(length, reported);
    }

    [Fact]
    public void TheHashesFileSaysWhatRedumpSaid()
    {
        var hashes = new DiscImageHashes(8192, "0badf00d", "00112233445566778899aabbccddeeff", "1111111111111111111111111111111111111111");
        var dat = RedumpDatTests.Parse(RedumpDatTests.Fixture);

        var matched = hashes.Format("Test Racer (Europe, Australia).iso", dat.Check(8192, hashes.Sha1), dat.Version);
        var other = hashes.Format("x.iso", dat.Check(8192, "9999999999999999999999999999999999999999"), dat.Version);
        var unknown = hashes.Format("x.iso", dat.Check(1, hashes.Sha1), dat.Version);
        var waiting = hashes.Format("x.iso", null, null);

        Assert.Equal(
            "Test Racer (Europe, Australia).iso\nSize    8192\nCRC-32  0badf00d\nMD5     00112233445566778899aabbccddeeff\nSHA-1   1111111111111111111111111111111111111111\n"
            + "Redump: matches \"Test Racer (Europe, Australia)\" (Redump's PS2 list of 2026-01-02 03-04-05)\n",
            matched);
        Assert.EndsWith("Redump: doesn't match; 2 disc(s) of this size, such as \"Test Racer (Europe, Australia)\" (Redump's PS2 list of 2026-01-02 03-04-05)\n", other, StringComparison.Ordinal);
        Assert.EndsWith("Redump: not in the list (Redump's PS2 list of 2026-01-02 03-04-05)\n", unknown, StringComparison.Ordinal);
        Assert.EndsWith("Redump: not checked yet\n", waiting, StringComparison.Ordinal);
    }

    private static uint SlowCrc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    private sealed class Sync<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
