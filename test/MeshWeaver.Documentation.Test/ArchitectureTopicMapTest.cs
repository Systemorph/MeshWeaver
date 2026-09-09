using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 The topic map in <c>Data/Architecture.md</c> must LIST every architecture page, and must keep
/// one entry per line.
///
/// <para><b>Why the second half matters as much as the first (#3699).</b> The map used to be a table
/// whose cells each carried the whole entry list on ONE line — the longest was 5,877 characters
/// holding 34 entries. Adding a page meant appending to that line, so two doc pull requests
/// conflicted BY CONSTRUCTION however unrelated their subjects: 24 commits touched the file in one
/// day and the conflict was hand-resolved four times, twice on PRs that had gone <c>DIRTY</c> and
/// were therefore running no CI at all.</para>
///
/// <para><b>And the failure was SILENT.</b> Keeping one side of such a merge drops the other side's
/// page from the index. The page stays reachable by URL, so
/// <c>DocumentationLinkIntegrityTest</c> still passes — it checks that links RESOLVE, not that pages
/// are LISTED. The page simply loses its only discoverable entry point and no gate says a word.
/// That is the hole this guard closes, and it is the guard the issue asked for rather than the
/// reformat alone.</para>
/// </summary>
public class ArchitectureTopicMapTest
{
    [Fact]
    public void EveryArchitecturePage_IsListedInTheTopicMap()
    {
        var (map, dir) = ReadMap();
        var pages = dir.GetFiles("*.md", SearchOption.AllDirectories)
            .Select(f => Path.ChangeExtension(Path.GetRelativePath(dir.FullName, f.FullName), null)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray();
        Assert.NotEmpty(pages);
        var unlisted = Unlisted(map, pages);

        Assert.True(unlisted.Count == 0,
            "Architecture pages missing from Data/Architecture.md. Add each page under its theme; "
            + "a working URL alone does not make it discoverable.\n  "
            + string.Join("\n  ", unlisted));
    }

    [Fact]
    public void Coverage_RequiresTheCorrectPathForNestedPages()
    {
        var missing = Unlisted(
            "- [Elsewhere](/Doc/Other/Page)\n- [Top level](Page)",
            ["Page", "Nested/Page"]);
        Assert.Equal(new[] { "Nested/Page" }, missing);
        Assert.Empty(Unlisted("- [Nested](Nested/Page)", ["Nested/Page"]));
    }

    [Fact]
    public void Coverage_ResolvesArchitectureLinksWithFragments()
    {
        Assert.Empty(Unlisted(
            "- [One](/Doc/Architecture/One#details)\n- [Two](./Nested/Two.md#example)",
            ["One", "Nested/Two"]));
    }

    [Fact]
    public void TheTopicMap_KeepsOneEntryPerLine()
    {
        var (map, _) = ReadMap();
        var crowded = map.Split('\n')
            .Select((text, i) => (Line: i + 1, text))
            .Where(l => l.text.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            .Where(l => Regex.Matches(l.text, @"\]\([^)]+\)").Count != 1)
            .Select(l => $"line {l.Line}: {l.text.Trim()[..Math.Min(120, l.text.Trim().Length)]}…")
            .ToList();

        crowded.Should().BeEmpty(
            "each topic-map bullet must carry exactly ONE linked entry so that two doc PRs appending pages touch "
            + "DIFFERENT lines and git merges them unaided. Packing several entries onto one line "
            + "restores the conflict-by-construction this format exists to remove — and its silent "
            + "failure, where a hand-merge keeps one side and drops the other side's page from the "
            + "index with every gate still green (#3699)");
    }

    /// <summary>Architecture pages with no entry, retaining their path below Architecture/.</summary>
    private static List<string> Unlisted(string map, IEnumerable<string> pages)
    {
        var listed = Regex.Matches(map, @"\]\(([^)]+)\)")
            .Select(m => NormalizeArchitectureLink(m.Groups[1].Value))
            .Where(path => path is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return pages.Where(path => !listed.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static string? NormalizeArchitectureLink(string href)
    {
        var path = href.Split('#')[0].TrimEnd('/');
        const string prefix = "/Doc/Architecture/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            path = path[prefix.Length..];
        else if (path.StartsWith('/') || path.Contains(':'))
            return null;
        if (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            path = path[..^3];
        return path;
    }

    private static (string Map, DirectoryInfo Dir) ReadMap()
    {
        var root = FindRepoRoot();
        var data = Path.Combine(root, "src", "MeshWeaver.Documentation", "Data");
        var map = File.ReadAllText(Path.Combine(data, "Architecture.md"));
        var dir = new DirectoryInfo(Path.Combine(data, "Architecture"));
        Assert.True(dir.Exists, $"Data/Architecture not found under {data} — the guard would pass vacuously");
        return (map, dir);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }
}
