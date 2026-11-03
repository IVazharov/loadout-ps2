using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Loadout.Vmc;

public sealed class VmcImageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 6, 15, 42, TimeSpan.Zero);

    [Fact]
    public void An8MbCardPassesOplsChecks()
    {
        var card = VmcImage.Create(8, Now);

        // OPL's sysCheckVMC: the magic, card type 2, and a file size of pages per cluster x clusters x page size.
        Assert.Equal(8 * 1024 * 1024, card.Length);
        Assert.Equal("Sony PS2 Memory Card Format 1.2.0.0", Encoding.ASCII.GetString(card, 0, 35));
        Assert.Equal(2, card[336]);
        var pageSize = BinaryPrimitives.ReadUInt16LittleEndian(card.AsSpan(40));
        var pagesPerCluster = BinaryPrimitives.ReadUInt16LittleEndian(card.AsSpan(42));
        var clusters = BinaryPrimitives.ReadUInt32LittleEndian(card.AsSpan(48));
        Assert.Equal(card.Length, pageSize * pagesPerCluster * (int)clusters);
    }

    [Fact]
    public void An8MbCardHasTheLayoutOfASonyCard()
    {
        var card = VmcImage.Create(8, Now);

        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(card.AsSpan(44))); // pages per erase block
        Assert.Equal(41u, Word(card, 52)); // first allocatable cluster
        Assert.Equal(8135u, Word(card, 56)); // allocatable clusters
        Assert.Equal(0u, Word(card, 60)); // root directory
        Assert.Equal(1023u, Word(card, 64)); // backup blocks
        Assert.Equal(1022u, Word(card, 68));
        Assert.Equal(8u, Word(card, 80)); // one indirect FAT cluster, at cluster 8
        Assert.Equal(uint.MaxValue, Word(card, 84));
        Assert.Equal(uint.MaxValue, Word(card, 208)); // no bad blocks

        // The indirect FAT cluster lists the 32 FAT clusters, 9 to 40.
        Assert.Equal(9u, Word(card, 8 * 1024));
        Assert.Equal(40u, Word(card, (8 * 1024) + (31 * 4)));
        Assert.Equal(0u, Word(card, (8 * 1024) + (32 * 4)));

        // The FAT: the root directory's one cluster ends its chain, every other cluster is free.
        Assert.Equal(uint.MaxValue, Word(card, 9 * 1024));
        Assert.Equal(0x7FFFFFFFu, Word(card, (9 * 1024) + 4));
        Assert.Equal(0x7FFFFFFFu, Word(card, (40 * 1024) + ((8135 - (31 * 256) - 1) * 4)));
        Assert.Equal(0u, Word(card, (40 * 1024) + ((8135 - (31 * 256)) * 4)));

        // The root directory holds "." and "..", and the rest of the card is erased.
        Assert.Equal(".", Encoding.ASCII.GetString(card, (41 * 1024) + 64, 1));
        Assert.Equal("..", Encoding.ASCII.GetString(card, (41 * 1024) + 512 + 64, 2));
        Assert.Equal(0x8427, BinaryPrimitives.ReadUInt16LittleEndian(card.AsSpan(41 * 1024)));
        Assert.Equal(0xA426, BinaryPrimitives.ReadUInt16LittleEndian(card.AsSpan((41 * 1024) + 512)));
        Assert.All(card.AsSpan(42 * 1024).ToArray(), b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void TheRootDirectoryTimesAreInJapanTime()
    {
        var card = VmcImage.Create(8, Now);

        // 06:15:42 UTC is 15:15:42 in Japan.
        Assert.Equal(new byte[] { 0, 42, 15, 15, 30, 10, 0xEA, 0x07 }, card.AsSpan((41 * 1024) + 8, 8).ToArray());
        Assert.Equal(card.AsSpan((41 * 1024) + 8, 8).ToArray(), card.AsSpan((41 * 1024) + 24, 8).ToArray());
    }

    // Recorded from cards that matched a line-by-line port of OPL's genvmc (pc/genvmc, commit 1cb3a8ee)
    // byte for byte, with the root directory's times zeroed.
    [Theory]
    [InlineData(8, 41, "7b0529419dc389f89c348e60f1571a4c8bd2bda8099ce961566571258e87d50b")]
    [InlineData(16, 73, "f5717aa239155568ae7b8937351c3d1fed8c6257c6a5a08a06256fb27848b6f2")]
    [InlineData(32, 137, "431bf1a77673729895a230bd6b191a618d38867e441fba33ebe44f28834e1356")]
    [InlineData(64, 265, "272a568f164a2c6bc91cb7a55123eea5f1b3e27df784e0855faae5b0f67cf690")]
    public void EachSizeIsTheCardGenvmcWrites(int megabytes, int rootCluster, string sha256)
    {
        var card = VmcImage.Create(megabytes, Now);
        Assert.Equal((uint)rootCluster, Word(card, 52));
        foreach (var at in new[] { 8, 24, 512 + 8, 512 + 24 })
        {
            card.AsSpan((rootCluster * 1024) + at, 8).Clear();
        }

        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(card)).ToLowerInvariant());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(128)]
    public void OtherSizesAreRefused(int megabytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VmcImage.Create(megabytes, Now));
    }

    private static uint Word(byte[] card, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(card.AsSpan(offset));
}
