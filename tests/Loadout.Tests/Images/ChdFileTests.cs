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
    public void ReadsWhatChdmanExtracts(string name, string sha256)
    {
        using var image = ChdImageStream.Open(Path.Combine(Fixtures, name));
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant());
        if (name.StartsWith("dvd", StringComparison.Ordinal))
        {
            Assert.Empty(image.Tracks);
            return;
        }

        Assert.NotEmpty(image.Tracks);
        Assert.Equal(
            File.ReadAllText(Path.Combine(Fixtures, "sheets", Path.ChangeExtension(name, ".cue"))),
            ChdTracks.Sheet(image.Tracks, "out.bin"));
    }

    [Theory]
    [InlineData("dvd-default.chd")]
    [InlineData("dvd-all-large-hunk.chd")]
    [InlineData("cd-mix.chd")]
    [InlineData("cd-mode1.chd")]
    [InlineData("cd-cooked.chd")]
    public void ReadsTheSameBytesInAnyOrderAndAnySize(string name)
    {
        var path = Path.Combine(Fixtures, name);
        var whole = ReadWhole(path);
        using var image = ChdImageStream.Open(path);
        Assert.Equal(whole.Length, image.Length);

        // Backwards in odd pieces, across frames, hunks and tracks, then past the end.
        var random = new Random(7);
        for (var round = 0; round < 200; round++)
        {
            var start = random.NextInt64(0, whole.Length);
            var piece = new byte[random.Next(1, 70000)];
            image.Position = start;
            var read = image.Read(piece);
            Assert.Equal((int)Math.Min(piece.Length, whole.Length - start), read);
            Assert.True(whole.AsSpan((int)start, read).SequenceEqual(piece.AsSpan(0, read)));
        }

        image.Seek(0, SeekOrigin.End);
        Assert.Equal(0, image.Read(new byte[16]));
    }

    [Theory]
    [InlineData("dvd-default.chd", 2048)]
    [InlineData("cd-mix.chd", 2352)]
    [InlineData("cd-large-hunk.chd", 1000)]
    public async Task ReadsInOrderAsTheInstallDoes(string name, int piece)
    {
        var path = Path.Combine(Fixtures, name);
        var whole = ReadWhole(path);
        using var image = ChdImageStream.Open(path);
        using var copy = new MemoryStream();
        var buffer = new byte[piece];
        int count;
        while ((count = await image.ReadAsync(buffer.AsMemory())) > 0)
        {
            copy.Write(buffer, 0, count);
        }

        Assert.Equal(whole, copy.ToArray());
    }

    [Fact]
    public void ClosingItWhileItDecodesAheadIsSafe()
    {
        using (var image = ChdImageStream.Open(Path.Combine(Fixtures, "dvd-default.chd")))
        {
            var buffer = new byte[100000];
            image.ReadExactly(buffer);
        }

        // Decoding ahead stops with the stream, and the file is free again.
        File.Copy(Path.Combine(Fixtures, "dvd-default.chd"), Path.Combine(_work, "copy.chd"));
        using var reopened = File.Open(Path.Combine(_work, "copy.chd"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
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

        Assert.Equal(ReadWhole(Path.Combine(Fixtures, "dvd-default.chd")), ReadWhole(path));
    }

    [Fact]
    public void ACancelledDecodeStops()
    {
        using var chd = ChdFile.Open(Path.Combine(Fixtures, "dvd-default.chd"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => chd.DecodeHunks(0, 1, new byte[chd.HunkBytes], cancelled.Token));
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
            var expected = ReadWhole(Path.Combine(Fixtures, name));
            try
            {
                Assert.Equal(expected, ReadWhole(path));
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

        var ex = Assert.Throws<ChdException>(() => ReadWhole(path));
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

    private static byte[] ReadWhole(string path)
    {
        using var image = ChdImageStream.Open(path);
        using var copy = new MemoryStream();
        image.CopyTo(copy);
        return copy.ToArray();
    }
}
