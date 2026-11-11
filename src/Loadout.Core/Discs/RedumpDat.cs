using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace Loadout.Discs;

/// <summary>One file of a disc as Redump lists it: an .iso for a DVD, a .bin and a .cue for a CD.</summary>
public sealed record RedumpRom(string Name, long Size, string Crc32, string Md5, string Sha1);

/// <summary>One disc in Redump's list, by its official name, such as "Need for Speed - ProStreet (Europe, Australia)".</summary>
public sealed record RedumpGame(string Name, string Category, IReadOnlyList<RedumpRom> Roms);

public enum RedumpVerdict
{
    /// <summary>Size and SHA-1 are those of a disc in Redump's list: the copy is exact.</summary>
    Matches,

    /// <summary>
    /// Redump lists discs of this size, but none with these contents: another printing of the game, or a read error the
    /// drive did not notice. Reading the disc again tells them apart: two equal reads point to another printing.
    /// </summary>
    DoesNotMatch,

    /// <summary>Redump lists no disc of this size.</summary>
    NotInRedump,
}

/// <summary>What Redump's list says about a copy.</summary>
public sealed record RedumpCheck(RedumpVerdict Verdict, RedumpGame? Game, IReadOnlyList<RedumpGame> SameSize);

/// <summary>
/// Redump's list of PS2 discs (its "datfile", http://redump.org/datfile/ps2/): every verified dump with the size and hashes
/// of its files. A PS2 DVD read as 2048-byte sectors is exactly Redump's .iso, so a copy is checked by its size and SHA-1.
/// </summary>
public sealed class RedumpDat
{
    private readonly Dictionary<string, (RedumpGame Game, RedumpRom Rom)> _bySha1;
    private readonly Dictionary<long, List<RedumpGame>> _bySize;

    private RedumpDat(string version, IReadOnlyList<RedumpGame> games)
    {
        Version = version;
        Games = games;
        _bySha1 = new Dictionary<string, (RedumpGame, RedumpRom)>(StringComparer.OrdinalIgnoreCase);
        _bySize = [];
        foreach (var game in games)
        {
            foreach (var rom in game.Roms.Where(r => !r.Name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)))
            {
                _bySha1.TryAdd(rom.Sha1, (game, rom));
                if (!_bySize.TryGetValue(rom.Size, out var list))
                {
                    _bySize[rom.Size] = list = [];
                }

                if (!list.Contains(game))
                {
                    list.Add(game);
                }
            }
        }
    }

    /// <summary>The list's date as Redump gives it, such as "2026-06-15 03-41-38".</summary>
    public string Version { get; }

    public IReadOnlyList<RedumpGame> Games { get; }

    /// <summary>Reads the list from Redump's zip, which holds one .dat file. Throws <see cref="InvalidDataException"/> when it is not one.</summary>
    public static RedumpDat ReadZip(Stream zip)
    {
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The zip has no .dat file in it.");
            using var dat = entry.Open();
            return Parse(dat);
        }
        catch (IOException ex)
        {
            throw new InvalidDataException($"Redump's list could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>Reads the list's XML. Throws <see cref="InvalidDataException"/> when it is not Redump's list of discs.</summary>
    public static RedumpDat Parse(Stream xml)
    {
        var settings = new XmlReaderSettings
        {
            // The file names Logiqx's DTD; nothing in it is needed, and nothing is fetched.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };
        var games = new List<RedumpGame>();
        var version = string.Empty;
        var sawDatafile = false;
        try
        {
            using var reader = XmlReader.Create(xml, settings);
            reader.MoveToContent();
            while (!reader.EOF)
            {
                // Reading an element's text or a whole game leaves the reader on the next node already.
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Name == "datafile")
                    {
                        sawDatafile = true;
                    }
                    else if (reader.Name == "version" && reader.Depth == 2)
                    {
                        version = reader.ReadElementContentAsString().Trim();
                        continue;
                    }
                    else if (reader.Name == "game")
                    {
                        games.Add(ReadGame(reader));
                        continue;
                    }
                }

                reader.Read();
            }
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"Redump's list is not valid XML: {ex.Message}", ex);
        }

        if (!sawDatafile || games.Count == 0)
        {
            throw new InvalidDataException("This is not Redump's list of discs.");
        }

        return new RedumpDat(version, games);
    }

    /// <summary>Checks a copy by its size and SHA-1 (lowercase or uppercase hex).</summary>
    public RedumpCheck Check(long size, string sha1)
    {
        if (_bySha1.TryGetValue(sha1, out var hit) && hit.Rom.Size == size)
        {
            return new RedumpCheck(RedumpVerdict.Matches, hit.Game, [hit.Game]);
        }

        return _bySize.TryGetValue(size, out var sameSize)
            ? new RedumpCheck(RedumpVerdict.DoesNotMatch, null, sameSize)
            : new RedumpCheck(RedumpVerdict.NotInRedump, null, []);
    }

    private static RedumpGame ReadGame(XmlReader reader)
    {
        var name = reader.GetAttribute("name") ?? string.Empty;
        var category = string.Empty;
        var roms = new List<RedumpRom>();
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return new RedumpGame(name, category, roms);
        }

        var depth = reader.Depth;
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "category")
            {
                category = reader.ReadElementContentAsString().Trim();
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element
                && reader.Name == "rom"
                && long.TryParse(reader.GetAttribute("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                && reader.GetAttribute("sha1") is { Length: 40 } sha1)
            {
                roms.Add(new RedumpRom(
                    reader.GetAttribute("name") ?? string.Empty,
                    size,
                    reader.GetAttribute("crc") ?? string.Empty,
                    reader.GetAttribute("md5") ?? string.Empty,
                    sha1.ToLowerInvariant()));
            }

            reader.Read();
        }

        // Past the game's end.
        reader.Read();
        return new RedumpGame(name, category, roms);
    }
}
