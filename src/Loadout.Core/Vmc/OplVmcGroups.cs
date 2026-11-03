using Loadout.Core;

namespace Loadout.Vmc;

/// <summary>
/// The names OPL's game menu gives virtual memory cards: <c>&lt;group&gt;_&lt;slot&gt;</c>, where the
/// group is the title's startup file name, such as <c>SLUS_202.97</c>, unless OPL puts the title in a
/// group that shares one card (ThirdParty/OplVmcGroups).
/// </summary>
public static class OplVmcGroups
{
    private static readonly Lazy<Dictionary<string, string>> Groups = new(Load);

    /// <summary>The card OPL would make for the title in a slot (0 is slot 1), without <c>.bin</c>.</summary>
    public static string CardName(DiscId discId, int slot = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, 1);
        return GroupOf(discId.OplFileStem) + "_" + slot;
    }

    /// <summary>The group a startup file name such as <c>SLUS_202.97</c> belongs to, or the name itself.</summary>
    public static string GroupOf(string startup) => Groups.Value.TryGetValue(startup, out var group) ? group : startup;

    private static Dictionary<string, string> Load()
    {
        using var stream = typeof(OplVmcGroups).Assembly.GetManifestResourceStream("Loadout.Vmc.vmc-groups.txt")
            ?? throw new InvalidOperationException("The memory card groups are missing from Loadout.Core.");
        using var reader = new StreamReader(stream);
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var title in fields.Skip(1))
            {
                // OPL looks the groups up in order, so a title in two groups belongs to the first.
                groups.TryAdd(title, fields[0]);
            }
        }

        return groups;
    }
}
