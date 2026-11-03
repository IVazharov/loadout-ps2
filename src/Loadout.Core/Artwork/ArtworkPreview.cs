namespace Loadout.Artwork;

/// <summary>
/// Image data for a WPF <c>Image</c> control. The control binding stays in the app project.
/// </summary>
public sealed class ArtworkPreview
{
    private ArtworkPreview(string? filePath, byte[]? bytes)
    {
        FilePath = filePath;
        Bytes = bytes;
    }

    public string? FilePath { get; }

    public byte[]? Bytes { get; }

    public static ArtworkPreview FromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A preview path is required.", nameof(filePath));
        }

        return new ArtworkPreview(filePath, null);
    }

    public static ArtworkPreview FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            throw new ArgumentException("Preview bytes are empty.", nameof(bytes));
        }

        return new ArtworkPreview(null, bytes.ToArray());
    }
}
