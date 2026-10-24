using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// <see cref="RepoPaths"/> is the ONE repository-root lookup for test code, and this pins both what
/// it resolves and that no test grows its own copy again.
///
/// <para>Test files used to carry 80-odd private walks up from the binary: most anchored on
/// <c>MeshWeaver.slnx</c>, nine on the first <c>.github</c> directory, one on a <c>.git</c>
/// directory, starting from either the base directory or the working directory. They all happened
/// to land on the same directory, but each was a separate place for the answer to drift — and a
/// <c>.git</c> anchor is the one that drifts silently, because in a git worktree <c>.git</c> is a
/// FILE, so <see cref="Directory.Exists(string)"/> walks straight past the worktree root to
/// whatever enclosing checkout holds a real <c>.git</c> directory.</para>
/// </summary>
public class RepoPathsTest
{
    /// <summary>
    /// In a worktree the root is found by its solution file even though <c>.git</c> is a file, and the
    /// walk stops at the NEAREST root — not an enclosing checkout that also has one.
    /// Negative control: the <c>.git</c>-directory anchor some copies used resolves the OUTER checkout.
    /// </summary>
    [Fact]
    public void AWorktreeRoot_IsFoundByItsSolutionFile_NotItsGitEntry()
    {
        var outer = Directory.CreateTempSubdirectory("repopaths");
        try
        {
            // An outer checkout (real .git directory) holding a worktree (.git FILE), as
            // .claude/worktrees/<name> sits inside the primary checkout.
            File.WriteAllText(Path.Combine(outer.FullName, RepoPaths.RootMarker), "<Solution />");
            Directory.CreateDirectory(Path.Combine(outer.FullName, ".git"));
            var worktree = Directory.CreateDirectory(Path.Combine(outer.FullName, ".claude", "worktrees", "x"));
            File.WriteAllText(Path.Combine(worktree.FullName, RepoPaths.RootMarker), "<Solution />");
            File.WriteAllText(Path.Combine(worktree.FullName, ".git"), "gitdir: ../../../.git/worktrees/x\n");
            var bin = Directory.CreateDirectory(Path.Combine(worktree.FullName, "test", "T", "bin", "Release", "net10.0"));

            Assert.Equal(worktree.FullName, RepoPaths.TryFindRoot(bin.FullName));

            // Negative control — the shape this replaced: anchor on a .git DIRECTORY.
            string? GitDirectoryAnchor(string start)
            {
                for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
                    if (Directory.Exists(Path.Combine(d.FullName, ".git")))
                        return d.FullName;
                return null;
            }
            Assert.Equal(outer.FullName, GitDirectoryAnchor(bin.FullName));
        }
        finally
        {
            outer.Delete(recursive: true);
        }
    }

    /// <summary>Outside any repository there is no root: <c>TryFindRoot</c> says so rather than guessing.</summary>
    [Fact]
    public void OutsideAnyRepository_ThereIsNoRoot()
    {
        var dir = Directory.CreateTempSubdirectory("repopaths-none");
        try
        {
            Assert.Null(RepoPaths.TryFindRoot(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>From this test binary the root is THIS repository: it holds the solution, src/ and test/.</summary>
    [Fact]
    public void FromTheTestBinary_TheRootIsThisRepository()
    {
        var root = RepoPaths.Root;
        Assert.True(File.Exists(Path.Combine(root, RepoPaths.RootMarker)));
        Assert.True(Directory.Exists(Path.Combine(root, "src")), $"{root} has no src/");
        Assert.True(File.Exists(Path.Combine(root, "test", "MeshWeaver.Fixture", "RepoPaths.cs")),
            $"{root} is not the tree that holds RepoPaths.cs");
    }

    /// <summary>
    /// A parent-directory walk (<c>x = x.Parent</c>) whose loop names a root marker — the solution
    /// file, <c>.github</c> or <c>.git</c> — within a few lines above it.
    /// </summary>
    private static readonly Regex ParentStep = new(@"\b(\w+)\s*=\s*\1\s*\.\s*Parent\b", RegexOptions.Compiled);

    private static readonly Regex RootMarkerLiteral =
        new("\"(?:MeshWeaver\\.slnx|\\.github|\\.git)\"", RegexOptions.Compiled);

    private static string[] HandRolledRootWalks(string root) =>
        SourceScan.SourceFiles(root, ["test"])
            .Where(f => !f.EndsWith(Path.Combine("MeshWeaver.Fixture", "RepoPaths.cs"), StringComparison.Ordinal))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                var code = SourceScan.MaskCommentsAndStrings(text);
                return ParentStep.Matches(code).Any(m =>
                {
                    var from = Math.Max(0, m.Index - 400);
                    return RootMarkerLiteral.IsMatch(text.AsSpan(from, m.Index - from).ToString());
                });
            })
            .Select(f => SourceScan.Relative(root, f))
            .ToArray();

    /// <summary>
    /// No test file walks up to the repository root by hand any more — it calls
    /// <see cref="RepoPaths.Root"/> (or <see cref="RepoPaths.TryFindRoot()"/> to skip outside a checkout).
    /// </summary>
    [Fact]
    public void NoTestCarriesItsOwnRepoRootWalk()
    {
        var offenders = HandRolledRootWalks(RepoPaths.Root);
        Assert.True(offenders.Length == 0,
            "These test files walk up to the repository root by hand; use RepoPaths.Root "
            + "(or RepoPaths.TryFindRoot() for a test that skips outside a checkout):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>The detector sees the shape it forbids — planted in a temp tree, it is named.</summary>
    [Fact]
    public void TheWalkDetector_SeesAPlantedCopy()
    {
        var dir = Directory.CreateTempSubdirectory("repopaths-guard");
        try
        {
            var test = Directory.CreateDirectory(Path.Combine(dir.FullName, "test", "X"));
            File.WriteAllText(Path.Combine(test.FullName, "Copy.cs"),
                "class C { static string R() { var dir = new System.IO.DirectoryInfo(\".\");\n"
                + "  while (dir is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, \".github\")))\n"
                + "    dir = dir.Parent;\n  return dir!.FullName; } }\n");
            File.WriteAllText(Path.Combine(test.FullName, "Clean.cs"),
                "class D { static string R() => MeshWeaver.Fixture.RepoPaths.Root; }\n");

            Assert.Equal(["test/X/Copy.cs"], HandRolledRootWalks(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
