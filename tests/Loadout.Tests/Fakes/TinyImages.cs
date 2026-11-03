using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Loadout.Fakes;

internal static class TinyImages
{
    public static byte[] Png(params (byte R, byte G, byte B)[] pixels)
    {
        var width = pixels.Length;
        var raw = new byte[1 + (width * 3)];
        raw[0] = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            raw[1 + (i * 3)] = pixels[i].R;
            raw[2 + (i * 3)] = pixels[i].G;
            raw[3 + (i * 3)] = pixels[i].B;
        }

        return EncodePng(width, 1, raw);
    }

    public static byte[] SolidPng(byte r, byte g, byte b)
        => Png((r, g, b), (r, g, b));

    public static byte[] FilteredPng()
    {
        var raw = new byte[]
        {
            1,
            10, 20, 30,
            1, 2, 3,
        };
        return EncodePng(2, 1, raw);
    }

    public static readonly byte[] Jpeg =
    [
        255, 216, 255, 224, 0, 16, 74, 70, 73, 70, 0, 1, 2, 0, 0, 1, 0, 1, 0, 0, 255, 254, 0, 16, 76, 97, 118, 99, 54, 48, 46, 51, 49, 46, 49, 48, 50, 0, 255, 219, 0, 67, 0, 8, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7, 8, 8, 8, 7, 7, 7, 6, 6, 7, 7, 8, 8, 8, 8, 9, 9, 9, 8, 8, 8, 8, 9, 9, 10, 10, 10, 12, 12, 11, 11, 14, 14, 14, 17, 17, 20, 255, 196, 0, 76, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 6, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 6, 7, 16, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 17, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 192, 0, 17, 8, 0, 2, 0, 2, 3, 1, 34, 0, 2, 17, 0, 3, 17, 0, 255, 218, 0, 12, 3, 1, 0, 2, 17, 3, 17, 0, 63, 0, 139, 0, 81, 127, 127, 255, 217,
    ];

    public static readonly byte[] Html = "<html><body>not a cover</body></html>"u8.ToArray();

    private static byte[] EncodePng(int width, int height, byte[] raw)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(typeBytes);
        output.Write(data);
        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        output.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                var mask = (uint)-(int)(crc & 1);
                crc = (crc >> 1) ^ (0xEDB88320u & mask);
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
