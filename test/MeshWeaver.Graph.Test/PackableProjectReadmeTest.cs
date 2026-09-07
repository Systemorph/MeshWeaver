#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// What this repository still publishes to nuget.org, and the README every package needs.
///
/// <para>Two assertions, one subject. <b>The set</b>: exactly one project here packs —
/// <c>memex/aspire/Memex.Aspire.Hosting</c> (<c>MeshWeaver.Aspire.Hosting.Memex</c>). Everything
/// else under the MeshWeaver prefix was retired on 2026-09-07 and the packages unlisted
/// (<c>Doc/Architecture/NuGetPackageRetirement</c>); the second survivor,
/// <c>MeshWeaver.MemexTemplate</c>, lives in MeshWeaver.Plugins and is out of this tree's reach.
/// <b>The README</b>: Directory.Build.props does <c>&lt;None Include="README.md" Pack="true"&gt;</c>
/// for ALL projects and declares <c>PackageReadmeFile</c>, so a packable project without one does
/// not merely ship a bare listing — <c>dotnet pack</c> FAILS it with <c>NU5019: File not found</c>.</para>
///
/// <para>🚨 Both failures land at the worst possible moment. <c>dotnet build</c> does not pack, so
/// CI is green, review is green, and the gap first appears when a <c>v*.*.*</c> tag runs
/// <c>publish-packages.yml</c> — after the tag is public. That is exactly how v3.0.0-rc7 went out:
/// MeshWeaver.Speech.Contract was added without a README and the whole NuGet publish died at Pack.</para>
///
/// <para>🚨 Why the SET is asserted and not just the READMEs. <c>IsPackable</c> now defaults to
/// <c>false</c> at the repository root with one opt-in, and the retirement tool
/// (<c>scripts/orphaned-nuget-packages.py</c>) derives "orphaned" as published MINUS packed. A
/// project that quietly regains packability therefore SPARES its retired package from the sweep and
/// silently re-enters the release's pack glob — neither of which any build reports. This test is
/// what notices. Adding a package is a deliberate act: update the expected set here, and add the
/// project to <c>.github/workflows/publish-packages.yml</c>, which asserts the same count.</para>
/// </summary>
public class PackableProjectReadmeTest
{
    /// <summary>The one project in this repository that still produces a NuGet package.</summary>
    private static readonly string[] ExpectedPackableProjects =
    [
        Path.Combine("memex", "aspire", "Memex.Aspire.Hosting", "Memex.Aspire.Hosting.csproj"),
    ];

    [Fact]
    public void ExactlyTheDeclaredProjectsPack_AndEachCarriesAReadme()
    {
        var root = FindRepositoryRoot();
        Assert.SkipWhen(root is null,
            "repository tree not reachable from the test bin — this convention check runs in-repo only");

        var (scanned, packable) = Scan(root!);

        // 🚨 Print the DENOMINATOR. "Nothing packs" and "the walk broke" read identically from an
        // empty result, and the expected set is now small enough that an empty scan would MATCH it
        // if the walk silently found no projects at all.
        scanned.Should().BeGreaterThan(50,
            "the repository holds many csproj files — a tiny scan means the walk broke, not that "
            + "the tree shrank");

        var relative = packable
            .Select(csproj => Path.GetRelativePath(root!, csproj))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        relative.Should().Equal(ExpectedPackableProjects,
            "exactly one project here still publishes to nuget.org (Doc/Architecture/"
            + "NuGetPackageRetirement). A project appearing in this list has regained packability: "
            + "it will be packed by publish-packages.yml AND it will spare its retired package from "
            + "the unlist sweep, both silently. A project MISSING from it stops being published.");

        var withoutReadme = packable
            .Where(csproj => !File.Exists(Path.Combine(Path.GetDirectoryName(csproj)!, "README.md")))
            .Select(csproj => Path.GetRelativePath(root!, csproj))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        withoutReadme.Should().BeEmpty(
            "Directory.Build.props packs README.md into every package, so a packable project "
            + "without one fails `dotnet pack` with NU5019 — and that only surfaces when a release "
            + "tag is already public. Add a README.md beside each csproj listed here.");
    }

    private static readonly Regex IsPackable =
        new(@"<IsPackable>\s*(true|false)\s*</IsPackable>", RegexOptions.IgnoreCase);

    private static readonly Regex ImportsAbove =
        new(@"<Import\b[^>]*GetPathOfFileAbove", RegexOptions.IgnoreCase);

    /// <summary>
    /// Every csproj in the tree, and the subset that effectively packs.
    /// </summary>
    /// <remarks>
    /// 🚨 <c>IsPackable</c> is INHERITED, so reading only the csproj answers the wrong question:
    /// with the root default now <c>false</c>, every project that declares nothing would read as
    /// packable. The props chain is resolved the way MSBuild resolves it — and identically to
    /// <c>scripts/orphaned-nuget-packages.py</c>, which subtracts this same set from what is
    /// published.
    /// </remarks>
    private static (int Scanned, List<string> Packable) Scan(string root)
    {
        var scanned = 0;
        var packable = new List<string>();

        foreach (var csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, csproj).Split(Path.DirectorySeparatorChar);
            if (relative.Any(part => part is "bin" or "obj" or ".worktrees"))
                continue;

            scanned++;

            var text = File.ReadAllText(csproj);
            var own = IsPackable.Match(text);
            var packs = own.Success
                ? own.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase)
                : InheritedIsPackable(Path.GetDirectoryName(csproj)!, root);

            if (packs)
                packable.Add(csproj);
        }

        return (scanned, packable);
    }

    /// <summary>
    /// The <c>IsPackable</c> a project inherits from the Directory.Build.props chain.
    /// </summary>
    /// <remarks>
    /// MSBuild auto-imports only the NEAREST Directory.Build.props; a chain continues solely because
    /// a props file explicitly imports the one above it. So an explicit declaration is the answer,
    /// and a file that neither declares nor imports ends the chain at MSBuild's own default, which
    /// is <see langword="true"/>. Erring toward true is deliberate: a false negative here would let
    /// a real package escape both assertions.
    /// </remarks>
    private static bool InheritedIsPackable(string directory, string root)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Directory.Build.props");
            if (File.Exists(candidate))
            {
                var text = File.ReadAllText(candidate);
                var declared = IsPackable.Match(text);
                if (declared.Success)
                    return declared.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (!ImportsAbove.IsMatch(text))
                    return true;   // chain stops here; MSBuild's default is packable
            }

            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                              root.TrimEnd(Path.DirectorySeparatorChar),
                              StringComparison.Ordinal))
                break;

            current = current.Parent;
        }

        return true;
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
