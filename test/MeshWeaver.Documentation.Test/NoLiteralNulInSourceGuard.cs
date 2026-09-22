using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
/// (<c>content</c>), skipped it silently through <c>Where(Directory.Exists)</c>, counted a file it
/// could not read as clean, and carried no control that its detector detects. A renamed root, a
/// filter that stopped matching, or a scanner that read nothing would all have answered green. So
/// the scan now states its DENOMINATOR (files examined, per root — written to the test output on
/// the success path too, since xUnit prints an assertion message only on failure), refuses a
/// declared root that is absent, refuses a file it could not read, and
/// <see cref="TheScannerFindsAPlantedNulAndHonoursItsOwnFilters"/> proves the detector, the
/// text classification and the segment exclusions on a throwaway tree — both directions.</para>
/// </summary>
public class NoLiteralNulInSourceGuard(ITestOutputHelper output)
{
    /// <summary>
    /// Repo-root directories that hold hand-authored text — source, mesh node content, the
    /// workflows and scripts CI runs, the charts the fleet deploys. Every entry MUST exist:
    /// <see cref="NoTrackedTextFileContainsALiteralNulCharacter"/> fails on an absent one rather
    /// than skipping it, because a root that scans nothing is indistinguishable from a clean one.
    /// </summary>
    private static readonly ImmutableArray<string> ScannedRoots =
        ["src", "test", "samples", "memex", "clients", ".github", "tools", "scripts", "deploy"];

