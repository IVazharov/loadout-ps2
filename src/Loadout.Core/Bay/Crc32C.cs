using System.Buffers.Binary;
using System.Numerics;

namespace Loadout.Bay;

/// <summary>
/// CRC-32C (Castagnoli), the checksum the install check compares. The CPU's CRC32 instruction
/// computes it, so a game's checksums cost next to nothing while it is sent. Loadout Bay
/// computes the same sum in software as it reads the game back.
/// </summary>
public static class Crc32C
{
    public const uint Start = 0xFFFFFFFF;

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        var at = 0;
        for (; at + 8 <= data.Length; at += 8)
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data[at..]));
        }

        for (; at < data.Length; at++)
        {
            crc = BitOperations.Crc32C(crc, data[at]);
        }

        return crc;
    }

    public static uint Finish(uint crc) => ~crc;

    public static uint Of(ReadOnlySpan<byte> data) => Finish(Update(Start, data));
}
