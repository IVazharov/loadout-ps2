using System.Security.Cryptography;

namespace Loadout.Images;

/// <summary>
/// Reads files made by chdman 0.289 with every compression, and expects exactly what chdman extracts
/// from them. See ChdFixtures/make-fixtures.py.
/// </summary>
public sealed class ChdFileTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Images", "ChdFixtures");

    private readonly string _work = Directory.CreateTempSubdirectory("loadout-chd-").FullName;

    public static TheoryData<string, string> Expected()
    {
        var data = new TheoryData<string, string>();
        foreach (var line in File.ReadAllLines(Path.Combine(Fixtures, "expected.txt")))
        {
            var parts = line.Split(' ');
            data.Add(parts[0], parts[1]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Expected))]
    public void ExtractsWhatChdmanExtracts(string name, string sha256)
    {
        using var chd = ChdFile.Open(Path.Combine(Fixtures, name));
        if (name.StartsWith("dvd", StringComparison.Ordinal))
        {
            Assert.Equal(ChdImageKind.Dvd, chd.Kind);
            var iso = Path.Combine(_work, "out.iso");
            ChdExtractor.ToIso(chd, iso, null, CancellationToken.None);
            Assert.Equal(sha256, Sha256(iso));
            return;
        }

        Assert.Equal(ChdImageKind.Cd, chd.Kind);
        var cue = Path.Combine(_work, "out.cue");
        var bin = Path.Combine(_work, "out.bin");
        ChdExtractor.ToCueAndBin(chd, cue, bin, null, CancellationToken.None);
        Assert.Equal(sha256, Sha256(bin));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(Fixtures, "sheets", Path.ChangeExtension(name, ".cue"))),
            File.ReadAllBytes(cue));
    }

    [Fact]
    public void AUnaryCodeCanUseUpTheWholeBitBuffer()
    {
        // 63 zeros and a one fill the reader's 64 bits exactly. What follows must not mix with them.
        byte[] data = [0, 0, 0, 0, 0, 0, 0, 1, 0xA5, 0x5A];
        var bits = new ChdBitReader(data, 0, data.Length);

        Assert.Equal(63u, bits.ReadUnary());
        Assert.Equal(0xA5u, bits.Read(8));
        Assert.Equal(0x5Au, bits.Read(8));
    }

    [Fact]
    public void AWrongHashInTheHeaderIsNotDamage()
    {
        // Some tools write a wrong one over correct data.
        var bytes = File.ReadAllBytes(Path.Combine(Fixtures, "dvd-default.chd"));
        bytes[64] ^= 0xFF;
        var path = Path.Combine(_work, "wrong-hash.chd");
        File.WriteAllBytes(path, bytes);
        using var original = ChdFile.Open(Path.Combine(Fixtures, "dvd-default.chd"));
        using var chd = ChdFile.Open(path);

        Assert.Equal(ReadAll(original), ReadAll(chd));
    }

    [Fact]
    public void ProgressReachesAHundredPercent()
    {
        using var chd = ChdFile.Open(Path.Combine(Fixtures, "dvd-default.chd"));
        var reports = new List<double>();
        chd.ReadAll((_, _) => { }, reports.Add, CancellationToken.None);
        Assert.Equal(100.0, reports[^1]);
    }

    [Fact]
    public void ACancelledReadStops()
    {
        using var chd = ChdFile.Open(Path.Combine(Fixtures, "dvd-default.chd"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => chd.ReadAll((_, _) => { }, null, cancelled.Token));
    }

    [Theory]
    [InlineData("dvd-lzma.chd")]
    [InlineData("dvd-zlib.chd")]
    [InlineData("dvd-huff.chd")]
    [InlineData("dvd-flac.chd")]
    [InlineData("dvd-zstd.chd")]
    [InlineData("cd-cdlz.chd")]
    [InlineData("cd-cdzl.chd")]
    [InlineData("cd-cdzs.chd")]
    [InlineData("cd-cdfl-audio.chd")]
    public void DamagedDataIsRefused(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Fixtures, name));

        // In the compressed data, which follows the header: every place must be noticed or harmless,
        // and none may hand over wrong data without saying so.
        for (var offset = 200; offset < bytes.Length - 400; offset += Math.Max(1, bytes.Length / 40))
        {
            var damaged = (byte[])bytes.Clone();
            damaged[offset] ^= 0x55;
            var path = Path.Combine(_work, "damaged.chd");
            File.WriteAllBytes(path, damaged);
            using var original = ChdFile.Open(Path.Combine(Fixtures, name));
            var expected = ReadAll(original);
            try
            {
                using var chd = ChdFile.Open(path);
                Assert.Equal(expected, ReadAll(chd));
            }
            catch (ChdException ex)
            {
                Assert.Contains("chd", ex.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AFileCutShortIsRefused()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Fixtures, "dvd-default.chd"));
        var path = Path.Combine(_work, "cut.chd");
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        var ex = Assert.Throws<ChdException>(() =>
        {
            using var chd = ChdFile.Open(path);
            chd.ReadAll((_, _) => { }, null, CancellationToken.None);
        });
        Assert.Contains("ends early", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 8)]
    public void AFileThatIsNotAChdIsRefused(byte fill, int length)
    {
        var path = Path.Combine(_work, "blank.chd");
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<ChdException>(() => ChdFile.Open(path));
        Assert.Contains("not a complete chd", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderVersionIsRefusedByName()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Fixtures, "dvd-default.chd"));
        bytes[15] = 4;
        var path = Path.Combine(_work, "v4.chd");
        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<ChdException>(() => ChdFile.Open(path));
        Assert.Contains("version 4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AChildOfAParentIsRefused()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Fixtures, "dvd-default.chd"));
        bytes[104] = 1;
        var path = Path.Combine(_work, "child.chd");
        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<ChdException>(() => ChdFile.Open(path));
        Assert.Contains("parent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCompressionIsRefusedByName()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Fixtures, "dvd-default.chd"));
        "avhu"u8.CopyTo(bytes.AsSpan(16));
        var path = Path.Combine(_work, "video.chd");
        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<ChdException>(() => ChdFile.Open(path));
        Assert.Contains("avhu", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ChdImageKind.Dvd, "DVD ")]
    [InlineData(ChdImageKind.Cd, "CHT2")]
    [InlineData(ChdImageKind.Cd, "CHTR")]
    [InlineData(ChdImageKind.Unsupported, "GDDD")]
    [InlineData(ChdImageKind.Unsupported, "CHGD")]
    [InlineData(ChdImageKind.Unsupported, "DVD ", "CHT2")]
    [InlineData(ChdImageKind.Unknown, "XXXX")]
    [InlineData(ChdImageKind.Unknown)]
    public void MetadataTagsSayWhatKindOfDiscItIs(ChdImageKind expected, params string[] tags)
        => Assert.Equal(expected, ChdFile.Classify(tags));

    public void Dispose() => TempDirectory.Delete(_work);

    private static string ReadAll(ChdFile chd)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        chd.ReadAll((buffer, count) => hash.AppendData(buffer, 0, count), null, CancellationToken.None);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
