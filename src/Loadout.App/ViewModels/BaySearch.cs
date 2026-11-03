using System.Globalization;
using System.Net;
using Loadout.Bay;

namespace Loadout.App;

/// <summary>What a Find Bay search found, and what to add when it found nothing.</summary>
public sealed record BaySearchResult(IReadOnlyList<FoundBay> Found, bool NoNetwork, string CutNote);

/// <summary>
/// One Find Bay search, for Settings, the Installed Games empty state and the
/// Connect fallback: the probe, then the sweep, with what it is doing in words.
/// </summary>
public static class BaySearch
{
    public const string Asking = "Asking the network…";

    public const string NoNetwork = "This PC has no network connection.";

    /// <summary>
    /// Runs the search from the UI thread; <paramref name="note"/> hears "Asking the network…" and then the
    /// sweep's progress, on the UI thread.
    /// </summary>
    public static async Task<BaySearchResult> RunAsync(Action<string> note, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(note);
        var networks = BayDiscovery.LocalNetworks();
        if (networks.Count == 0)
        {
            return new BaySearchResult([], true, "");
        }

        var ranges = networks.Select(BayDiscovery.SweepRangeOf).OfType<SweepRange>().ToList();
        note(Asking);
        // Reports from the sweep's tasks can arrive out of order; the count shown only goes up.
        var shown = -1;
        var progress = new Progress<SweepProgress>(step =>
        {
            if (step.Checked > shown && !cancellationToken.IsCancellationRequested)
            {
                shown = step.Checked;
                note(Checking(ranges, step));
            }
        });
        var found = await BayDiscovery.FindAsync(networks, progress, cancellationToken);
        return new BaySearchResult(found, false, CutNote(ranges));
    }

    /// <summary>"Checking 10.0.0.1 – 10.0.1.254 (312 of 510)…", naming the first range and how many more there are.</summary>
    public static string Checking(IReadOnlyList<SweepRange> ranges, SweepProgress progress)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentNullException.ThrowIfNull(progress);
        var where = ranges.Count == 0 ? "the network"
            : ranges[0].First + " – " + ranges[0].Last
              + (ranges.Count == 1 ? "" : ranges.Count == 2 ? " and 1 more network" : string.Create(CultureInfo.CurrentCulture, $" and {ranges.Count - 1} more networks"));
        return string.Format(CultureInfo.CurrentCulture, "Checking {0} ({1:N0} of {2:N0})…", where, progress.Checked, progress.Total);
    }

    /// <summary>"Searched 10.0.0.0/24 of a larger network." for each range the sweep cut, or empty.</summary>
    public static string CutNote(IReadOnlyList<SweepRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        return string.Join(" ", ranges.Where(range => range.Cut).Select(range =>
            string.Create(CultureInfo.InvariantCulture, $"Searched {range.Network}/{range.PrefixLength} of a larger network.")));
    }

    /// <summary>"10.0.0.60 · Loadout Bay 0.9.0", a line of a list of found Bays.</summary>
    public static string Line(FoundBay bay)
    {
        ArgumentNullException.ThrowIfNull(bay);
        return bay.Address + " · Loadout Bay " + bay.Version;
    }

    public static bool IsAt(FoundBay bay, string host) =>
        IPAddress.TryParse(host, out var address) && address.Equals(bay.Address);
}
