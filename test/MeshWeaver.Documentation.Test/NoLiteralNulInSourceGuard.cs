using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard: no text file under the scanned source roots may contain a literal
/// <b>U+0000</b> (NUL) character.
///
/// <para>The scan walks the WORKING TREE (<see cref="ScannedRoots"/>), not the git index — so a
/// file that is merely staged, or not yet added at all, is caught just the same. That is
/// deliberate: the point is to fail before the character is ever committed.</para>
///
/// <para>This is not style policing — a literal NUL in a source file is a latent production failure
/// with an invisible cause (#1449). The repo's own trees are synced into a mesh as nodes whose
/// content IS the file's text; that content is persisted as PostgreSQL <c>jsonb</c>, and jsonb
/// stores DECODED text, which cannot hold a NUL byte. The write dies with
/// <c>22P05: unsupported Unicode escape sequence</c> and a DETAIL the connection policy redacts, so
/// the log names neither the file nor the character. Nothing upstream complains: JSON permits
/// <c>\u0000</c>, <c>System.Text.Json</c> emits it, git stores it, and every editor renders it as
/// nothing at all.</para>
///
/// <para>Both offenders this guard was written against were the same mistake — a composite-key
/// separator typed as a raw NUL instead of an escape:
/// <c>MeshWeaver.Markdown.Export/Html/DocumentAreaResolution.cs</c> (which is what actually failed in
/// production) and <c>clients/react/src/live/grpcSource.ts</c>. Both now use U+001F UNIT SEPARATOR
/// written as an escape — same "cannot appear in a path" property, and representable in jsonb.</para>
///
/// <para>The fix for a failure here is never to suppress it: write the character as an escape
/// (<c>'\u001F'</c> in C#, <c>"\u001F"</c> in TypeScript) so it is visible in review, or drop it.</para>
///
/// <para>🚨 <b>A guard whose predicate is "nothing bad is present" is satisfied by an empty
/// universe</b>, and this one was: it declared a root that does not exist in this repository
/// (<c>content</c>), skipped it silently through <c>Where(Directory.Exists)</c>, and carried no
/// control that its detector detects. A renamed root, a filter that stopped matching, or a
/// scanner that read nothing would all have answered green. So the scan now states its
/// DENOMINATOR (files examined, per root), refuses a declared root that is absent, and
/// <see cref="TheScannerFindsAPlantedNulAndHonoursItsOwnFilters"/> proves the detector, the
/// extension allow-list and the segment exclusions on a throwaway tree — both directions.</para>
/// </summary>
public class NoLiteralNulInSourceGuard
{
    /// <summary>
    /// Repo-root directories that hold hand-authored text — source, mesh node content, the
    /// workflows and scripts CI runs, the charts the fleet deploys. Every entry MUST exist:
    /// <see cref="NoTrackedTextFileContainsALiteralNulCharacter"/> fails on an absent one rather
    /// than skipping it, because a root that scans nothing is indistinguishable from a clean one.
    /// </summary>
    private static readonly string[] ScannedRoots =
        ["src", "test", "samples", "memex", "clients", ".github", "tools", "scripts", "deploy"];

