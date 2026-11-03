namespace Loadout.Persistence;

public sealed record PortableLayout(
    string SettingsFile,
    string DatabaseFile,
    string ConversionCache,
    string ArtworkCache);

/// <summary>
/// The settings file sits next to the executable. The database and caches sit in a data folder there.
/// </summary>
public static class PortableData
{
    public const string SettingsFileName = "Loadout.settings.json";

    public const string DataFolderName = "data";

    public const string DatabaseFileName = "loadout.sqlite";

    public static PortableLayout Resolve(string exeDirectory)
    {
        if (string.IsNullOrWhiteSpace(exeDirectory))
        {
            throw new ArgumentException("The executable folder is required.", nameof(exeDirectory));
        }

        var settings = Path.Combine(exeDirectory, SettingsFileName);
        var data = Path.Combine(exeDirectory, DataFolderName);
        Directory.CreateDirectory(data);
        if (!File.Exists(settings))
        {
            File.WriteAllText(settings, "{\"dataFolder\":\"data\"}\n");
        }

        return new PortableLayout(
            settings,
            Path.Combine(data, DatabaseFileName),
            Path.Combine(data, "conversion-cache"),
            Path.Combine(data, "artwork-cache"));
    }
}
