using System.Globalization;
using Loadout.Core;
using Loadout.Images;
using Loadout.Vmc;

namespace Loadout.Bay;

public enum MemoryCardState
{
    /// <summary>No card of the game's name in OPL's VMC folder, and its settings name none.</summary>
    None,

    /// <summary>A card of the name OPL would give the game's card is already in the VMC folder.</summary>
    CardThere,

    /// <summary>The game's settings file already names a card in slot 1.</summary>
    SettingsNameACard,
}

/// <param name="DiscId">The game's disc ID, canonical.</param>
/// <param name="Card">The card OPL would make for the game in slot 1, without <c>.bin</c>.</param>
/// <param name="SettingsCard">The card the game's settings name, or null.</param>
public sealed record MemoryCardPlan(string DiscId, string Card, MemoryCardState State, string? SettingsCard)
{
    public string FileName => Card + ".bin";
}

/// <param name="ReplaceExisting">True when the user chose to replace the cards already there with empty ones.</param>
public sealed record MemoryCardRequest(int SizeMegabytes, IReadOnlyList<MemoryCardPlan> Plans, bool ReplaceExisting);

/// <summary>
/// Gives installed games a virtual memory card in OPL's VMC folder, named as OPL names it, and sets it
/// in slot 1 of the game's settings file. A card that is already there may hold saves: it is kept and
/// used unless the user chose to replace it, and a card the settings already name is left alone.
/// </summary>
public static class BayMemoryCards
{
    /// <summary>Reads OPL's VMC folder and the games' settings files over a connection of its own.</summary>
    public static async Task<IReadOnlyList<MemoryCardPlan>> PlanAsync(
        string host, IEnumerable<QueuePreviewItem> items, int port = BayWire.Port, CancellationToken cancellationToken = default)
    {
        var ids = items.Select(item => DiscId.TryParse(item.DiscId, out var id) ? id : (DiscId?)null).OfType<DiscId>().ToList();
        using var bay = await LoadoutBayClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        return await PlanAsync(bay, ids, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<MemoryCardPlan>> PlanAsync(LoadoutBayClient bay, IReadOnlyList<DiscId> games, CancellationToken cancellationToken = default)
    {
        var cards = (await bay.ListFilesAsync(BayFolder.Vmc, cancellationToken).ConfigureAwait(false)).Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        var settingsFiles = (await bay.ListFilesAsync(BayFolder.Cfg, cancellationToken).ConfigureAwait(false)).Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        var plans = new List<MemoryCardPlan>();
        foreach (var id in games.DistinctBy(id => id.Canonical))
        {
            var settings = settingsFiles.Contains(OplGameSettings.FileName(id))
                ? OplGameSettings.Parse(await bay.ReadFileAsync(BayFolder.Cfg, OplGameSettings.FileName(id), cancellationToken).ConfigureAwait(false))
                : OplGameSettings.Empty;
            var card = OplVmcGroups.CardName(id);
            var state = settings.MemoryCard is not null ? MemoryCardState.SettingsNameACard
                : cards.Contains(card + ".bin") ? MemoryCardState.CardThere
                : MemoryCardState.None;
            plans.Add(new MemoryCardPlan(id.Canonical, card, state, settings.MemoryCard));
        }

        return plans;
    }

    /// <summary>
    /// Gives one game the card its plan names and returns a sentence saying what was done.
    /// <paramref name="madeThisRun"/> holds the cards made earlier in the same queue, so two discs that
    /// share a card get one. A new card that OPL would refuse as fragmented is deleted again.
    /// </summary>
    public static async Task<string> GiveCardAsync(
        LoadoutBayClient bay,
        MemoryCardPlan plan,
        int sizeMegabytes,
        bool replaceExisting,
        ISet<string> madeThisRun,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(madeThisRun);
        if (plan.State == MemoryCardState.SettingsNameACard)
        {
            return "Its OPL settings already name a memory card, " + plan.SettingsCard + ", which was left as it is.";
        }

        string done;
        if (madeThisRun.Contains(plan.Card))
        {
            done = "It shares the memory card made for an earlier game in the queue, VMC/" + plan.FileName + ".";
        }
        else if (plan.State == MemoryCardState.CardThere && !replaceExisting)
        {
            done = "It uses the memory card already on the PS2, VMC/" + plan.FileName + ", with its saves.";
        }
        else
        {
            if (plan.State == MemoryCardState.CardThere)
            {
                await bay.DeleteFileAsync(BayFolder.Vmc, plan.FileName, cancellationToken).ConfigureAwait(false);
            }

            await bay.WriteFileAsync(BayFolder.Vmc, plan.FileName, VmcImage.Create(sizeMegabytes, now), cancellationToken: cancellationToken).ConfigureAwait(false);
            var runs = await bay.GetFileRunsAsync(BayFolder.Vmc, plan.FileName, cancellationToken).ConfigureAwait(false);
            if (runs >= BayWire.VmcMaxRuns)
            {
                await bay.DeleteFileAsync(BayFolder.Vmc, plan.FileName, cancellationToken).ConfigureAwait(false);
                throw new LoadoutBayException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The new card landed in {runs - 1} pieces on the PS2's disk, and OPL takes a card in at most {BayWire.VmcMaxRuns - 2}, so it was deleted again. The free space in OPL's partition is too scattered."),
                    BayWire.StatusNoSpace);
            }

            madeThisRun.Add(plan.Card);
            var size = sizeMegabytes.ToString(CultureInfo.InvariantCulture);
            done = plan.State == MemoryCardState.CardThere
                ? "Its memory card VMC/" + plan.FileName + " was replaced with a new, empty " + size + " MB card."
                : "It got a new " + size + " MB memory card, VMC/" + plan.FileName + ".";
        }

        // Read again, as an earlier game of the queue may have written the same settings file.
        var name = OplGameSettings.FileName(DiscId.TryParse(plan.DiscId, out var id) ? id : throw new ArgumentException("The plan's disc ID does not parse.", nameof(plan)));
        var files = await bay.ListFilesAsync(BayFolder.Cfg, cancellationToken).ConfigureAwait(false);
        var settings = files.Any(file => file.Name == name)
            ? OplGameSettings.Parse(await bay.ReadFileAsync(BayFolder.Cfg, name, cancellationToken).ConfigureAwait(false))
            : OplGameSettings.Empty;
        if (settings.MemoryCard is null)
        {
            await bay.WriteFileAsync(BayFolder.Cfg, name, settings.WithMemoryCard(plan.Card), cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return done;
    }
}