    /// <summary>
    /// Extensions of files a human authors. Deliberately an allow-list: a deny-list would have to
    /// enumerate every binary format that legitimately contains NUL bytes, and would let the next
    /// new text extension through unchecked. Measured over the scanned roots — every text
    /// extension present is listed; the ones deliberately NOT listed are binary by nature
    /// (<c>.png</c>, <c>.jpeg</c>, <c>.xlsx</c>, <c>.docx</c>, <c>.ttf</c>, <c>.tflite</c>, <c>.pyc</c>).
    /// </summary>
    private static readonly ImmutableHashSet<string> TextExtensions = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        ".cs", ".csx", ".razor", ".cshtml", ".csproj", ".slnx", ".props", ".targets",
        ".ts", ".tsx", ".js", ".jsx", ".css", ".scss", ".html", ".svg",
        ".md", ".json", ".yml", ".yaml", ".xml", ".toml", ".conf", ".plist", ".proto", ".csv",
        ".sql", ".sh", ".ps1", ".py", ".rb", ".txt", ".allow", ".bicep", ".tpl", ".example",
        ".gitignore", ".helmignore");

    private static readonly ImmutableHashSet<string> ExcludedSegments = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "bin", "obj", "node_modules", "TestResults", ".git", ".vs", "dist");

    /// <summary>Written as a cast, not an escape — this file must survive its own guard.</summary>
    private const char Nul = (char)0;

    /// <summary>
    /// What one root's scan measured: how many files it examined, which offended, and which it
    /// could not read — the last is a failure of the SCAN, never a clean result.
    /// </summary>
    private sealed record RootScan(
        string Root, int FilesExamined, IReadOnlyList<string> Offenders, IReadOnlyList<string> Unreadable);

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

        // The denominator — on the SUCCESS path as well. xUnit prints an assertion message only
        // when the assertion fails, so a clean run that reported its counts nowhere else would
        // read exactly like a run that examined nothing.
        var denominator = $"examined {scans.Sum(s => s.FilesExamined)} files over {scans.Count} roots: "
                          + string.Join(", ", scans.Select(s => $"{s.Root}={s.FilesExamined}"));
        output.WriteLine($"NoLiteralNulInSourceGuard: {denominator}");

        // A root that matched no text file at all is not a clean root, it is a filter that stopped
        // matching.
        var empty = scans.Where(s => s.FilesExamined == 0).Select(s => s.Root).ToList();
        Assert.True(empty.Count == 0,
            "A scanned root matched ZERO text files, so nothing under it was examined — the "
            + "text classification or the segment exclusions no longer reach it:\n  "
            + string.Join("\n  ", empty));

        // A file the scan could not READ is not evidence of anything, so it is a failure of the
        // scan, named — never counted as clean.
        var unreadable = scans.SelectMany(s => s.Unreadable).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.True(unreadable.Count == 0,
            "The scan could not READ these files, so it cannot vouch for them; a file it cannot "
            + "open is not a clean file. Make it readable or exclude it deliberately:\n  "
            + string.Join("\n  ", unreadable));

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
            + $"\n({denominator})");
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL. On a throwaway tree the scanner must report exactly the planted
    /// NULs — at their lines — and nothing else: a NUL in a file whose extension is not
    /// hand-authored text is not examined, a NUL under an excluded segment is not examined, an
    /// extensionless file (a <c>Dockerfile</c>, a shim script) IS examined, and a clean text file
    /// counts toward the denominator without offending. A scanner that cannot find a planted NUL,
    /// or that finds one it should not look at, fails here instead of answering the real tree
    /// green or red for the wrong reason.
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
            // Offender 1: a NUL on line 3 of an allow-listed extension.
            File.WriteAllText(Path.Combine(root, "sub", "node.json"), "{\n  \"a\": 1,\n  \"b\": \"x" + Nul + "y\"\n}\n");
            // Offender 2: a NUL on line 2 of an EXTENSIONLESS file — examined by shape, not by name.
            File.WriteAllText(Path.Combine(root, "Dockerfile"), "FROM scratch\nRUN " + Nul + "\n");
            // Not examined: the extension is binary by nature.
            File.WriteAllText(Path.Combine(root, "sub", "image.png"), "PNG" + Nul + Nul);
            // Not examined: the segment is excluded.
            File.WriteAllText(Path.Combine(root, "bin", "built.cs"), "// " + Nul);
            // Examined and clean: counts, does not offend.
            File.WriteAllText(Path.Combine(root, "clean.cs"), "// unit separator as an escape: \\u001F\n");

            var scan = ScanRoot(tree, "scanned");

            Assert.Equal(3, scan.FilesExamined);
            Assert.Empty(scan.Unreadable);
            Assert.Equal(
                new[]
                {
                    Path.Combine("scanned", "Dockerfile") + ":2",
                    Path.Combine("scanned", "sub", "node.json") + ":3",
                },
                scan.Offenders.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            Directory.Delete(tree, recursive: true);
        }
    }

    /// <summary>
    /// Whether a file is hand-authored text by SHAPE: an allow-listed extension, or no extension
    /// at all (a <c>Dockerfile</c>, a <c>CODEOWNERS</c>, a shim script). Measured over the
    /// scanned roots, every extensionless file is text; a binary one checked in without an
    /// extension fails this guard loudly, which is the direction an allow-list should err in.
    /// </summary>
    private static bool IsHandAuthoredText(string file)
    {
        var extension = Path.GetExtension(file);
        return extension.Length == 0 || TextExtensions.Contains(extension);
    }

    /// <summary>Walks one root exactly as the guard does and reports what it examined and found.</summary>
    private static RootScan ScanRoot(string repoRoot, string root)
    {
        var dir = Path.Combine(repoRoot, root);
        var examined = 0;
        var offenders = new List<string>();
        var unreadable = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (!IsHandAuthoredText(file) || IsExcluded(repoRoot, file))
                continue;
            examined++;
            var relative = Path.GetRelativePath(repoRoot, file);
            switch (FirstNulLine(file))
            {
                case null:
                    unreadable.Add(relative);
                    break;
                case > 0 and var line:
                    offenders.Add($"{relative}:{line}");
                    break;
            }
        }
        return new RootScan(root, examined, offenders, unreadable);
    }

    /// <summary>
    /// 1-based line number of the first NUL, 0 when the file holds none, or <c>null</c> when the
    /// file could not be read — which the caller reports as a scan failure, never as clean.
    /// </summary>
    private static int? FirstNulLine(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
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
            .Any(ExcludedSegments.Contains);

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