    /// <summary>
    /// Extensions of files a human authors. Deliberately an allow-list: a deny-list would have to
    /// enumerate every binary format that legitimately contains NUL bytes, and would let the next
    /// new text extension through unchecked.
    /// </summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".cshtml", ".csproj", ".slnx", ".props", ".targets",
        ".ts", ".tsx", ".js", ".jsx", ".css", ".scss", ".html",
        ".md", ".json", ".yml", ".yaml", ".xml", ".sql", ".sh", ".ps1", ".py", ".txt"
    };

    private static readonly string[] ExcludedSegments =
        ["bin", "obj", "node_modules", "TestResults", ".git", ".vs", "dist"];

    /// <summary>Written as a cast, not an escape — this file must survive its own guard.</summary>
    private const char Nul = (char)0;

    /// <summary>What one root's scan measured: how many files it examined, and which offended.</summary>
    private sealed record RootScan(string Root, int FilesExamined, IReadOnlyList<string> Offenders);

    [Fact]
    public void NoTrackedTextFileContainsALiteralNulCharacter()
    {
        var root = FindRepoRoot();

        // 🚨 Every declared root exists, or this test cannot say what it scanned. A root that has
        // moved is reported here by name rather than silently contributing zero files.
        var absent = ScannedRoots.Where(r => !Directory.Exists(Path.Combine(root, r))).ToList();
        Assert.True(absent.Count == 0,
            "A scanned root is ABSENT, so the guard would pass having examined nothing under it. "
            + "Either the directory moved (point ScannedRoots at its new home) or it was retired "
            + "(remove it from ScannedRoots) — never leave a root that scans nothing:\n  "
            + string.Join("\n  ", absent));

        var scans = ScannedRoots.Select(r => ScanRoot(root, r)).ToList();

        // The denominator, printed and asserted: a root that matched no text file at all is not a
        // clean root, it is a filter that stopped matching.
        var empty = scans.Where(s => s.FilesExamined == 0).Select(s => s.Root).ToList();
        Assert.True(empty.Count == 0,
            "A scanned root matched ZERO text files, so nothing under it was examined — the "
            + "extension allow-list or the segment exclusions no longer reach it:\n  "
            + string.Join("\n  ", empty));

        var offenders = scans
            .SelectMany(s => s.Offenders)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A literal NUL (U+0000) is present in a source file. It is invisible in every editor and "
            + "makes the file UNSTORABLE once synced into a mesh: PostgreSQL jsonb cannot represent "
            + "U+0000 and rejects the write with 22P05 (#1449). Write the character as an escape "
            + "('\\u001F' for a key separator) or remove it. Offending files:\n  "
            + string.Join("\n  ", offenders)
            + $"\n(examined {scans.Sum(s => s.FilesExamined)} files over {scans.Count} roots: "
            + string.Join(", ", scans.Select(s => $"{s.Root}={s.FilesExamined}")) + ")");
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL. On a throwaway tree the scanner must report exactly the planted
    /// NUL — at its line — and nothing else: a NUL in a file whose extension is not in the
    /// allow-list is not examined, a NUL under an excluded segment is not examined, and a clean
    /// text file counts toward the denominator without offending. A scanner that cannot find a
    /// planted NUL, or that finds one it should not look at, fails here instead of answering the
    /// real tree green or red for the wrong reason.
    /// </summary>
    [Fact]
    public void TheScannerFindsAPlantedNulAndHonoursItsOwnFilters()
    {
        var tree = Path.Combine(Path.GetTempPath(), "nul-guard-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(tree, "scanned");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        try
        {
            // The offender: a NUL on line 3 of an allow-listed extension.
            File.WriteAllText(Path.Combine(root, "sub", "node.json"), "{\n  \"a\": 1,\n  \"b\": \"x" + Nul + "y\"\n}\n");
            // Not examined: the extension is not hand-authored text.
            File.WriteAllText(Path.Combine(root, "sub", "image.png"), "PNG" + Nul + Nul);
            // Not examined: the segment is excluded.
            File.WriteAllText(Path.Combine(root, "bin", "built.cs"), "// " + Nul);
            // Examined and clean: counts, does not offend.
            File.WriteAllText(Path.Combine(root, "clean.cs"), "// unit separator as an escape: \\u001F\n");

            var scan = ScanRoot(tree, "scanned");

            Assert.Equal(2, scan.FilesExamined);
            var offender = Assert.Single(scan.Offenders);
            Assert.Equal(Path.Combine("scanned", "sub", "node.json") + ":3", offender);
        }
        finally
        {
            Directory.Delete(tree, recursive: true);
        }
    }

    /// <summary>Walks one root exactly as the guard does and reports what it examined and found.</summary>
    private static RootScan ScanRoot(string repoRoot, string root)
    {
        var dir = Path.Combine(repoRoot, root);
        var examined = 0;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (!TextExtensions.Contains(Path.GetExtension(file)) || IsExcluded(repoRoot, file))
                continue;
            examined++;
            var line = FirstNulLine(file);
            if (line > 0)
                offenders.Add($"{Path.GetRelativePath(repoRoot, file)}:{line}");
        }
        return new RootScan(root, examined, offenders);
    }

    /// <summary>1-based line number of the first NUL, or 0 when the file holds none.</summary>
    private static int FirstNulLine(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return 0; // a file being written by a concurrent build is not evidence of anything
        }

        var index = text.IndexOf(Nul);
        if (index < 0)
            return 0;

        var line = 1;
        for (var i = 0; i < index; i++)
            if (text[i] == '\n')
                line++;
        return line;
    }

    private static bool IsExcluded(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => ExcludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));

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
