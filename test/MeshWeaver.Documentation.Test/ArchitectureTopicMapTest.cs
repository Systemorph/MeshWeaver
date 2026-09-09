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
    /// <summary>
    /// How many architecture pages the topic map does NOT list, as measured when this guard was
    /// written (2026-09-09: 291 pages under Data/Architecture/, 168 distinct pages listed).
    ///
    /// <para>🚨 <b>A ratchet, not a budget, and RAISING IT IS NOT A FIX.</b> Zero is the right
    /// number and 125 is what the tree actually had the day the hole was first measured — nobody had
    /// counted before, because nothing asked. Listing all 125 is a classification exercise (which
    /// theme does each page belong under?) that a guard cannot do and should not fake by seeding an
    /// allow list: an unexplained allow entry IS the silent unlisting this exists to prevent, just
    /// moved somewhere greener. So the count is pinned here and may only go DOWN, which gives the
    /// property that matters immediately — a NEW page cannot be added without being listed.</para>
    /// </summary>
    private const int UnlistedBaseline = 125;

    [Fact]
    public void EveryArchitecturePage_IsListedInTheTopicMap()
    {
        var unlisted = Unlisted();

        Assert.True(unlisted.Count <= UnlistedBaseline,
            $"{unlisted.Count} architecture page(s) are listed nowhere in the topic map, up from the "
            + $"baseline of {UnlistedBaseline}. A page reachable by URL but listed nowhere has lost "
            + "its only discoverable entry point, and no other gate notices — "
            + "DocumentationLinkIntegrityTest checks that links RESOLVE, not that pages are LISTED. "
            + "Add an entry to Data/Architecture.md under the theme it belongs to. Do NOT raise this "
            + "baseline: it may only go down.\n  "
            + string.Join("\n  ", unlisted.Except(new string[0]).Take(20)));
    }

    /// <summary>
    /// Caps the slack, so the baseline cannot quietly become permanent headroom: if the tree has
    /// fallen well below it, the baseline is stale and should be lowered in the same change that
    /// earned it. Same shape as <c>TestTimeoutLiteralRatchetGuard</c>'s companion.
    /// </summary>
    [Fact]
    public void TheBaselineStaysCloseToTheTree()
    {
        var actual = Unlisted().Count;
        Assert.True(UnlistedBaseline - actual <= 10,
            $"the topic map now omits only {actual} page(s) but UnlistedBaseline is still "
            + $"{UnlistedBaseline} — lower it to {actual}. A baseline left far above the tree is "
            + "headroom a future change can spend without any gate objecting, which is exactly what "
            + "a ratchet is meant to stop.");
    }

    [Fact]
    public void TheTopicMap_KeepsOneEntryPerLine()
    {
        var (map, _) = ReadMap();
        var crowded = map.Split('\n')
            .Select((text, i) => (Line: i + 1, text))
            .Where(l => l.text.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            .Where(l => Regex.Matches(l.text, @"\]\([^)]+\)").Count > 1)
            .Select(l => $"line {l.Line}: {l.text.Trim()[..Math.Min(120, l.text.Trim().Length)]}…")
            .ToList();

        crowded.Should().BeEmpty(
            "the topic map carries ONE entry per line so that two doc PRs appending pages touch "
            + "DIFFERENT lines and git merges them unaided. Packing several entries onto one line "
            + "restores the conflict-by-construction this format exists to remove — and its silent "
            + "failure, where a hand-merge keeps one side and drops the other side's page from the "
            + "index with every gate still green (#3699)");
    }

    /// <summary>Architecture pages with no entry in the topic map, by file name.</summary>
    private static List<string> Unlisted()
    {
        var (map, dir) = ReadMap();
        var listed = Regex.Matches(map, @"\]\(([^)]+)\)")
            .Select(m => m.Groups[1].Value)
            .Select(href => href.Split('#')[0].TrimEnd('/'))
            .Select(href => href.Contains('/') ? href[(href.LastIndexOf('/') + 1)..] : href)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return dir.GetFiles("*.md", SearchOption.TopDirectoryOnly)
            .Select(f => Path.GetFileNameWithoutExtension(f.Name))
            .Where(name => !listed.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
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
