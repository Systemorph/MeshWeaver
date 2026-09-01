#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 No tracked file may contain an unresolved merge-conflict marker.
///
/// <para><b>The measured gap.</b> On 2026-09-01 a conflict resolution in
/// <c>Data/Architecture.md</c> was committed with <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>,
/// <c>=======</c> and <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> still in the file — and the whole
/// documentation suite, <c>DocumentationLinkIntegrityTest</c> included, passed <b>171 of 171</b>
/// over it. Nothing in the repository could see it. The markdown still parsed, every link in the
/// file still resolved, and the only thing wrong was that the page had two half-tables and three
/// lines of git syntax in the middle of it.</para>
///
/// <para><b>Why nothing caught it, and why that generalises.</b> A conflict marker is not a syntax
/// error in Markdown, YAML, JSON or (often) C# — it is *valid text* in the formats this repository
/// is mostly made of. Compilation catches it in a <c>.cs</c> file and nowhere else, so the blast
/// radius is exactly the content the platform ships: doc pages, workflow YAML, allow-files, node
/// JSON. Those are the files a conflict is most likely to hit, because they are the ones several
/// sessions append to at once — a shared topic map is a magnet for exactly this.</para>
///
/// <para><b>What it costs when it escapes.</b> The rendered page shows the markers to whoever opens
/// it, and — worse — one side's additions are silently still there in raw form while looking like
/// content. A reader cannot tell a conflicted table from a badly written one. In a workflow file it
/// is a hard parse failure at the worst moment; in an allow-file it corrupts a ratchet's baseline.</para>
///
/// <para>This is a cheap, total check of the kind AGENTS.md asks for: it asserts an OUTCOME over the
/// real tree rather than a convention, and it cannot pass by not running — an empty file list fails
/// the guard rather than reporting success over nothing.</para>
/// </summary>
public class NoConflictMarkersGuard
{
    /// <summary>
    /// Conflict markers, anchored to the start of a line exactly as git writes them.
    ///
    /// <para>🚨 <c>=======</c> is deliberately NOT in this set. A row of equals signs is legitimate
    /// Markdown — it is setext H1 underlining — and it appears in ASCII rules and table separators
    /// all over this repository. Matching it would make the guard fire on healthy files, and a
    /// guard that cries wolf gets deleted. The two directional markers are unambiguous: nothing but
    /// git writes seven angle brackets at the start of a line.</para>
    /// </summary>
    private static readonly string[] Markers = ["<<<<<<<", ">>>>>>>"];

    /// <summary>
    /// Extensions worth reading. Binary files cannot carry a marker that matters, and scanning the
    /// whole tree would make this slow enough to be skipped.
    /// </summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".cs", ".csproj", ".slnx", ".json", ".yml", ".yaml", ".sh", ".ps1",
        ".props", ".targets", ".razor", ".ts", ".tsx", ".js", ".py", ".allow", ".txt",
    };

    [Fact]
    public void NoTrackedFileCarriesAnUnresolvedConflictMarker()
    {
        var root = FindRepoRoot();
        var tracked = TrackedFiles(root);

        // The guard's own input, asserted. A `git ls-files` that returns nothing (not a checkout,
        // git missing from PATH) would otherwise report a clean tree having read zero files —
        // the "green on no evidence" shape this repository keeps re-learning.
        Assert.True(
            tracked.Count > 500,
            $"only {tracked.Count} tracked file(s) enumerated under {root} — this guard cannot have "
            + "checked anything meaningful. Refusing to report a clean tree over an empty read.");

        var offenders = new List<string>();
        foreach (var relative in tracked)
        {
            if (!TextExtensions.Contains(Path.GetExtension(relative)))
                continue;

            var full = Path.Combine(root, relative);
            if (!File.Exists(full))
                continue;   // a delete staged but not yet written to disk

            var lines = File.ReadAllLines(full);
            for (var i = 0; i < lines.Length; i++)
            {
                if (Markers.Any(m => lines[i].StartsWith(m, StringComparison.Ordinal)))
                    offenders.Add($"{relative}:{i + 1}  {lines[i].Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Unresolved merge-conflict marker(s) in tracked files:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nA conflict marker is valid text in Markdown, YAML and JSON, so nothing else in "
            + "this repository will fail on it — the page simply renders git syntax to whoever "
            + "opens it, with one side's content sitting there looking like prose. Resolve the "
            + "conflict properly: for the Architecture topic map that means UNIONING both sides' "
            + "entries, because each side is usually adding a different page and taking either "
            + "wholesale silently orphans the other one.");
    }

    /// <summary>
    /// The tracked set, from git rather than from a directory walk — so generated output, build
    /// artefacts and other sessions' untracked scratch files cannot fail the build for a tree that
    /// is actually clean.
    /// </summary>
    private static List<string> TrackedFiles(string root)
    {
        var psi = new ProcessStartInfo("git", "ls-files")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var p = Process.Start(psi);
        Assert.NotNull(p);
        var stdout = p!.StandardOutput.ReadToEnd();
        p.WaitForExit();

        Assert.True(p.ExitCode == 0, $"`git ls-files` failed in {root} — this guard reads the tracked set from git.");

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root (no .github directory found)");
        return dir!.FullName;
    }
}
