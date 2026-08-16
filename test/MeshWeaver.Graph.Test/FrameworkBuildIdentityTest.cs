#pragma warning disable CS1591

using MeshWeaver.Graph.Configuration;
using MeshWeaver.Plugin.Build;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the ONE framework build identity (#1660 WS3): the pure resolution rule, and — the pin the
/// whole CI bake stands on — that every reader (the in-process
/// <c>NodeTypeCompilationHelpers.FrameworkVersion</c>, the loaded-assembly reading, and
/// <see cref="FrameworkIdentity.ReadIdentity"/>'s metadata-only PE reading a producer uses on a
/// restored package) resolves the SAME value for the same Graph assembly. These tests run in both
/// build flavors on purpose: locally the assembly carries no stamp (identity = MVID), on CI it
/// carries the commit stamp (identity = g&lt;sha&gt;) — the consistency assertions must hold in
/// BOTH, which is exactly the property that makes the bake adoptable.
/// </summary>
public class FrameworkBuildIdentityTest
{
    [Fact]
    public void Resolve_PrefersTheStampedIdentity()
    {
        FrameworkBuildIdentity.Resolve("g" + new string('a', 40), "22825f59aaaaaaaaaaaaaaaaaaaaaaaa")
            .Should().Be("g" + new string('a', 40),
                "a CI build's commit identity covers the whole tree — it wins over the MVID");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_FallsBackToTheContentIdentity(string? stamped)
    {
        FrameworkBuildIdentity.Resolve(stamped, "22825f59aaaaaaaaaaaaaaaaaaaaaaaa")
            .Should().Be("22825f59aaaaaaaaaaaaaaaaaaaaaaaa",
                "a local build carries no stamp and keeps the content-exact MVID scheme");
    }

    [Fact]
    public void FrameworkVersion_IsTheResolvedIdentityOfTheGraphAssembly()
    {
        var graph = typeof(NodeTypeCompilationHelpers).Assembly;
        var expected = FrameworkBuildIdentity.Resolve(
            FrameworkBuildIdentity.StampedIdentityOf(graph),
            graph.ManifestModule.ModuleVersionId.ToString("N"));

        NodeTypeCompilationHelpers.FrameworkVersion.Should().Be(expected,
            "there is exactly one identity resolution; every consumer flows from it");
        PrebuiltAssemblySeeder.LiveFrameworkMvid.Should().Be(expected,
            "the producer-facing public reading must never diverge from the gate");
    }

    [Fact]
    public void PeRead_AgreesWithTheLoadedAssembly()
    {
        // The producer side (mw-plugin-test's bake, the plugin packer) reads the identity off the
        // assembly FILE without loading it; the consumer gate reads it off the LOADED assembly.
        // If these ever disagree, every bake declines wholesale — silently. This assertion holds
        // in both build flavors: unstamped (both resolve the MVID) and CI-stamped (both resolve
        // the commit identity).
        var graph = typeof(NodeTypeCompilationHelpers).Assembly;
        graph.Location.Should().NotBeNullOrEmpty();

        FrameworkIdentity.ReadIdentity(graph.Location)
            .Should().Be(PrebuiltAssemblySeeder.LiveFrameworkMvid);
    }

    [Fact]
    public void StoreTag_IsAlwaysAttributableByTheRetentionSweep()
    {
        // FileSystemAssemblyStore's filename tag is FrameworkVersion[..8]; the retention sweep
        // only ever deletes files whose tag it can attribute (AssemblyCacheGenerations.TagOf).
        // Whatever flavor built this test run, the live tag must round-trip — otherwise every
        // generation this build writes would be unreclaimable.
        var tag = NodeTypeCompilationHelpers.FrameworkVersion[..8];
        AssemblyCacheGenerations.TagOf($"v7-{tag}-9f4455cd1122.dll")
            .Should().Be(tag.ToLowerInvariant());
    }
}
