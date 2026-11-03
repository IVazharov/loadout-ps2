namespace Loadout.Core;

public enum AddGameResult
{
    Added,
    Duplicate,
    InvalidDiscId,
}

/// <summary>
/// In-memory index keyed only by canonical disc ID. A duplicate is refused and
/// the existing record is left unchanged. Replacing a game is a separate, confirmed delete and install.
/// </summary>
public sealed class InstalledGameIndex
{
    private readonly Dictionary<string, InstalledGameRecord> _games = new(StringComparer.Ordinal);

    public IReadOnlyCollection<InstalledGameRecord> Games => _games.Values;

    public void Clear() => _games.Clear();

    public AddGameResult TryAdd(string? discIdText, string? sourceFileName)
    {
        if (!DiscId.TryParse(discIdText, out var discId))
        {
            return AddGameResult.InvalidDiscId;
        }

        if (_games.ContainsKey(discId.Canonical))
        {
            return AddGameResult.Duplicate;
        }

        _games.Add(discId.Canonical, new InstalledGameRecord(discId, sourceFileName ?? string.Empty));
        return AddGameResult.Added;
    }

    public bool TryGet(string? discIdText, out InstalledGameRecord? record)
    {
        record = null;
        if (!DiscId.TryParse(discIdText, out var discId))
        {
            return false;
        }

        return _games.TryGetValue(discId.Canonical, out record);
    }
}

public sealed record InstalledGameRecord(DiscId DiscId, string SourceFileName);
