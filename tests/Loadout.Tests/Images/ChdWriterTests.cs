using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Loadout.Fakes;

namespace Loadout.Images;

/// <summary>
/// Writes chds and reads them back with Loadout's reader, which ChdFileTests holds to what chdman
/// extracts. The header's checksums are held to chdman's own, in the files it made.
/// </summary>
public sealed class ChdWriterTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Images", "ChdFixtures");

    private readonly string _work = Directory.CreateTempSubdirectory("loadout-chd-writer-").FullName;

    public static TheoryData<string> FixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(Fixtures, "*.chd").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void TheHeaderChecksumsAreWorkedOutAsChdmanDoes(string name)
    {
        var path = Path.Combine(Fixtures, name);
        var header = File.ReadAllBytes(path).AsSpan(0, 124).ToArray();
        if (header.AsSpan(16, 16).IndexOfAnyExcept((byte)0) < 0)
        {
            // chdman leaves an uncompressed chd without checksums.
            Assert.True(header.AsSpan(64, 40).IndexOfAnyExcept((byte)0) < 0);
            return;
        }

        using var chd = ChdFile.Open(path);
        var logical = DecodeLogical(chd);

        var data = Sha1(logical);
        Assert.Equal(Convert.ToHexString(header, 64, 20), Convert.ToHexString(data));
        var overall = ChdWriter.OverallSha1(data, chd.Metadata.Select(entry => (entry.Tag, entry.Data)));
        Assert.Equal(Convert.ToHexString(header, 84, 20), Convert.ToHexString(overall));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(300)]
    [InlineData(4096)]
    [InlineData(18816)]
    public void LzmaComesBackWhole(int size)
    {
        foreach (var data in Samples(size))
        {
            var encoder = new ChdLzmaEncoder(size);
            // Noise grows a little: room for that.
            var compressed = new byte[(size * 2) + 64];
            var length = encoder.Encode(data, compressed, compressed.Length);
            Assert.True(length > 0);

            var back = new byte[size];
            new ChdLzmaCodec().Decode(compressed, 0, length, back);
            Assert.Equal(data, back);
        }
    }

    [Fact]
    public void OneLzmaEncoderServesManyHunks()
    {
        var encoder = new ChdLzmaEncoder(4096);
        var decoder = new ChdLzmaCodec();
        var compressed = new byte[5000];
        var back = new byte[4096];
        foreach (var data in Samples(4096).Concat(Samples(4096).Reverse()))
        {
            var length = encoder.Encode(data, compressed, compressed.Length);
            decoder.Decode(compressed, 0, length, back);
            Assert.Equal(data, back);
        }
    }

    [Fact]
    public void LzmaGivesUpAtTheLimit()
    {
        var noise = new byte[4096];
        new Random(3).NextBytes(noise);
        Assert.Equal(-1, new ChdLzmaEncoder(4096).Encode(noise, new byte[8192], 4096));
        Assert.Equal(-1, new ChdLzmaEncoder(4096).Encode(new byte[4096], new byte[8192], 3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    [InlineData(4096)]
    public void HuffmanComesBackWhole(int size)
    {
        foreach (var data in Samples(size))
        {
            var tree = new ChdHuffmanEncoder(256, 16);
            foreach (var value in data)
            {
                tree.Count(value);
            }

            tree.ComputeTree();
            var compressed = new byte[(size * 3) + 1024];
            var bits = new ChdBitWriter();
            bits.Reset(compressed, 0, compressed.Length);
            tree.ExportTreeHuffman(bits, new ChdHuffmanEncoder(24, 6));
            foreach (var value in data)
            {
                tree.Encode(bits, value);
            }

            var length = bits.Flush();
            var back = new byte[size];
            new ChdHuffmanCodec().Decode(compressed, 0, length, back);
            Assert.Equal(data, back);
        }
    }

    [Fact]
    public void HuffmanCodesNeverGrowPastTheirLimit()
    {
        // Counts that double each time would give a code as long as there are codes.
        var tree = new ChdHuffmanEncoder(256, 16);
        for (var code = 0; code < 30; code++)
        {
            for (var count = 0; count < 1 << Math.Min(code, 24); count++)
            {
                tree.Count(code);
            }
        }

        tree.ComputeTree();
        Assert.All(Enumerable.Range(0, 30), code => Assert.InRange(tree.Length(code), 1, 16));
    }

    [Theory]
    [InlineData("dvd-default.chd")]
    [InlineData("dvd-all-large-hunk.chd")]
    public void ADvdComesBackAsTheSameIso(string fixture)
    {
        var iso = Path.Combine(_work, "game.iso");
        File.WriteAllBytes(iso, ReadImage(Path.Combine(Fixtures, fixture)));
        var chd = Path.Combine(_work, "game.chd");

        var length = ChdWriter.WriteDvd(iso, chd, 3, CancellationToken.None);

        Assert.Equal(new FileInfo(chd).Length, length);
        Assert.True(length < new FileInfo(iso).Length);
        AssertSameImage(iso, chd);
        using var file = ChdFile.Open(chd);
        Assert.Equal(ChdImageKind.Dvd, file.Kind);
        Assert.Equal(4096, file.HunkBytes);
        AssertChecksums(chd, file);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4097)]
    public void AnIsoOfAnyLengthComesBack(int sectors)
    {
        var image = new byte[sectors * 2048];
        new Random(sectors).NextBytes(image.AsSpan(0, image.Length / 2));
        var iso = Path.Combine(_work, "odd.iso");
        File.WriteAllBytes(iso, image);
        var chd = Path.Combine(_work, "odd.chd");

        ChdWriter.WriteDvd(iso, chd, 2, CancellationToken.None);

        AssertSameImage(iso, chd);
        using var file = ChdFile.Open(chd);
        AssertChecksums(chd, file);
    }

    [Fact]
    public void RepeatedAndPlainHunksMakeAMapThatReadsBack()
    {
        // Runs of every length the map's run coding has a form for, references near and far, and
        // hunks that do not compress.
        var random = new Random(11);
        var unique = Enumerable.Range(0, 6).Select(_ =>
        {
            var hunk = new byte[4096];
            random.NextBytes(hunk);
            return hunk;
        }).ToArray();
        using var image = new MemoryStream();
        foreach (var run in new[] { 1, 2, 3, 18, 19, 300, 5 })
        {
            for (var index = 0; index < run; index++)
            {
                image.Write(new byte[4096]);
            }

            image.Write(unique[run % unique.Length]);
            image.Write(unique[(run + 1) % unique.Length]);
        }

        var iso = Path.Combine(_work, "runs.iso");
        File.WriteAllBytes(iso, image.ToArray());
        var chd = Path.Combine(_work, "runs.chd");

        ChdWriter.WriteDvd(iso, chd, 2, CancellationToken.None);

        AssertSameImage(iso, chd);
        using var file = ChdFile.Open(chd);
        AssertChecksums(chd, file);
    }

    [Fact]
    public void TheSameImageMakesTheSameFileWhateverTheThreads()
    {
        var iso = Path.Combine(_work, "game.iso");
        File.WriteAllBytes(iso, ReadImage(Path.Combine(Fixtures, "dvd-default.chd")));

        ChdWriter.WriteDvd(iso, Path.Combine(_work, "one.chd"), 1, CancellationToken.None);
        ChdWriter.WriteDvd(iso, Path.Combine(_work, "four.chd"), 4, CancellationToken.None);

        Assert.Equal(File.ReadAllBytes(Path.Combine(_work, "one.chd")), File.ReadAllBytes(Path.Combine(_work, "four.chd")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(301)]
    public void ACdComesBackAsTheSameBinAndSheet(int sectors)
    {
        var bin = Path.Combine(_work, "game.bin");
        File.WriteAllBytes(bin, Mode2Sectors(sectors));
        var chd = Path.Combine(_work, "game.chd");

        ChdWriter.WriteCd(bin, "MODE2_RAW", chd, 3, CancellationToken.None);

        AssertSameImage(bin, chd);
        using (var image = ChdImageStream.Open(chd))
        {
            Assert.Equal(
                "FILE \"game.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n",
                ChdTracks.Sheet(image.Tracks, "game.bin"));
        }

        using var file = ChdFile.Open(chd);
        Assert.Equal(ChdImageKind.Cd, file.Kind);
        Assert.Equal(2448 * 8, file.HunkBytes);
        Assert.Equal((sectors + 3) / 4 * 4 * 2448L, file.LogicalBytes);
        AssertChecksums(chd, file);
    }

    [Fact]
    public void CdSectorsLoseOnlyWhatCanBeCalculatedAgain()
    {
        // Sectors with good error correction shrink by its size; the rest keep theirs.
        var bin = Path.Combine(_work, "game.bin");
        var sectors = Mode2Sectors(64);
        sectors[(10 * 2352) + 0x900] ^= 1;
        File.WriteAllBytes(bin, sectors);
        var chd = Path.Combine(_work, "game.chd");

        ChdWriter.WriteCd(bin, "MODE2_RAW", chd, 2, CancellationToken.None);

        AssertSameImage(bin, chd);
    }

    [Fact]
    public void ACancelledWriteLeavesNoFile()
    {
        var iso = Path.Combine(_work, "game.iso");
        File.WriteAllBytes(iso, new byte[2048 * 16]);
        var chd = Path.Combine(_work, "game.chd");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ChdWriter.WriteDvd(iso, chd, 2, cancel.Token));
        Assert.False(File.Exists(chd));
    }

    [Fact]
    public async Task TheAsyncWriteReportsHowFarItGot()
    {
        var iso = Path.Combine(_work, "game.iso");
        File.WriteAllBytes(iso, new byte[2048 * 5000]);
        var chd = Path.Combine(_work, "game.chd");
        var reported = new List<long>();

        await ChdWriter.WriteDvdAsync(iso, chd, new SynchronousProgress(reported.Add), CancellationToken.None);

        Assert.Equal(2048 * 5000, reported[^1]);
        AssertSameImage(iso, chd);
    }

    public void Dispose() => TempDirectory.Delete(_work);

    private static IEnumerable<byte[]> Samples(int size)
    {
        var random = new Random(size);
        var noise = new byte[size];
        random.NextBytes(noise);
        yield return noise;
        yield return new byte[size];
        var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("BOOT2 = cdrom0:\\SLUS_200.62;1\nVER = 1.00\n", (size / 40) + 1)))[..size];
        yield return text;
        var skewed = new byte[size];
        for (var index = 0; index < size; index++)
        {
            skewed[index] = (byte)(random.Next(16) == 0 ? random.Next(256) : index % 3);
        }

        yield return skewed;
        var mixed = new byte[size];
        noise.AsSpan(0, size / 2).CopyTo(mixed);
        text.AsSpan(0, size - (size / 2)).CopyTo(mixed.AsSpan(size / 2));
        yield return mixed;
    }

    /// <summary>Mode 2 form 1 sectors, as on a PS2 CD, with their sync and parities filled in.</summary>
    private static byte[] Mode2Sectors(int count)
    {
        var random = new Random(count);
        var sectors = new byte[count * 2352];
        for (var index = 0; index < count; index++)
        {
            var sector = sectors.AsSpan(index * 2352, 2352);
            var address = index + 150;
            sector[12] = ToBcd(address / 4500);
            sector[13] = ToBcd(address / 75 % 60);
            sector[14] = ToBcd(address % 75);
            sector[15] = 2;
            random.NextBytes(sector.Slice(24, index % 3 == 0 ? 2048 : 512));
            ChdCdEcc.Restore(sector);
        }

        return sectors;
    }

    private static byte[] Sha1(byte[] data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(data);
        return hash.GetHashAndReset();
    }

    private static byte ToBcd(int value) => (byte)(((value / 10) << 4) | (value % 10));

    private static byte[] ReadImage(string chd)
    {
        using var image = ChdImageStream.Open(chd);
        using var copy = new MemoryStream();
        image.CopyTo(copy);
        return copy.ToArray();
    }

    private static byte[] DecodeLogical(ChdFile chd)
    {
        var buffer = new byte[chd.HunkCount * (long)chd.HunkBytes];
        var batch = chd.BatchHunks;
        var hunks = new byte[batch * chd.HunkBytes];
        for (uint first = 0; first < chd.HunkCount; first += (uint)batch)
        {
            var count = (int)Math.Min((uint)batch, chd.HunkCount - first);
            chd.DecodeHunks(first, count, hunks, CancellationToken.None);
            hunks.AsSpan(0, count * chd.HunkBytes).CopyTo(buffer.AsSpan((int)(first * chd.HunkBytes)));
        }

        return buffer[..(int)chd.LogicalBytes];
    }

    private static void AssertSameImage(string image, string chd)
    {
        using var stream = ChdImageStream.Open(chd);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(File.ReadAllBytes(image), copy.ToArray());
    }

    private static void AssertChecksums(string path, ChdFile chd)
    {
        var header = File.ReadAllBytes(path).AsSpan(0, 124).ToArray();
        var data = Sha1(DecodeLogical(chd));
        Assert.Equal(Convert.ToHexString(data), Convert.ToHexString(header, 64, 20));
        Assert.Equal(
            Convert.ToHexString(ChdWriter.OverallSha1(data, chd.Metadata.Select(entry => (entry.Tag, entry.Data)))),
            Convert.ToHexString(header, 84, 20));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12)));
    }

    private sealed class SynchronousProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
