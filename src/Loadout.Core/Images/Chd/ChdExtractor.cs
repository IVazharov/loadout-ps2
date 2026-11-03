using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Loadout.Images;

/// <summary>
/// Writes a chd out as the files chdman's extractdvd and extractcd write, byte for byte: an iso for
/// a DVD, a cue sheet and one bin for a CD.
/// </summary>
internal static class ChdExtractor
{
    private const int TrackPadding = 4;
    private const int FramesPerSecond = 75;
    private const int OutputBuffer = 1024 * 1024;

    private static readonly Regex TrackPattern = new(
        @"^TRACK:(\d+) TYPE:(\S+) SUBTYPE:(\S+) FRAMES:(\d+)(?: PREGAP:(\d+) PGTYPE:(\S+) PGSUB:(\S+) POSTGAP:(\d+))?",
        RegexOptions.CultureInvariant);

    public static void ToIso(ChdFile chd, string isoPath, Action<double>? progress, CancellationToken cancellationToken)
    {
        using var output = new FileStream(isoPath, FileMode.Create, FileAccess.Write, FileShare.None, OutputBuffer);
        chd.ReadAll((buffer, count) => output.Write(buffer, 0, count), progress, cancellationToken);
    }

    public static void ToCueAndBin(ChdFile chd, string cuePath, string binPath, Action<double>? progress, CancellationToken cancellationToken)
    {
        var tracks = ReadTracks(chd);
        using (var output = new FileStream(binPath, FileMode.Create, FileAccess.Write, FileShare.None, OutputBuffer))
        {
            var writer = new FrameWriter(tracks, output);
            chd.ReadAll(writer.Write, progress, cancellationToken);
            if (!writer.Finished)
            {
                throw new ChdException("This chd is damaged: it holds less data than its track list says.");
            }
        }

        // The sheet comes last, so a sheet always has its data.
        File.WriteAllText(cuePath, Sheet(tracks, Path.GetFileName(binPath)), new UTF8Encoding(false));
    }

    internal static IReadOnlyList<ChdTrack> ReadTracks(ChdFile chd)
    {
        var tracks = new List<ChdTrack>();
        foreach (var entry in chd.Metadata.Where(entry => entry.Tag is "CHTR" or "CHT2"))
        {
            var text = Encoding.ASCII.GetString(entry.Data).TrimEnd('\0');
            var match = TrackPattern.Match(text);
            if (!match.Success)
            {
                throw new ChdException("This chd is damaged: its track list does not make sense.");
            }

            var type = match.Groups[2].Value;
            var dataBytes = DataBytes(type);
            var frames = Number(match.Groups[4]);
            var pregap = match.Groups[5].Success ? Number(match.Groups[5]) : 0;
            var pregapType = match.Groups[6].Success ? match.Groups[6].Value : string.Empty;
            var pregapInFile = pregap > 0 && pregapType.StartsWith('V') && DataBytes(pregapType[1..]) > 0;
            var postgap = match.Groups[8].Success ? Number(match.Groups[8]) : 0;
            if (dataBytes == 0 || frames < 0 || Number(match.Groups[1]) != tracks.Count + 1)
            {
                throw new ChdException("This chd is damaged: its track list does not make sense.");
            }

            var padded = (frames + TrackPadding - 1) / TrackPadding * TrackPadding;
            tracks.Add(new ChdTrack(type, dataBytes, frames, padded - frames, pregap, pregapInFile, postgap));
        }

        if (tracks.Count == 0)
        {
            throw new ChdException("This chd keeps its track list in an old form that Loadout does not read. chdman can update it with its copy command.");
        }

        return tracks;
    }

    private static int Number(Group group) =>
        int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ChdException("This chd is damaged: its track list does not make sense.");

