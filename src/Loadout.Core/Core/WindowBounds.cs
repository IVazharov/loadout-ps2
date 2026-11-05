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
    /// <summary>The window moved, and shrunk if it must be, to lie wholly inside the area.</summary>
    public WindowBounds FitWithin(int left, int top, int width, int height)
    {
        var fittedWidth = Math.Min(Width, width);
        var fittedHeight = Math.Min(Height, height);
        return this with
        {
            Left = Math.Clamp(Left, left, left + width - fittedWidth),
            Top = Math.Clamp(Top, top, top + height - fittedHeight),
            Width = fittedWidth,
            Height = fittedHeight,
        };
    }

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
