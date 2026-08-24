#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Every project that packs must carry a <c>README.md</c> beside its csproj.
///
/// <para>Directory.Build.props does <c>&lt;None Include="README.md" Pack="true"&gt;</c> for ALL
/// projects, unconditionally, and declares <c>PackageReadmeFile</c>. So a packable project without
/// one does not merely ship a bare listing — <c>dotnet pack</c> FAILS it with
/// <c>NU5019: File not found</c>.</para>
///
/// <para>🚨 The failure lands at the worst possible moment. <c>dotnet build</c> does not pack, so
/// CI is green, review is green, and the gap first appears when a <c>v*.*.*</c> tag runs the
/// release — after the tag is public and after the IMAGE workflow has already succeeded, leaving a
/// release half-shipped. That is exactly how v3.0.0-rc7 went out: MeshWeaver.Speech.Contract was
/// added without a README and the whole NuGet publish died at Pack.</para>
/// </summary>
public class PackableProjectReadmeTest
{
    [Fact]
    public void EveryPackableProject_HasAReadmeBesideItsCsproj()
    {
        var root = FindRepositoryRoot();
        Assert.SkipWhen(root is null,
            "repository tree not reachable from the test bin — this convention check runs in-repo only");

        var projects = PackableProjects(root!).ToList();

        // A discovery that found nothing has verified nothing; it must not read as a pass.
        projects.Should().HaveCountGreaterThan(20,
            "the repository packs many projects — an empty or tiny discovery means the walk broke, "
            + "not that the convention holds");

        var missing = projects
            .Where(csproj => !File.Exists(Path.Combine(Path.GetDirectoryName(csproj)!, "README.md")))
            .Select(csproj => Path.GetRelativePath(root!, csproj))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            "Directory.Build.props packs README.md into every package, so a packable project "
            + "without one fails `dotnet pack` with NU5019 — and that only surfaces when a release "
            + "tag is already public. Add a README.md beside each csproj listed here.");
    }

    /// <summary>
    /// Projects the release packs: everything under <c>src/</c> that does not opt out with
    /// <c>IsPackable=false</c>. Build output and worktrees are skipped; test and sample trees are
    /// out of scope because they are not what <c>dotnet pack</c> ships from a release tag.
    /// </summary>
    private static IEnumerable<string> PackableProjects(string root)
    {
        var src = Path.Combine(root, "src");
        if (!Directory.Exists(src))
            yield break;

        foreach (var csproj in Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, csproj).Split(Path.DirectorySeparatorChar);
            if (relative.Any(part => part is "bin" or "obj" or ".worktrees"))
                continue;

            var text = File.ReadAllText(csproj);
            // Opting out of packing also opts out of the README rule — nothing is produced to
            // carry it. Matched loosely because the property may sit under any condition.
            if (text.Contains("<IsPackable>false</IsPackable>", StringComparison.OrdinalIgnoreCase))
                continue;

            yield return csproj;
        }
    }

    private static string? FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
