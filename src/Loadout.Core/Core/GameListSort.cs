namespace Loadout.Core;

public static class GameListSort
{
    public static T[] ByTitle<T>(IEnumerable<T> rows, Func<T, string?> title, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(title);
        var ordered = descending
            ? rows.OrderByDescending(row => title(row) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(row => title(row) ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        return ordered.ToArray();
    }
}
