using System.Globalization;

namespace Loadout.Updates;

/// <summary>
/// A version as VERSION and the release tags write it: major.minor.patch, with an optional pre-release part such
/// as <c>-dev.3</c>, ordered by SemVer 2.0's precedence (so <c>0.10.0-dev.3</c> comes before <c>0.10.0</c>).
/// Build metadata after a <c>+</c> is read and ignored, as SemVer says.
/// </summary>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    private readonly string[] _preRelease;

    private AppVersion(int major, int minor, int patch, string[] preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _preRelease = preRelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The dot-separated identifiers after the <c>-</c>, empty for a release.</summary>
    public IReadOnlyList<string> PreRelease => _preRelease;

    public bool IsPreRelease => _preRelease.Length > 0;

    public static AppVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException("'" + text + "' is not a version such as 1.2.3 or 1.2.3-dev.4.");

    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AppVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            text = text[..plus];
        }

        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var core = dash >= 0 ? text[..dash] : text;
        var parts = core.Split('.');
        if (parts.Length != 3
            || !TryNumber(parts[0], out var major)
            || !TryNumber(parts[1], out var minor)
            || !TryNumber(parts[2], out var patch))
        {
            return false;
        }

        var preRelease = Array.Empty<string>();
        if (dash >= 0)
        {
            preRelease = text[(dash + 1)..].Split('.');
            foreach (var identifier in preRelease)
            {
                if (identifier.Length == 0 || !identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                {
                    return false;
                }

                if (identifier.All(char.IsAsciiDigit) && identifier.Length > 1 && identifier[0] == '0')
                {
                    return false;
                }
            }
        }

        version = new AppVersion(major, minor, patch, preRelease);
        return true;
    }

    private static bool TryNumber(string text, out int value)
    {
        value = 0;
        return text.Length > 0
            && text.All(char.IsAsciiDigit)
            && (text.Length == 1 || text[0] != '0')
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major != other.Major ? Major.CompareTo(other.Major)
            : Minor != other.Minor ? Minor.CompareTo(other.Minor)
            : Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }

        // A pre-release comes before the release of the same numbers.
        if (_preRelease.Length == 0 || other._preRelease.Length == 0)
        {
            return other._preRelease.Length.CompareTo(_preRelease.Length) switch
            {
                < 0 => -1,
                > 0 => 1,
                _ => 0,
            };
        }

        for (var i = 0; i < Math.Min(_preRelease.Length, other._preRelease.Length); i++)
        {
            var order = CompareIdentifiers(_preRelease[i], other._preRelease[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return _preRelease.Length.CompareTo(other._preRelease.Length);
    }

    // Numbers compare as numbers and come before words; words compare by their ASCII codes.
    private static int CompareIdentifiers(string left, string right)
    {
        var leftNumber = left.All(char.IsAsciiDigit);
        var rightNumber = right.All(char.IsAsciiDigit);
        if (leftNumber && rightNumber)
        {
            return left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
        }

        if (leftNumber != rightNumber)
        {
            return leftNumber ? -1 : 1;
        }

        return Math.Sign(string.CompareOrdinal(left, right));
    }

    public bool Equals(AppVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is AppVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major);
        hash.Add(Minor);
        hash.Add(Patch);
        foreach (var identifier in _preRelease)
        {
            hash.Add(identifier, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
        + (_preRelease.Length > 0 ? "-" + string.Join('.', _preRelease) : string.Empty);

    public static bool operator ==(AppVersion? left, AppVersion? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(AppVersion? left, AppVersion? right) => !(left == right);

    public static bool operator <(AppVersion? left, AppVersion? right) => left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(AppVersion? left, AppVersion? right) => left is null || left.CompareTo(right) <= 0;

    public static bool operator >(AppVersion? left, AppVersion? right) => left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(AppVersion? left, AppVersion? right) => left is null ? right is null : left.CompareTo(right) >= 0;
}
