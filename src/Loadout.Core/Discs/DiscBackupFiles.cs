using System.Text;
using Loadout.Core;

namespace Loadout.Discs;

/// <summary>
/// The files of one backed-up disc, all named after it in one folder: the copy (.iso for a DVD; .bin and .cue for a CD, as
/// Redump keeps them), its report (.report.txt), its hashes (.hashes.txt), its map for going on (.map) and the map as a
/// picture (.map.png).
/// </summary>
public sealed record DiscBackupFiles(string Folder, string Name, bool IsCd = false)
{
    public const string ImageExtension = ".iso";

    public const string CdImageExtension = ".bin";

    public string Image => Path.Combine(Folder, Name + (IsCd ? CdImageExtension : ImageExtension));

    /// <summary>A CD's cue sheet, which names its .bin.</summary>
    public string Cue => Path.Combine(Folder, Name + ".cue");

    public string Report => Path.Combine(Folder, Name + ".report.txt");

    public string Hashes => Path.Combine(Folder, Name + ".hashes.txt");

    public string Map => Path.Combine(Folder, Name + ".map");

    public string MapPicture => Path.Combine(Folder, Name + ".map.png");

    public IReadOnlyList<string> All => IsCd ? [Image, Cue, Report, Hashes, Map, MapPicture] : [Image, Report, Hashes, Map, MapPicture];

    /// <summary>A one-track CD's cue sheet as Redump writes it, so it matches Redump's .cue byte for byte.</summary>
    public string CueText(int mode) => $"FILE \"{Name}{CdImageExtension}\" BINARY\r\n  TRACK 01 MODE{mode}/2352\r\n    INDEX 01 00:00:00\r\n";

    /// <summary>Writes the cue sheet of a CD copy whose data sectors are in this mode (1 or 2; PS2 CDs are mode 2).</summary>
    public void WriteCue(int mode) => File.WriteAllText(Cue, CueText(mode));

    /// <summary>The name of a copy that matches Redump: Redump's own, such as "Need for Speed - ProStreet (Europe, Australia)".</summary>
    public static string NameFor(RedumpGame game) => SafeName(game.Name);

    /// <summary>The name of any other copy: the game's title and disc ID, such as "Need for Speed ProStreet [SLES-55002]".</summary>
    public static string NameFor(string? title, DiscId? discId)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "Unknown game" : title.Trim();
        return SafeName(discId is { } id ? $"{name} [{id.Canonical}]" : name);
    }

    /// <summary>Files named <paramref name="name"/> in the folder, or "name (2)" and so on when that name is taken by any of them.</summary>
    public static DiscBackupFiles Unused(string folder, string name, bool isCd = false)
    {
        var files = new DiscBackupFiles(folder, name, isCd);
        for (var n = 2; Taken(files); n++)
        {
            files = new DiscBackupFiles(folder, $"{name} ({n})", isCd);
        }

        return files;
    }

    // A name is taken by the files of a DVD copy or a CD copy.
    private static bool Taken(DiscBackupFiles files) =>
        (files with { IsCd = false }).All.Concat((files with { IsCd = true }).All).Any(File.Exists);

    /// <summary>
    /// Gives every file that exists a new name, such as Redump's once a copy matches. The name is made unique first.
    /// Returns the files under their new name.
    /// </summary>
    public DiscBackupFiles RenameTo(string name)
    {
        if (string.Equals(name, Name, StringComparison.OrdinalIgnoreCase))
        {
            return this;
        }

        var target = Unused(Folder, name, IsCd);
        foreach (var (from, to) in All.Zip(target.All))
        {
            if (File.Exists(from))
            {
                File.Move(from, to);
            }
        }

        // The cue names its .bin: write it again for the new name.
        if (IsCd && File.Exists(target.Cue))
        {
            target.WriteCue(File.ReadAllText(target.Cue).Contains("MODE1/", StringComparison.Ordinal) ? 1 : 2);
        }

        return target;
    }

    /// <summary>
    /// A name Windows accepts for a file: no \ / : * ? " &lt; &gt; | or control characters (each becomes a space, or ' for "),
    /// no trailing dots or spaces, not a device name such as CON, and at most 120 characters.
    /// </summary>
    public static string SafeName(string name)
    {
        var text = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            text.Append(c switch
            {
                '"' => '\'',
                '\\' or '/' or ':' or '*' or '?' or '<' or '>' or '|' => ' ',
                _ when char.IsControl(c) => ' ',
                _ => c,
            });
        }

        var safe = string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (safe.Length > 120)
        {
            safe = safe[..120];
        }

        safe = safe.TrimEnd('.', ' ');
        if (safe.Length == 0)
        {
            return "Disc";
        }

        var stem = safe.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9"
            ? "_" + safe
            : safe;
    }
}
