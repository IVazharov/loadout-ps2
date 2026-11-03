using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Loadout.Artwork;

public enum ArtworkInspectionStatus
{
    Valid,
    Blank,
    Empty,
    NotAnImage,
}

public readonly record struct ArtworkInspection(ArtworkInspectionStatus Status, ArtworkFormat Format, int Width, int Height)
{
    public bool IsCacheable => Status == ArtworkInspectionStatus.Valid;
}

/// <summary>
/// Accepts a non-empty PNG or JPEG. HTML and other error bodies are rejected.
/// A PNG whose pixels are one colour, or fully transparent, is a blank placeholder.
/// </summary>
public static class ArtworkImages
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public static ArtworkInspection Inspect(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            return new ArtworkInspection(ArtworkInspectionStatus.Empty, default, 0, 0);
        }

        if (content.Length > MaxBytes || LooksLikeHtml(content))
        {
            return new ArtworkInspection(ArtworkInspectionStatus.NotAnImage, default, 0, 0);
        }

        if (IsPng(content))
        {
            return InspectPng(content);
        }

        if (IsJpeg(content))
        {
            return InspectJpeg(content);
        }

        return new ArtworkInspection(ArtworkInspectionStatus.NotAnImage, default, 0, 0);
    }

    private static bool LooksLikeHtml(ReadOnlySpan<byte> content)
    {
        var start = 0;
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            start = 3;
        }

        while (start < content.Length && content[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            start++;
        }

        var head = content[start..];
        if (head.Length == 0 || head[0] != (byte)'<')
        {
            return false;
        }

        var sampleLength = Math.Min(head.Length, 64);
        var sample = Encoding.ASCII.GetString(head[..sampleLength]).TrimStart().ToLowerInvariant();
        return sample.StartsWith("<!doctype", StringComparison.Ordinal)
            || sample.StartsWith("<html", StringComparison.Ordinal)
            || sample.StartsWith("<head", StringComparison.Ordinal)
            || sample.StartsWith("<body", StringComparison.Ordinal)
            || sample.StartsWith("<h1", StringComparison.Ordinal);
    }

    private static bool IsPng(ReadOnlySpan<byte> content)
        => content.Length >= 8
            && content[0] == 0x89
            && content[1] == 0x50
            && content[2] == 0x4E
            && content[3] == 0x47
            && content[4] == 0x0D
            && content[5] == 0x0A
            && content[6] == 0x1A
            && content[7] == 0x0A;

    private static bool IsJpeg(ReadOnlySpan<byte> content)
        => content.Length >= 4 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF;

    private static ArtworkInspection InspectJpeg(ReadOnlySpan<byte> content)
    {
        var index = 2;
        var sawFrame = false;
        var width = 0;
        var height = 0;
        while (index < content.Length)
        {
            if (content[index] != 0xFF)
            {
                return NotImage();
            }

            while (index < content.Length && content[index] == 0xFF)
            {
                index++;
            }

            if (index >= content.Length)
            {
                return NotImage();
            }

            var marker = content[index++];
            if (marker == 0xD9)
            {
                return sawFrame
                    ? new ArtworkInspection(ArtworkInspectionStatus.Valid, ArtworkFormat.Jpeg, width, height)
                    : NotImage();
            }

            if (marker == 0x00 || marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            if (index + 1 >= content.Length)
            {
                return NotImage();
            }

            var length = (content[index] << 8) | content[index + 1];
            if (length < 2 || index + length > content.Length)
            {
                return NotImage();
            }

            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (length < 7)
                {
                    return NotImage();
                }

                height = (content[index + 3] << 8) | content[index + 4];
                width = (content[index + 5] << 8) | content[index + 6];
                sawFrame = width > 0 && height > 0;
            }

            if (marker == 0xDA)
            {
                index += length;
                while (index < content.Length)
                {
                    if (content[index] != 0xFF)
                    {
                        index++;
                        continue;
                    }

                    var next = index + 1;
                    while (next < content.Length && content[next] == 0xFF)
                    {
                        next++;
                    }

                    if (next >= content.Length)
                    {
                        return NotImage();
                    }

                    if (content[next] == 0x00)
                    {
                        index = next + 1;
                        continue;
                    }

                    if (content[next] == 0xD9)
                    {
                        return sawFrame
                            ? new ArtworkInspection(ArtworkInspectionStatus.Valid, ArtworkFormat.Jpeg, width, height)
                            : NotImage();
                    }

                    index = next - 1;
                    break;
                }

                continue;
            }

            index += length;
        }

        return NotImage();
    }

    private static ArtworkInspection InspectPng(ReadOnlySpan<byte> content)
    {
        var index = 8;
        var sawHeader = false;
        var sawData = false;
        var sawEnd = false;
        var width = 0;
        var height = 0;
        byte bitDepth = 0;
        byte colorType = 0;
        byte interlace = 0;
        using var data = new MemoryStream();
        while (index + 12 <= content.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(content[index..]);
            if (length < 0 || index + 12 + length > content.Length)
            {
                return NotImage();
            }

            var type = content.Slice(index + 4, 4);
            var chunk = content.Slice(index + 8, length);
            var crcSpan = content.Slice(index + 4, 4 + length);
            var expected = BinaryPrimitives.ReadUInt32BigEndian(content[(index + 8 + length)..]);
            if (Crc32(crcSpan) != expected)
            {
                return NotImage();
            }

            if (Ascii(type, "IHDR"))
            {
                if (length < 13)
                {
                    return NotImage();
                }

                width = BinaryPrimitives.ReadInt32BigEndian(chunk);
                height = BinaryPrimitives.ReadInt32BigEndian(chunk[4..]);
                bitDepth = chunk[8];
                colorType = chunk[9];
                var compression = chunk[10];
                var filter = chunk[11];
                interlace = chunk[12];
                if (width <= 0 || height <= 0 || compression != 0 || filter != 0 || interlace > 1)
                {
                    return NotImage();
                }

                if ((long)width * height > 4_000_000)
                {
                    return NotImage();
                }

                sawHeader = true;
            }
            else if (Ascii(type, "IDAT"))
            {
                data.Write(chunk);
                sawData = true;
            }
            else if (Ascii(type, "IEND"))
            {
                sawEnd = true;
                break;
            }

            index += 12 + length;
        }

        if (!sawHeader || !sawData || !sawEnd)
        {
            return NotImage();
        }

        if (bitDepth != 8 || interlace != 0 || BytesPerPixel(colorType) == 0)
        {
            return new ArtworkInspection(ArtworkInspectionStatus.Valid, ArtworkFormat.Png, width, height);
        }

        byte[] raw;
        try
        {
            raw = Inflate(data.ToArray());
        }
        catch (InvalidDataException)
        {
            return NotImage();
        }

        var bpp = BytesPerPixel(colorType);
        var stride = width * bpp;
        var expectedLength = (long)height * (stride + 1);
        if (raw.Length != expectedLength)
        {
            return NotImage();
        }

        bool blank;
        try
        {
            blank = PixelsAreBlank(raw, width, height, bpp, colorType);
        }
        catch (InvalidDataException)
        {
            return NotImage();
        }

        var status = blank ? ArtworkInspectionStatus.Blank : ArtworkInspectionStatus.Valid;
        return new ArtworkInspection(status, ArtworkFormat.Png, width, height);
    }

    private static bool PixelsAreBlank(byte[] raw, int width, int height, int bpp, byte colorType)
    {
        var stride = width * bpp;
        var previous = new byte[stride];
        var current = new byte[stride];
        var first = new byte[bpp];
        var haveFirst = false;
        var uniform = true;
        var visible = false;
        var alphaIndex = colorType switch
        {
            4 => 1,
            6 => 3,
            _ => -1,
        };

        for (var y = 0; y < height; y++)
        {
            var row = y * (stride + 1);
            var filter = raw[row];
            if (filter > 4)
            {
                throw new InvalidDataException("PNG row filter is not supported.");
            }

            Unfilter(raw.AsSpan(row + 1, stride), previous, current, filter, bpp);
            for (var x = 0; x < width; x++)
            {
                var pixel = current.AsSpan(x * bpp, bpp);
                if (alphaIndex >= 0 && pixel[alphaIndex] != 0)
                {
                    visible = true;
                }

                if (alphaIndex < 0)
                {
                    visible = true;
                }

                if (!haveFirst)
                {
                    pixel.CopyTo(first);
                    haveFirst = true;
                }
                else if (!pixel.SequenceEqual(first))
                {
                    uniform = false;
                }
            }

            (previous, current) = (current, previous);
        }

        if (!visible)
        {
            return true;
        }

        return uniform;
    }

    private static void Unfilter(ReadOnlySpan<byte> filtered, ReadOnlySpan<byte> previous, Span<byte> current, byte filter, int bpp)
    {
        for (var i = 0; i < filtered.Length; i++)
        {
            var left = i >= bpp ? current[i - bpp] : 0;
            var up = previous[i];
            var upLeft = i >= bpp ? previous[i - bpp] : 0;
            var value = filter switch
            {
                0 => filtered[i],
                1 => filtered[i] + left,
                2 => filtered[i] + up,
                3 => filtered[i] + ((left + up) / 2),
                4 => filtered[i] + Paeth(left, up, upLeft),
                _ => filtered[i],
            };
            current[i] = (byte)value;
        }
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var distanceLeft = Math.Abs(estimate - left);
        var distanceUp = Math.Abs(estimate - up);
        var distanceUpLeft = Math.Abs(estimate - upLeft);
        if (distanceLeft <= distanceUp && distanceLeft <= distanceUpLeft)
        {
            return left;
        }

        return distanceUp <= distanceUpLeft ? up : upLeft;
    }

    private static int BytesPerPixel(byte colorType)
        => colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => 0,
        };

    private static byte[] Inflate(byte[] zlib)
    {
        using var input = new MemoryStream(zlib);
        using var decoder = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    private static bool Ascii(ReadOnlySpan<byte> bytes, string text)
    {
        if (bytes.Length != text.Length)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (bytes[i] != (byte)text[i])
            {
                return false;
            }
        }

        return true;
    }

    private static ArtworkInspection NotImage()
        => new(ArtworkInspectionStatus.NotAnImage, default, 0, 0);

    private static uint Crc32(ReadOnlySpan<byte> data)
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
