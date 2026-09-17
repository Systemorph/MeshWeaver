using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet for the publish-by-copy defect class (MeshWeaver#2190):
/// <c>File.Move(staged, target, overwrite: false)</c> used as an atomic publication.
///
/// <para><b>It is not one.</b> On Unix .NET renames only while the rename SUCCEEDS; on failure it
/// tries <c>link(2)</c> and, where the volume has no hard links — Azure Files over SMB, which is
/// every portal's <c>/data</c> — it COPIES the staged file into the target, opening the target with
/// <c>FileShare.None</c> (an exclusive <c>flock</c>) and filling it afterwards
/// (<c>FileSystem.Unix.cs</c>, <c>LinkOrCopyFile</c>). For the length of that copy the target name
/// exists, holds incomplete bytes and is exclusively locked. Measured on the portal image's own
/// runtime: 21,573 reads of such a target failed with <i>"being used by another process"</i> and
/// 52,619 read it incomplete when the lock was not visible (a reader on another node — the share is
/// mounted <c>nobrl</c>). memex logged exactly that for an activation verdict on 2026-09-16, and the
/// replica that read it booted without the module.</para>
///
/// <para>The one permitted spelling is <c>MeshWeaver.Utils.NoReplaceMove.TryMove</c>: it renames
/// without replacing, falls back to a hard link where the volume has no such rename, and REFUSES
/// (throws) where it has neither — it contains no copy. <c>File.Move(…, overwrite: true)</c> is a
/// different operation (deliberate replacement) and stays allowed: it is a plain <c>rename(2)</c>
/// whose only fallback is across devices, which a sibling staging file cannot hit.</para>
/// </summary>
public class NoReplaceMoveRatchetGuard
{
    private static readonly string[] ScannedRoots = ["src", "tools", "samples", "memex"];

    /// <summary>The one file that may spell the BCL call without <c>overwrite: true</c>: the
    /// primitive itself, whose Windows step is <c>MoveFileEx</c> and is a rename on one volume.</summary>
    private const string PrimitiveFile = "src/MeshWeaver.Utils/NoReplaceMove.cs";

    private static readonly Regex Move = new(@"\bFile\s*\.\s*Move\s*\(", RegexOptions.Compiled);

    private static readonly Regex ReplacesDeliberately = new(
        @"overwrite\s*:\s*true|,\s*true\s*\)\s*$", RegexOptions.Compiled);

    private sealed record Site(string File, int Line, string Text);

    /// <summary>Every no-replace <c>File.Move</c> in the scanned trees — which must be none but the
    /// primitive's own.</summary>
    [Fact]
    public void NoProductionCodePublishesWithFileMoveWithoutOverwrite()
    {
        var root = SourceScan.FindRepoRoot();
        var offenders = SourceScan.SourceFiles(root, ScannedRoots)
            .SelectMany(f => Sites(SourceScan.Relative(root, f), File.ReadAllText(f)))
            .Where(s => !string.Equals(s.File, PrimitiveFile, StringComparison.Ordinal))
            .OrderBy(s => s.File, StringComparer.Ordinal)
            .ThenBy(s => s.Line)
            .ToList();

        Assert.True(offenders.Count == 0,
            "🚨 File.Move(source, target, overwrite: false) is NOT an atomic publication: when its "
            + "rename fails it copies into the target, which then exists incomplete and exclusively "
            + "locked for the length of the copy — the sharing violation that drops a whole module "
            + "from an activation read, and a truncated file for every other reader (#2190). Publish "
            + "with MeshWeaver.Utils.NoReplaceMove.TryMove instead: it returns false when the name "
            + "is taken and throws when it cannot rename, and it never copies. There is no allow "
            + "file.\n"
            + string.Join("\n", offenders.Select(o => $"  {o.File}:{o.Line} — {o.Text}")));
    }

    /// <summary>
    /// The primitive is still the primitive: an exemption that outlives its subject is a hole
    /// nobody can see.
    /// </summary>
    [Fact]
    public void ThePrimitiveIsStillWhereTheExemptionSaysItIs()
    {
        var root = SourceScan.FindRepoRoot();
        var file = Path.Combine(root, PrimitiveFile);

        Assert.True(File.Exists(file),
            $"{PrimitiveFile} is exempted by name and no longer exists — move the exemption with it.");
        Assert.Contains("NoReplaceMove", File.ReadAllText(file), StringComparison.Ordinal);
    }

    /// <summary>
    /// Non-vacuity: the classifier tells the forbidden call from the permitted ones, and the scan it
    /// runs under is the REAL one — a planted tree, read through <see cref="SourceScan"/>, so a guard
    /// that lost its roots fails rather than passing over nothing.
    /// </summary>
    [Fact]
    public void TheScannerTellsAPublicationFromAReplacement()
    {
        var dir = Directory.CreateTempSubdirectory("noreplace-guard-selftest");
        try
        {
            var src = Directory.CreateDirectory(Path.Combine(dir.FullName, "src")).FullName;
            File.WriteAllText(Path.Combine(src, "Publishes.cs"),
                "class C { void M() { File.Move(temp, path, overwrite: false); } }");
            File.WriteAllText(Path.Combine(src, "PublishesPositionally.cs"),
                "class C2 { void M() { File.Move(temp, path); } }");
            File.WriteAllText(Path.Combine(src, "Replaces.cs"),
                "class D { void M() { File.Move(temp, path, overwrite: true); } }");
            File.WriteAllText(Path.Combine(src, "OnlyProse.cs"),
                "// File.Move(temp, path, overwrite: false) is the defect; use NoReplaceMove.\nclass E { }");
            File.WriteAllText(Path.Combine(src, "Script.csx"),
                "File.Move(staged, final, overwrite: false);");
            Directory.CreateDirectory(Path.Combine(src, "obj"));
            File.WriteAllText(Path.Combine(src, "obj", "Generated.cs"),
                "class F { void M() { File.Move(temp, path); } }");

            var found = SourceScan.SourceFiles(dir.FullName, ["src"])
                .SelectMany(f => Sites(Path.GetFileName(f), File.ReadAllText(f)))
                .Select(s => s.File)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Contains("Publishes.cs", found);
            Assert.Contains("PublishesPositionally.cs", found);
            Assert.Contains("Script.csx", found);
            Assert.DoesNotContain("Replaces.cs", found);
            Assert.DoesNotContain("OnlyProse.cs", found);
            Assert.DoesNotContain("Generated.cs", found);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static IEnumerable<Site> Sites(string file, string text)
    {
        var code = SourceScan.MaskCommentsAndStrings(text);
        foreach (Match match in Move.Matches(code))
        {
            var open = match.Index + match.Length - 1;
            var arguments = Arguments(code, open);
            if (ReplacesDeliberately.IsMatch(arguments.Trim() + ")"))
                continue;
            var line = code.Take(match.Index).Count(c => c == '\n') + 1;
            yield return new Site(file, line, Collapse(text, match.Index, open, arguments));
        }
    }

    /// <summary>The whole argument list of the call whose open paren is at <paramref name="open"/>.</summary>
    private static string Arguments(string code, int open)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (--depth == 0)
                        return code[(open + 1)..i];
                    break;
            }
        }
        return code[(open + 1)..];
    }

    private static string Collapse(string text, int start, int open, string arguments) =>
        Regex.Replace(text[start..Math.Min(text.Length, open + arguments.Length + 2)], @"\s+", " ");
}
