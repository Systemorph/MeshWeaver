#pragma warning disable CS1591

using MeshWeaver.Plugin.Build;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The framework FLOOR (<see cref="FrameworkVersionResolver.Resolve"/>) is the newest version at
/// which EVERY package in <see cref="CompilationEnvironment.PackageIds"/> is published — not the
/// newest <c>MeshWeaver.Graph</c>.
///
/// <para>Resolving it from one package is a latent outage. Every emitted project references all of
/// them, so a version where one has no build is not a floor any plugin can claim: NuGet resolves
/// the missing package to whatever else it can find and the restore dies with an NU1605 downgrade
/// before compiling a line. When <c>MeshWeaver.AI</c>'s source moved out of the platform repo the
/// platform stopped publishing it — the framework released <c>3.0.0-rc8</c>, <c>MeshWeaver.AI</c>
/// stopped at <c>3.0.0-rc7</c>, and 33 of 33 code-bearing packages failed to pack.</para>
///
/// <para>Local directory sources are enough to pin this: the resolver globs
/// <c>{packageId}.*.nupkg</c>, so a directory of empty files IS a feed, and the test needs no
/// network.</para>
/// </summary>
public class FrameworkFloorTest : IDisposable
{
    private readonly string feed =
        Directory.CreateTempSubdirectory(nameof(FrameworkFloorTest)).FullName;

    private void Publish(string packageId, params string[] versions)
    {
        foreach (var version in versions)
            File.WriteAllText(Path.Combine(feed, $"{packageId}.{version}.nupkg"), "");
    }

    /// <summary>Publishes every package at every version, so the feed is complete by default.</summary>
    private void PublishAll(params string[] versions)
    {
        foreach (var package in CompilationEnvironment.PackageIds)
            Publish(package, versions);
    }

    private string Resolve() =>
        FrameworkVersionResolver.Resolve(FrameworkVersionResolver.Latest, [feed], new HttpClient());

    [Fact]
    public void ResolvesTheNewestVersionPublishedForEveryPackage()
    {
        PublishAll("3.0.0-rc6", "3.0.0-rc7");
        Assert.Equal("3.0.0-rc7", Resolve());
    }

    /// <summary>The regression this exists for: one package short at the newest version drops the
    /// floor to the newest COMPLETE one, rather than naming a version that cannot restore.</summary>
    [Fact]
    public void DropsToTheNewestCompleteVersionWhenOnePackageIsNotPublishedThere()
    {
        PublishAll("3.0.0-rc6", "3.0.0-rc7");
        foreach (var package in CompilationEnvironment.PackageIds)
        {
            // MeshWeaver.AI is the one that actually stopped; assert on whichever it is, so the
            // test keeps meaning if the list changes.
            if (package == "MeshWeaver.AI")
                continue;
            Publish(package, "3.0.0-rc8");
        }

        Assert.Equal("3.0.0-rc7", Resolve());
    }

    /// <summary>A package no source can answer for must not empty the intersection — otherwise one
    /// unreachable feed resolves nothing at all.</summary>
    [Fact]
    public void APackageAbsentFromEverySourceDoesNotConstrainTheFloor()
    {
        foreach (var package in CompilationEnvironment.PackageIds)
        {
            if (package == "MeshWeaver.AI")
                continue;
            Publish(package, "3.0.0-rc7", "3.0.0-rc8");
        }

        Assert.Equal("3.0.0-rc8", Resolve());
    }

    /// <summary>An explicit version is the caller's choice and is returned untouched — the floor
    /// logic applies to <c>latest</c>, which is what CI passes.</summary>
    [Fact]
    public void AnExplicitVersionIsReturnedUnchanged()
    {
        PublishAll("3.0.0-rc7");
        Assert.Equal(
            "3.0.0-rc8",
            FrameworkVersionResolver.Resolve("3.0.0-rc8", [feed], new HttpClient()));
    }

    /// <summary>Resolving nothing must throw rather than fall back to a hard-coded version.</summary>
    [Fact]
    public void NoCommonVersionThrows()
    {
        Publish("MeshWeaver.Graph", "3.0.0-rc8");
        Publish("MeshWeaver.AI", "3.0.0-rc7");

        var ex = Assert.Throws<InvalidOperationException>(Resolve);
        Assert.Contains("MeshWeaver.AI", ex.Message, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(feed, recursive: true);
}
