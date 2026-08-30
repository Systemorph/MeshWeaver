using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// The scanning half of a governance ratchet: locate the repo, enumerate its source files, blank out
/// anything that is prose rather than code, and read the seeded allow file.
///
/// <para>Extracted so a second ratchet does not have to reimplement it. That is not tidiness — the
/// masker is the part that decides whether a guard is measuring code or its own documentation, and
/// this repo's remarks quote the exact shapes being ratcheted (the impersonation seam quotes
/// <c>Observable.Using(access.ImpersonateAsSystem</c>; half the blocking-bridge sites are named in
/// comments explaining why they were removed). A second, subtly different copy would give a second,
/// subtly wrong inventory — and a ratchet whose counts are wrong is worse than none, because it reads
/// as evidence.</para>
/// </summary>
internal static class SourceScan
{
    private static readonly string[] ExcludedSegments =
        ["bin", "obj", "node_modules", "TestResults", ".git", ".vs", "dist"];

    /// <summary>The repo root, found by walking up from the test binary until <c>MeshWeaver.slnx</c>.</summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException(
                   "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }

    /// <summary>Repo-relative, forward-slashed — the key form every allow file uses.</summary>
    public static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    public static bool IsExcluded(string root, string path) =>
        Relative(root, path).Split('/').Any(s => ExcludedSegments.Contains(s, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Every C#/Razor/script source file under the named roots, build output excluded.
    ///
    /// <para>🚨 <c>.csx</c> is in the list, and it is the one that is easy to forget. A script
    /// template such as <c>src/MeshWeaver.Graph/Templates/Mirror.csx</c> is REAL PRODUCTION CODE
    /// that compiles at RUNTIME in the portal and is invisible to <c>dotnet build</c> — the blind
    /// spot AGENTS.md names ("green CI does NOT mean the mesh compiles"). A ratchet that scanned
    /// only <c>.cs</c> would report a tree as clean while the same defect sat in a file no compiler
    /// ever looks at. Two <c>.ToTask(</c> sites were found in exactly that position.</para>
    /// </summary>
    /// <remarks>
    /// 🚨 <b>An EMPTY result is treated as a BROKEN SCAN, not as a clean tree (#2844).</b>
    ///
    /// <para>The <c>Where(Directory.Exists)</c> below silently drops a root that is not there. That
    /// is deliberate — a caller may name an optional root — but it means that pointing this scanner
    /// at the wrong tree drops EVERY root and yields an empty sequence. For the ~30 zero-tolerance
    /// guards built on it, "no offenders found" and "nothing was scanned" are then the same result,
    /// so all of them report green while enforcing nothing.</para>
    ///
    /// <para>That is not hypothetical: relocating <c>MeshWeaver.Documentation.Test</c> into another
    /// repository — a one-line csproj move considered on 2026-08-30 — would have disarmed the lot,
    /// silently and permanently. Only 4 of 34 guards asserted that their scan found anything.</para>
    ///
    /// <para>🚨 A planted-tree self-test does NOT cover this. Running the real scanner over a temp
    /// directory proves the SCANNER works; it cannot prove the scanner found the PRODUCTION tree.
    /// The two fail independently, and only the second produces a wall of green over an unenforced
    /// rule. Hence the check here, at the one place every guard passes through, rather than 30
    /// individually-tuned floors — a rule copied 30 times is how 30 came to lack it.</para>
    /// </remarks>
    public static IEnumerable<string> SourceFiles(string root, IEnumerable<string> scannedRoots)
    {
        var roots = scannedRoots as string[] ?? scannedRoots.ToArray();
        var files = roots
            .Select(r => Path.Combine(root, r))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Where(f => Path.GetExtension(f) is ".cs" or ".razor" or ".csx")
            .Where(f => !IsExcluded(root, f))
            .ToArray();

        if (files.Length == 0)
        {
            var missing = roots.Where(r => !Directory.Exists(Path.Combine(root, r))).ToArray();
            throw new InvalidOperationException(
                $"SourceScan found NO source files under '{root}' for root(s) "
                + $"[{string.Join(", ", roots)}]"
                + (missing.Length == 0
                    ? " — every root exists but contains no .cs/.razor/.csx. "
                    : $" — these do not exist: [{string.Join(", ", missing)}]. ")
                + "🚨 This is a BROKEN SCAN, not a clean tree. A guard that reports green here has "
                + "checked nothing: the repo root resolved somewhere unexpected (a relocated test "
                + "project, a shadow-copied binary, a renamed root). Fix the scan — never soften "
                + "the guard that surfaced it (#2844).");
        }

        return files;
    }

    /// <summary>
    /// Blanks comment and string-literal characters, preserving offsets and newlines, so the scan
    /// sees code only. Handles line/block comments, ordinary, verbatim and raw string literals, and
    /// character literals — enough that a doc comment containing <c>Plugins/*</c> does not swallow
    /// the next thousand lines (which is exactly what a naive scanner did).
    /// </summary>
    public static string MaskCommentsAndStrings(string text)
    {
        var masked = new StringBuilder(text);
        var i = 0;
        var n = text.Length;

        void Blank(int from, int to)
        {
            for (var k = from; k < to && k < n; k++)
                if (text[k] != '\n')
                    masked[k] = ' ';
        }

        while (i < n)
        {
            var c = text[i];
            var next = i + 1 < n ? text[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                var end = text.IndexOf('\n', i);
                end = end < 0 ? n : end;
                Blank(i, end);
                i = end;
            }
            else if (c == '/' && next == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? n : end + 2;
                Blank(i, end);
                i = end;
            }
            else if (c == '"' && i + 2 < n && text[i + 1] == '"' && text[i + 2] == '"')
            {
                var end = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                end = end < 0 ? n : end + 3;
                Blank(i, end);
                i = end;
            }
            else if (c == '@' && next == '"')
            {
                i = MaskVerbatim(text, masked, i + 1, Blank);
            }
            else if (c == '$' && next == '@' && i + 2 < n && text[i + 2] == '"')
            {
                Blank(i, i + 2);
                i = MaskVerbatim(text, masked, i + 2, Blank);
            }
            else if (c is '"' or '\'')
            {
                var quote = c;
                Blank(i, i + 1);
                i++;
                while (i < n && text[i] != quote && text[i] != '\n')
                {
                    var step = text[i] == '\\' ? 2 : 1;
                    Blank(i, i + step);
                    i += step;
                }

                if (i < n && text[i] == quote) { Blank(i, i + 1); i++; }
            }
            else
            {
                i++;
            }
        }

        return masked.ToString();
    }

    /// <summary>Masks a verbatim string starting at its opening quote; <c>""</c> is an escaped quote.</summary>
    private static int MaskVerbatim(string text, StringBuilder masked, int quote, Action<int, int> blank)
    {
        var n = text.Length;
        blank(quote, quote + 1);
        var i = quote + 1;
        while (i < n)
        {
            if (text[i] == '"')
            {
                if (i + 1 < n && text[i + 1] == '"') { blank(i, i + 2); i += 2; continue; }
                blank(i, i + 1);
                return i + 1;
            }

            blank(i, i + 1);
            i++;
        }

        return n;
    }

    /// <summary>The text between <paramref name="openParen"/> and the first comma at its own nesting
    /// depth (or the matching close paren) — i.e. the call's first argument.</summary>
    public static string FirstArgument(string code, int openParen)
    {
        var depth = 0;
        for (var i = openParen; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (--depth == 0) return code[(openParen + 1)..i];
                    break;
                case ',' when depth == 1:
                    return code[(openParen + 1)..i];
            }
        }

        return code[(openParen + 1)..];
    }

    /// <summary>
    /// <c>relative/path.cs&lt;TAB&gt;count</c>, one per line; <c>#</c> starts a comment.
    /// </summary>
    public static Dictionary<string, int> ReadAllowFile(string path, string allowFileName)
    {
        Assert.True(File.Exists(path),
            $"{allowFileName} is missing — without it this guard cannot tell a pre-existing site "
            + "from a new one and would report every occurrence as a failure. Restore it from git "
            + "rather than regenerating it: a regenerated file would silently bless whatever is in "
            + "the tree, which is the one thing a ratchet must never do.");

        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t', StringSplitOptions.TrimEntries))
            .ToDictionary(parts => parts[0], parts => int.Parse(parts[1]), StringComparer.Ordinal);
    }
}
