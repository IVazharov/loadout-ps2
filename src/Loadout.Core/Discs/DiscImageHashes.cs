using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Loadout.Discs;

/// <summary>A copy's size and the hashes Redump lists: CRC-32, MD5 and SHA-1, in lowercase hex.</summary>
public sealed record DiscImageHashes(long Size, string Crc32, string Md5, string Sha1)
{
    /// <summary>Reads the stream from its start to its end once, hashing as it goes. Reports the bytes read so far.</summary>
    public static async Task<DiscImageHashes> ComputeAsync(Stream stream, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var crc = Crc32Ieee.Start;
        var buffer = GC.AllocateUninitializedArray<byte>(1 << 20);
        var total = 0L;
        int n;
        while ((n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var data = buffer.AsSpan(0, n);
            md5.AppendData(data);
            sha1.AppendData(data);
            crc = Crc32Ieee.Append(crc, data);
            total += n;
            progress?.Report(total);
        }

        return new DiscImageHashes(
            total,
            Crc32Ieee.Finish(crc).ToString("x8", CultureInfo.InvariantCulture),
            Convert.ToHexStringLower(md5.GetHashAndReset()),
            Convert.ToHexStringLower(sha1.GetHashAndReset()));
    }

    /// <summary>The hashes file kept beside a copy: its name, size and hashes, and what Redump's list said about it.</summary>
    public string Format(string fileName, RedumpCheck? check, string? datVersion)
    {
        var text = new StringBuilder();
        text.Append(fileName).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"Size    {Size}\n");
        text.Append(CultureInfo.InvariantCulture, $"CRC-32  {Crc32}\n");
        text.Append(CultureInfo.InvariantCulture, $"MD5     {Md5}\n");
        text.Append(CultureInfo.InvariantCulture, $"SHA-1   {Sha1}\n");
        var list = string.IsNullOrEmpty(datVersion) ? "Redump's PS2 list" : $"Redump's PS2 list of {datVersion}";
        text.Append(check switch
        {
            null => "Redump: not checked yet\n",
            { Verdict: RedumpVerdict.Matches, Game: { } game } => $"Redump: matches \"{game.Name}\" ({list})\n",
            { Verdict: RedumpVerdict.DoesNotMatch } => $"Redump: doesn't match; {check.SameSize.Count} disc(s) of this size, such as \"{check.SameSize[0].Name}\" ({list})\n",
            _ => $"Redump: not in the list ({list})\n",
        });
        return text.ToString();
    }
}

/// <summary>CRC-32 as zip and Redump use it (IEEE 802.3, reflected), eight bytes at a time.</summary>
internal static class Crc32Ieee
{
    public const uint Start = 0xFFFFFFFF;

    private static readonly uint[][] Tables = BuildTables();

    public static uint Finish(uint crc) => ~crc;

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var t = Tables;
        while (data.Length >= 8)
        {
            var low = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
            var high = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            crc = t[7][low & 0xFF] ^ t[6][(low >> 8) & 0xFF] ^ t[5][(low >> 16) & 0xFF] ^ t[4][low >> 24]
                ^ t[3][high & 0xFF] ^ t[2][(high >> 8) & 0xFF] ^ t[1][(high >> 16) & 0xFF] ^ t[0][high >> 24];
            data = data[8..];
        }

        foreach (var b in data)
        {
            crc = t[0][(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[][] BuildTables()
    {
        var tables = new uint[8][];
        tables[0] = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            tables[0][i] = c;
        }

        for (var t = 1; t < 8; t++)
        {
            tables[t] = new uint[256];
            for (var i = 0; i < 256; i++)
            {
                tables[t][i] = (tables[t - 1][i] >> 8) ^ tables[0][tables[t - 1][i] & 0xFF];
            }
        }

        return tables;
    }
}
