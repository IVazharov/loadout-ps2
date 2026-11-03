using System.Buffers.Binary;
using System.Text;

namespace Loadout.Fakes;

/// <summary>
/// Writes an uncompressed chd, so a test can put any disc image in one. As in one chdman makes, it
/// has no checksums. ChdFileTests covers the compressed kinds, with files chdman made.
/// </summary>
internal static class ChdTestFile
{
    private const int HeaderBytes = 124;
    private const int FrameBytes = 2448;
    private const int SectorBytes = 2352;

    public static void WriteDvd(string path, byte[] image, int trailingBytes = 0)
        => Write(path, image, 4096, 2048, [("DVD ", [0])], trailingBytes);

    public static void WriteTagged(string path, byte[] image, string tag)
        => Write(path, image, 4096, 2048, [(tag, [0])], 0);

    /// <summary>A CD of whole 2352 byte sectors. A sheet's tracks are given as their type and frames.</summary>
    public static void WriteCd(string path, byte[] sectors, params (string Type, int Frames)[] tracks)
    {
        var padded = tracks.Sum(track => (track.Frames + 3) / 4 * 4);
        var logical = new byte[padded * FrameBytes];
        var source = 0;
        var frame = 0;
        foreach (var track in tracks)
        {
            for (var index = 0; index < track.Frames; index++)
            {
                sectors.AsSpan(source, SectorBytes).CopyTo(logical.AsSpan(frame * FrameBytes));
                source += SectorBytes;
                frame++;
            }

            frame = (frame + 3) / 4 * 4;
        }

        var metadata = tracks
            .Select((track, index) => ("CHT2", Encoding.ASCII.GetBytes(
                $"TRACK:{index + 1} TYPE:{track.Type} SUBTYPE:NONE FRAMES:{track.Frames} PREGAP:0 PGTYPE:MODE1 PGSUB:NONE POSTGAP:0\0")))
            .ToArray();
        Write(path, logical, FrameBytes * 8, FrameBytes, metadata, 0);
    }

    private static void Write(string path, byte[] logical, int hunkBytes, int unitBytes, (string Tag, byte[] Data)[] metadata, int trailingBytes)
    {
        var hunks = (logical.Length + hunkBytes - 1) / hunkBytes;
        var header = new byte[HeaderBytes];
        "MComprHD"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), HeaderBytes);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(32), (ulong)logical.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(40), HeaderBytes);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(56), (uint)hunkBytes);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(60), (uint)unitBytes);

        // A hunk of zeros needs no room: its map entry stays zero.
        var map = new byte[hunks * 4];
        var firstHunk = ((HeaderBytes + map.Length + hunkBytes - 1) / hunkBytes) + 0;
        using var body = new MemoryStream();
        var next = firstHunk;
        var hunk = new byte[hunkBytes];
        for (var index = 0; index < hunks; index++)
        {
            Array.Clear(hunk);
            var length = Math.Min(hunkBytes, logical.Length - (index * hunkBytes));
            logical.AsSpan(index * hunkBytes, length).CopyTo(hunk);
            if (hunk.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                continue;
            }

            BinaryPrimitives.WriteUInt32BigEndian(map.AsSpan(index * 4), (uint)next);
            body.Write(hunk);
            next++;
        }

        var metadataStart = (long)next * hunkBytes;
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(48), (ulong)metadataStart);
        using var file = File.Create(path);
        file.Write(header);
        file.Write(map);
        file.Write(new byte[(firstHunk * hunkBytes) - HeaderBytes - map.Length]);
        body.Position = 0;
        body.CopyTo(file);

        var position = metadataStart;
        for (var index = 0; index < metadata.Length; index++)
        {
            var (tag, data) = metadata[index];
            var entry = new byte[16];
            Encoding.Latin1.GetBytes(tag, entry);
            BinaryPrimitives.WriteInt32BigEndian(entry.AsSpan(4), data.Length);
            position += entry.Length + data.Length;
            BinaryPrimitives.WriteUInt64BigEndian(entry.AsSpan(8), index == metadata.Length - 1 ? 0 : (ulong)position);
            file.Write(entry);
            file.Write(data);
        }

        file.Write(new byte[trailingBytes]);
    }
}
