
namespace Loadout.Core;

public class FreeSpaceTests
{
    [Fact]
    public void ZeroSectorsAreZeroBytes()
    {
        Assert.Equal(0, FreeSpace.BytesFromSectorCount(0));
    }

    [Fact]
    public void Two512ByteSectorsAreOneKibibyte()
    {
        Assert.Equal(1024, FreeSpace.BytesFromSectorCount(2));
    }

    [Fact]
    public void MaximumSectorCountDoesNotOverflow()
    {
        var bytes = FreeSpace.BytesFromSectorCount(uint.MaxValue);
        Assert.Equal(512L * uint.MaxValue, bytes);
        Assert.True(bytes > uint.MaxValue);
    }
}
