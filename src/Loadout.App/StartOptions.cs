using System.Diagnostics;
using System.Globalization;

namespace Loadout.App;

/// <summary>
/// What the Loadout that updated itself tells the new one on its command line: the version it replaced, its
/// process (which the new one waits for, so they don't share the window or the database), and whether it was
/// connected to Loadout Bay, so the new one connects again. For testing, <c>--simulate-disc &lt;image&gt;</c> gives Back
/// up discs a simulated drive S: with that image as a scratched disc, and <c>--simulate-dirt from-to,...</c> its dirty
/// sectors.
/// </summary>
public sealed record StartOptions(string? UpdatedFrom, int? WaitFor, bool Reconnect)
{
    public const string UpdatedFromSwitch = "--updated-from";
    public const string WaitForSwitch = "--wait-for";
    public const string ReconnectSwitch = "--reconnect";
    public const string SimulateDiscSwitch = "--simulate-disc";
    public const string SimulateDirtSwitch = "--simulate-dirt";

    /// <summary>A disc image for a simulated drive on Back up discs, for testing; null for the PC's own drives.</summary>
    public string? SimulatedDisc { get; init; }

    /// <summary>The simulated disc's dirty sectors, such as "568320-568720,1482880-1483280"; null for two default stretches.</summary>
    public string? SimulatedDirt { get; init; }

    public static StartOptions Current { get; private set; } = new(null, null, false);

    public static StartOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? from = null;
        int? waitFor = null;
        var reconnect = false;
        string? simulated = null;
        string? dirt = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == UpdatedFromSwitch && i + 1 < args.Count)
            {
                from = args[++i];
            }
            else if (args[i] == WaitForSwitch && i + 1 < args.Count
                && int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                waitFor = pid;
            }
            else if (args[i] == ReconnectSwitch)
            {
                reconnect = true;
            }
            else if (args[i] == SimulateDiscSwitch && i + 1 < args.Count)
            {
                simulated = args[++i];
            }
            else if (args[i] == SimulateDirtSwitch && i + 1 < args.Count)
            {
                dirt = args[++i];
            }
        }

        return new StartOptions(from, waitFor, reconnect) { SimulatedDisc = simulated, SimulatedDirt = dirt };
    }

    /// <summary>Reads this process's command line, and waits up to 20 s for the Loadout it replaced to close.</summary>
    public static void Load(IReadOnlyList<string> args)
    {
        Current = Parse(args);
        if (Current.WaitFor is not { } pid)
        {
            return;
        }

        try
        {
            using var old = Process.GetProcessById(pid);
            old.WaitForExit(TimeSpan.FromSeconds(20));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>The command line for the new Loadout after an update.</summary>
    public static IReadOnlyList<string> AfterUpdate(string oldVersion, bool reconnect)
    {
        var args = new List<string> { UpdatedFromSwitch, oldVersion, WaitForSwitch, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) };
        if (reconnect)
        {
            args.Add(ReconnectSwitch);
        }

        return args;
    }
}
