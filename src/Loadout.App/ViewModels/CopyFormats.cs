using Loadout.Persistence;

namespace Loadout.App;

/// <summary>
/// How Back up discs keeps its copies: a DVD as a chd or an iso, a CD as a .bin and .cue or a chd. Kept as they change.
/// A copy is made as an iso or a bin either way; one kept as a chd is compressed once it is complete.
/// </summary>
public sealed class CopyFormats : ObservableObject
{
    public const string Chd = "CHD";
    public const string Iso = "ISO";
    public const string BinCue = "bin/cue";

    private readonly Action<BackupFormats> _keep;
    private bool _dvdAsChd;
    private bool _cdAsChd;

    public CopyFormats(BackupFormats start, Action<BackupFormats> keep)
    {
        ArgumentNullException.ThrowIfNull(start);
        _keep = keep ?? throw new ArgumentNullException(nameof(keep));
        _dvdAsChd = start.DvdAsChd;
        _cdAsChd = start.CdAsChd;
    }

    public IReadOnlyList<string> DvdChoices { get; } = [Chd, Iso];

    public IReadOnlyList<string> CdChoices { get; } = [BinCue, Chd];

    /// <summary>How a DVD's copy is kept: CHD or ISO.</summary>
    public string DvdFormat
    {
        get => _dvdAsChd ? Chd : Iso;
        set => Set(ref _dvdAsChd, value == Chd);
    }

    /// <summary>How a CD's copy is kept: bin/cue or CHD.</summary>
    public string CdFormat
    {
        get => _cdAsChd ? Chd : BinCue;
        set => Set(ref _cdAsChd, value == Chd);
    }

    /// <summary>Whether a copy of a CD, or of a DVD, becomes a chd once it is complete.</summary>
    public bool ChdFor(bool cd) => cd ? _cdAsChd : _dvdAsChd;

    private void Set(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetField(ref field, value, name))
        {
            _keep(new BackupFormats(_dvdAsChd, _cdAsChd));
        }
    }
}
