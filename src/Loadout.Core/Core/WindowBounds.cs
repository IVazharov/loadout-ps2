using System.Text.Json;
using System.Text.Json.Serialization;

namespace Loadout.Core;

/// <summary>
/// Where the main window was when it closed: its restored rectangle in device pixels, as Windows
/// reports it, and whether it was maximized. Stored as
/// <c>{"left":120,"top":80,"width":2065,"height":1575,"maximized":false}</c>.
/// </summary>
public sealed record WindowBounds(int Left, int Top, int Width, int Height, bool Maximized)
{
    public string ToJson() => JsonSerializer.Serialize(new Stored
    {
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height,
        Maximized = Maximized,
    });

    /// <summary>Null when the text is missing, not bounds, or has no area.</summary>
    public static WindowBounds? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json);
            if (stored?.Left is not int left || stored.Top is not int top
                || stored.Width is not int width || stored.Height is not int height
                || width <= 0 || height <= 0)
            {
                return null;
            }

            return new WindowBounds(left, top, width, height, stored.Maximized ?? false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class Stored
    {
        [JsonPropertyName("left")]
        public int? Left { get; set; }

        [JsonPropertyName("top")]
        public int? Top { get; set; }

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("height")]
        public int? Height { get; set; }

        [JsonPropertyName("maximized")]
        public bool? Maximized { get; set; }
    }
}
