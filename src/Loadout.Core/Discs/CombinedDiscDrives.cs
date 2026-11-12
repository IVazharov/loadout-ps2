namespace Loadout.Discs;

/// <summary>Several sets of drives as one, such as two simulated drives for testing: each call goes to the set that has the drive.</summary>
public sealed class CombinedDiscDrives(IReadOnlyList<IDiscDrives> parts) : IDiscDrives
{
    private readonly IReadOnlyList<IDiscDrives> _parts = parts is { Count: > 0 } ? parts : throw new ArgumentException("No drives to combine.", nameof(parts));

    public DiscCopierSettings? CopierSettings => _parts[0].CopierSettings;

    public bool IsSimulated => _parts.All(part => part.IsSimulated);

    public IReadOnlyList<string> List() => [.. _parts.SelectMany(part => part.List())];

    public string Fingerprint(string drive) => PartOf(drive)?.Fingerprint(drive) ?? string.Empty;

    public DiscInDrive? Look(string drive) => PartOf(drive)?.Look(drive);

    public IDiscReader Open(string drive) => PartOf(drive)?.Open(drive) ?? throw new IOException($"There is no drive {drive}.");

    public bool TryEject(string drive) => PartOf(drive)?.TryEject(drive) ?? false;

    private IDiscDrives? PartOf(string drive) =>
        _parts.FirstOrDefault(part => part.List().Any(name => string.Equals(name.TrimEnd('\\'), drive.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)));
}