    private static int DataBytes(string type) => type switch
    {
        "MODE1" or "MODE1/2048" or "MODE2_FORM1" or "MODE2/2048" => 2048,
        "MODE1_RAW" or "MODE1/2352" or "MODE2_RAW" or "MODE2/2352" or "CDI/2352" or "AUDIO" => 2352,
        "MODE2" or "MODE2/2336" or "MODE2_FORM_MIX" => 2336,
        "MODE2_FORM2" or "MODE2/2324" => 2324,
        _ => 0,
    };

    private static string Sheet(IReadOnlyList<ChdTrack> tracks, string binName)
    {
        var sheet = new StringBuilder();
        sheet.Append("FILE \"").Append(binName).Append("\" BINARY\n");
        var frameOffset = 0;
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            var mode = track.Type == "AUDIO"
                ? "AUDIO"
                : (track.Type.StartsWith("MODE1", StringComparison.Ordinal) ? "MODE1/" : "MODE2/") + track.DataBytes.ToString("D4", CultureInfo.InvariantCulture);
            sheet.Append("  TRACK ").Append((index + 1).ToString("D2", CultureInfo.InvariantCulture)).Append(' ').Append(mode).Append('\n');
            if (track.Pregap > 0 && !track.PregapInFile)
            {
                sheet.Append("    PREGAP ").Append(Msf(track.Pregap)).Append('\n');
                sheet.Append("    INDEX 01 ").Append(Msf(frameOffset)).Append('\n');
            }
            else if (track.Pregap > 0)
            {
                sheet.Append("    INDEX 00 ").Append(Msf(frameOffset)).Append('\n');
                sheet.Append("    INDEX 01 ").Append(Msf(frameOffset + track.Pregap)).Append('\n');
            }
            else
            {
                sheet.Append("    INDEX 01 ").Append(Msf(frameOffset)).Append('\n');
            }

            if (track.Postgap > 0)
            {
                sheet.Append("    POSTGAP ").Append(Msf(track.Postgap)).Append('\n');
            }

            frameOffset += track.Frames;
        }

        // chdman on Windows ends its lines this way.
        return sheet.Replace("\n", "\r\n").ToString();
    }

    private static string Msf(int frames) => string.Create(
        CultureInfo.InvariantCulture,
        $"{frames / (60 * FramesPerSecond):D2}:{frames / FramesPerSecond % 60:D2}:{frames % FramesPerSecond:D2}");

    /// <summary>Takes the image's frames in order, and writes each track's data without subcode or padding.</summary>
    private sealed class FrameWriter
    {
        private readonly IReadOnlyList<ChdTrack> _tracks;
        private readonly Stream _output;
        private readonly byte[] _swapped = new byte[ChdCdCodec.SectorBytes];
        private int _track;
        private int _frame;

        public FrameWriter(IReadOnlyList<ChdTrack> tracks, Stream output)
        {
            _tracks = tracks;
            _output = output;
        }

        public bool Finished => _track == _tracks.Count;

        public void Write(byte[] buffer, int count)
        {
            for (var offset = 0; offset + ChdCdCodec.FrameBytes <= count && !Finished; offset += ChdCdCodec.FrameBytes)
            {
                var track = _tracks[_track];
                if (_frame < track.Frames)
                {
                    WriteFrame(buffer, offset, track);
                }

                _frame++;
                if (_frame >= track.Frames + track.ExtraFrames)
                {
                    _track++;
                    _frame = 0;
                }
            }
        }

        private void WriteFrame(byte[] buffer, int offset, ChdTrack track)
        {
            if (track.Type != "AUDIO")
            {
                _output.Write(buffer, offset, track.DataBytes);
                return;
            }

            // A chd keeps audio big-endian and a bin little-endian.
            for (var index = 0; index < track.DataBytes; index += 2)
            {
                _swapped[index] = buffer[offset + index + 1];
                _swapped[index + 1] = buffer[offset + index];
            }

            _output.Write(_swapped, 0, track.DataBytes);
        }
    }
}

internal sealed record ChdTrack(string Type, int DataBytes, int Frames, int ExtraFrames, int Pregap, bool PregapInFile, int Postgap);
