using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard: no tracked text file may contain a literal <b>U+0000</b> (NUL) character.
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
/// </summary>
public class NoLiteralNulInSourceGuard
{
    /// <summary>Repo-root directories that hold hand-authored text (source and mesh node content).</summary>
    private static readonly string[] ScannedRoots =
        ["src", "test", "samples", "content", "memex", "clients"];

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

    [Fact]
    public void NoTrackedTextFileContainsALiteralNulCharacter()
    {
        var root = FindRepoRoot();

        var offenders = ScannedRoots
            .Select(r => Path.Combine(root, r))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Where(f => TextExtensions.Contains(Path.GetExtension(f)))
            .Where(f => !IsExcluded(root, f))
            .Select(f => (File: f, Line: FirstNulLine(f)))
            .Where(x => x.Line > 0)
            .Select(x => $"  {Path.GetRelativePath(root, x.File)}:{x.Line}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A literal NUL (U+0000) is present in tracked text. It is invisible in every editor and "
            + "makes the file UNSTORABLE once synced into a mesh: PostgreSQL jsonb cannot represent "
            + "U+0000 and rejects the write with 22P05 (#1449). Write the character as an escape "
            + "('\\u001F' for a key separator) or remove it. Offending files:\n"
            + string.Join("\n", offenders));
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
