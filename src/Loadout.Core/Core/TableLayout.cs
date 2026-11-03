using System.Text.Json;

namespace Loadout.Core;

/// <summary>
/// What a table remembers between runs: its column order and the widths the user set by hand,
/// by column id. Stored as <c>{"order":["art","discId",...],"widths":{"title":340}}</c>.
/// </summary>
public sealed record TableLayout(IReadOnlyList<string> Order, IReadOnlyDictionary<string, double> Widths)
{
    public string ToJson() => JsonSerializer.Serialize(new Stored { Order = [.. Order], Widths = new Dictionary<string, double>(Widths) });

    /// <summary>Null when the text is missing or not a layout.</summary>
    public static TableLayout? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json);
            if (stored is null)
            {
                return null;
            }

            var widths = (stored.Widths ?? [])
                .Where(pair => double.IsFinite(pair.Value) && pair.Value > 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return new TableLayout((stored.Order ?? []).Where(id => !string.IsNullOrEmpty(id)).ToArray(), widths);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The ids in the saved order. Ids the table no longer has are dropped, and ids the saved
    /// order lacks keep their default place after the ones it names.
    /// </summary>
    public IReadOnlyList<string> Arrange(IReadOnlyList<string> defaultOrder)
    {
        ArgumentNullException.ThrowIfNull(defaultOrder);
        var known = Order.Where(defaultOrder.Contains).Distinct(StringComparer.Ordinal).ToList();
        known.AddRange(defaultOrder.Where(id => !known.Contains(id, StringComparer.Ordinal)));
        return known;
    }

    private sealed class Stored
    {
        [System.Text.Json.Serialization.JsonPropertyName("order")]
        public List<string>? Order { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("widths")]
        public Dictionary<string, double>? Widths { get; set; }
    }
}
