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
/// real tree rather than a convention, and it cannot pass by not running — it asserts a sentinel
/// path was actually enumerated rather than trusting that the scan found something.</para>
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
    /// Extensions that cannot meaningfully carry a marker. Everything else is read.
    ///
    /// <para>🚨 A BLOCKLIST, not an allow-list, and the distinction is the point. An allow-list of
    /// "text" extensions silently skips whatever nobody thought of — <c>.csx</c> scripts, and every
    /// extensionless tracked file: <c>.gitignore</c>, <c>.editorconfig</c>, <c>.gitattributes</c>,
    /// <c>CODEOWNERS</c>, <c>LICENSE</c>, <c>Dockerfile</c>. Those are ordinary conflict targets,
    /// and a guard reporting green over them is the same "checked nothing" failure it exists to
    /// prevent, one level in.</para>
    /// </summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp", ".webp", ".pdf", ".zip", ".gz", ".tgz",
        ".dll", ".exe", ".pdb", ".so", ".dylib", ".nupkg", ".snk", ".woff", ".woff2", ".ttf",
        ".otf", ".eot", ".mp4", ".mov", ".webm", ".mp3", ".wav", ".bin", ".dat",
    };

    /// <summary>
    /// A path every checkout of this repository has. Asserting it was enumerated proves the scan
    /// ran against the real tree — deterministically, where a "more than N files" floor only
    /// guesses at it and drifts as the repository grows.
    /// </summary>
    private const string SentinelPath = "AGENTS.md";

    [Fact]
    public void NoTrackedFileCarriesAnUnresolvedConflictMarker()
    {
        var root = FindRepoRoot();
        var tracked = TrackedFiles(root);

        // The guard's own input, asserted against a path that MUST exist rather than a count that
        // merely looks plausible. A `git ls-files` returning nothing (not a checkout, git missing
        // from PATH, wrong working directory) would otherwise report a clean tree having read zero
        // files — the "green on no evidence" shape this repository keeps re-learning.
        Assert.True(
            tracked.Contains(SentinelPath),
            $"`git ls-files` under {root} did not enumerate {SentinelPath}, so this guard did not "
            + "scan the repository. Refusing to report a clean tree over a read that found nothing.");

        var offenders = new List<string>();
        var symlinks = new List<string>();
        foreach (var relative in tracked)
        {
            if (BinaryExtensions.Contains(Path.GetExtension(relative)))
                continue;

            var full = Path.Combine(root, relative);
            if (!File.Exists(full))
                continue;   // a delete staged but not yet written to disk

            // 🚨 A SYMLINK IS NOT THIS GUARD'S BUSINESS, AND A DANGLING ONE USED TO KILL IT.
            // `File.Exists` answers TRUE for a tracked symlink entry while `File.OpenRead` throws
            // `FileNotFoundException` on the missing target, so the scan died on the first one and
            // never reached its assertion — reporting a stack trace under the name
            // "NoTrackedFileCarriesAnUnresolvedConflictMarker", which reads as a merge problem and
            // is not one. MeshWeaver#3343 sat red for a day on exactly that: it had committed 37
            // absolute symlinks into a developer's own checkout, and the guard named none of them.
            //
            // A symlink's CONTENT is its target path; it cannot carry a conflict marker, so
            // skipping it loses no coverage. What it must not do is fail silently — a link is
            // still something nobody meant to commit, which is why the sibling assertion below
            // reports them rather than this loop swallowing them.
            var info = new FileInfo(full);
            if (info.LinkTarget is not null)
            {
                symlinks.Add($"{relative} → {info.LinkTarget}");
                continue;
            }

            // Extension lists are guesses; a NUL byte is evidence. Catches an unlisted binary
            // without the blocklist having to know its name.
            if (LooksBinary(full))
                continue;

            var lines = File.ReadAllLines(full);
            for (var i = 0; i < lines.Length; i++)
            {
                if (Markers.Any(m => lines[i].StartsWith(m, StringComparison.Ordinal)))
                    offenders.Add($"{relative}:{i + 1}  {lines[i].Trim()}");
            }
        }

        // Reported SEPARATELY from the markers, and after them, because they are a different
        // defect with a different fix. An ABSOLUTE link is never legitimate here — it names one
        // machine's filesystem — and this repository tracks none at all, so the bar is simply zero.
        Assert.True(
            symlinks.Count == 0,
            "Tracked symlink(s), which this repository does not use:\n  "
            + string.Join("\n  ", symlinks)
            + "\n\nThese are almost always local dev-convenience links caught by a `git add -A` — "
            + "an absolute one names the committer's own checkout and resolves nowhere else, so "
            + "every other clone and every CI run gets a dangling entry. `git rm --cached` them, "
            + "and consider a .gitignore entry so the next `add -A` cannot repeat it.");

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

    /// <summary>A NUL byte in the first 8 KB — the standard, name-independent binary test.</summary>
    private static bool LooksBinary(string path)
    {
        using var s = File.OpenRead(path);
        Span<byte> head = stackalloc byte[8192];
        var read = s.Read(head);
        return head[..read].IndexOf((byte)0) >= 0;
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
