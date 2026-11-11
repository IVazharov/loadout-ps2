using System.Text.RegularExpressions;

namespace Loadout.Docs;

/// <summary>
/// Keeps the README true to the app: what Loadout downloads, the labels it names, and the files it links to. A failure here
/// means the README needs the same change the code got.
/// </summary>
public partial class ReadmeTests
{
    // Bold phrases in the README that aren't Loadout's own labels: its parts' names, the PS2's requirements, OPL's menu.
    private static readonly string[] NotLabels =
    [
        "Loadout for PS2",
        "Loadout Bay for PS2",
        "A fat PS2",
        "A hard disk formatted for the PS2.",
        "A way to start homebrew and run an ELF from a USB drive",
        "[Open PS2 Loader](https://github.com/ps2homebrew/Open-PS2-Loader)",
        "A USB drive formatted as FAT32",
        "Settings → HDD Device Start Mode",
        "Settings → Applications Start Mode",
    ];

    private static readonly string[] Counts = ["no", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    private static readonly string Root = FindRoot();

    private static readonly string Readme = File.ReadAllText(Path.Combine(Root, "README.md"));

    [Fact]
    public void EveryPlaceLoadoutDownloadsFromIsInWhatLoadoutDownloads()
    {
        var section = Section("What Loadout downloads");
        var sources = Directory.EnumerateFiles(Path.Combine(Root, "src", "Loadout.Core"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .SelectMany(path => File.ReadLines(path).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)))
            .SelectMany(line => WebAddress().Matches(line).Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            var named = NamedBy(new Uri(source));
            Assert.True(section.Contains(named, StringComparison.Ordinal), $"Loadout downloads from {source}, but README's \"What Loadout downloads\" doesn't name \"{named}\".");
        }
    }

    [Fact]
    public void WhatLoadoutDownloadsCountsItsItems()
    {
        var section = Section("What Loadout downloads");
        var items = section.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal));
        var said = Regex.Match(section, @"goes online for (\w+) things only");

        Assert.True(said.Success, "README's \"What Loadout downloads\" no longer says how many things Loadout goes online for.");
        Assert.Equal(Counts[items], said.Groups[1].Value);
    }

    [Fact]
    public void EveryLabelTheReadmeNamesIsInTheApp()
    {
        var app = Directory.EnumerateFiles(Path.Combine(Root, "src", "Loadout.App"), "*.*", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path) && (path.EndsWith(".xaml", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal)))
            .SelectMany(path => Quoted().Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value.Replace("_", string.Empty, StringComparison.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var label in Bold().Matches(Readme).Select(m => m.Groups[1].Value).Distinct().Except(NotLabels))
        {
            Assert.True(app.Contains(label) || app.Contains(label + "…"), $"README names **{label}**, but the app has no such label.");
        }
    }

    [Fact]
    public void EveryFileTheReadmeLinksToIsThere()
    {
        foreach (var link in LocalLink().Matches(Readme).Select(m => m.Groups[1].Value))
        {
            var path = link.Split('#')[0];
            Assert.True(File.Exists(Path.Combine(Root, path)), $"README links to {link}, which is not in the repository.");
        }
    }

    // How the README names a source: a GitHub repository by owner/name, Loadout's own releases as GitHub's, anything else by host.
    private static string NamedBy(Uri source)
    {
        var parts = source.AbsolutePath.Trim('/').Split('/');
        return source.Host switch
        {
            "api.github.com" => "releases from GitHub",
            "github.com" or "raw.githubusercontent.com" => parts[0] + "/" + parts[1],
            _ => source.Host,
        };
    }

    private static string Section(string heading)
    {
        var start = Readme.IndexOf("## " + heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"README has no \"{heading}\" section.");
        var end = Readme.IndexOf("\n## ", start + 3, StringComparison.Ordinal);
        return Readme[start..(end < 0 ? Readme.Length : end)].Replace("\r", string.Empty, StringComparison.Ordinal);
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Loadout.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("The repository's root (Loadout.slnx) was not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex("\"(https?://[^\"]+)\"")]
    private static partial Regex WebAddress();

    [GeneratedRegex("\"([^\"\\r\\n]*)\"")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"\]\((?!https?:|#)([^)\s]+)\)")]
    private static partial Regex LocalLink();
}
