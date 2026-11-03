namespace Loadout.Core;

/// <summary>
/// APA free-space conversion: <c>HDIOC_FREESECTOR</c> counts 512-byte sectors.
/// </summary>
public static class FreeSpace
{
    public const int ApaSectorSizeBytes = 512;

    public static long BytesFromSectorCount(uint sectorCount)
        => sectorCount * (long)ApaSectorSizeBytes;
}
