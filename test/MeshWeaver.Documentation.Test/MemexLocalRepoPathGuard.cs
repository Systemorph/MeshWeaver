#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>Every path <c>memex-local</c> builds under the platform checkout must still be there.</b>
///
/// <para>The script composes absolute paths from <c>resolve_repo()</c> — <c>"$repo/clients/…"</c>,
/// <c>"$repo/memex/aspire/…"</c> — and hands them to <c>npm</c> and <c>docker build</c>. Nothing
/// links those strings to the tree, so when a directory MOVES the script keeps pointing at where it
/// used to be and fails at the developer's machine rather than in CI.</para>
///
/// <para>That is not hypothetical: <c>044a85618</c> ("The GUI leaves the platform: the view packs
/// and the web clients") moved every web client to MeshWeaver.Plugins and did not touch
/// <c>deploy/homebrew/</c>. The next <c>memex-local update</c> died on
/// <c>ENOENT … clients/grpc-web/package.json</c>, with two more dead paths queued behind it.</para>
///
/// <para><b>Existence on disk is NOT the check.</b> The leftover <c>clients/grpc-web/</c> still had
/// a <c>node_modules/</c> and a lockfile after the move, so the directory was there and only the
/// package was gone — a <c>Directory.Exists</c> guard would have passed while the script was
/// broken. The assertions below therefore check what each COMMAND needs: an <c>npm --prefix</c>
/// target needs a <c>package.json</c>; a <c>docker build -f</c> target needs that file.</para>
/// </summary>
public class MemexLocalRepoPathGuard
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Script(string root) =>
        File.ReadAllText(Path.Combine(root, "deploy", "homebrew", "bin", "memex-local"));

    private static string Under(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Every <c>npm --prefix "$repo/X"</c> in the script, as X.</summary>
    private static IEnumerable<string> NpmPrefixTargets(string script) =>
        Regex.Matches(script, @"npm\s+--prefix\s+""\$repo/(?<path>[^""]+)""")
            .Select(m => m.Groups["path"].Value)
            .Distinct();

    /// <summary>
    /// Every <c>docker build -f "$repo/X"</c> in the script, as X.
    ///
    /// <para>🚨 <c>-f</c> is also shell's "file exists" test, and the script uses it to PROBE for
    /// paths that may legitimately be absent — <c>[ -f "$repo/clients/portal-next/Dockerfile" ]</c>
    /// is how it recognises a pre-move checkout. Matching every <c>-f</c> flags that probe as a
    /// missing Dockerfile, which is a guard failing on the code written to survive the very move it
    /// is guarding against. Test expressions are excluded by the line they sit on.</para>
    /// </summary>
    private static IEnumerable<string> DockerfileTargets(string script) =>
        script.Split('\n')
            .Where(line => !line.Contains("[ -f", StringComparison.Ordinal))
            .SelectMany(line => Regex.Matches(line, @"-f\s+""\$repo/(?<path>[^""]+)""")
                .Select(m => m.Groups["path"].Value))
            .Distinct();

    /// <summary>Every <c>"$repo/X"</c> naming a file with an extension — project files and the like.</summary>
    private static IEnumerable<string> FileTargets(string script) =>
        Regex.Matches(script, @"""\$repo/(?<path>[^""]*\.[A-Za-z0-9]+)""")
            .Select(m => m.Groups["path"].Value)
            .Distinct();

    [Fact]
    public void EveryNpmPrefixUnderTheRepo_HasAPackageJson()
    {
        var root = RepoRoot();
        var missing = NpmPrefixTargets(Script(root))
            .Where(p => !File.Exists(Under(root, p + "/package.json")))
            .ToList();

        Assert.True(missing.Count == 0,
            "memex-local runs `npm --prefix` against a directory in this repo that has no "
            + "package.json — npm fails with ENOENT and the local install stops: "
            + string.Join(", ", missing)
            + ". If the package MOVED, point the script at the checkout that now holds it; the "
            + "directory being present is not enough, node_modules survives a move.");
    }

    [Fact]
    public void EveryDockerfileUnderTheRepo_Exists()
    {
        var root = RepoRoot();
        var missing = DockerfileTargets(Script(root))
            .Where(p => !File.Exists(Under(root, p)))
            .ToList();

        Assert.True(missing.Count == 0,
            "memex-local passes `docker build -f` a Dockerfile that is not in this repo: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void EveryFileUnderTheRepo_Exists()
    {
        var root = RepoRoot();
        var missing = FileTargets(Script(root))
            .Where(p => !File.Exists(Under(root, p)))
            .ToList();

        Assert.True(missing.Count == 0,
            "memex-local names a file under the platform checkout that is not there: "
            + string.Join(", ", missing));
    }
}
