using System;
using System.Collections.Generic;
using Memex.Portal.Shared.Api;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins which assembly the bundle route serves for a caller's lane (#1751) — the whole distribution
/// decision, and both of its branches fail SILENTLY when they are wrong.
///
/// <list type="bullet">
/// <item><description>Serve a <b>lagging</b> build on the instance's own lane and everything looks
/// healthy: the consumer adopts, the counts say "adopted", and it is quietly running an older
/// assembly than the portal that served it. A <c>Release</c> record genuinely can lag —
/// <c>PrebuiltAssemblySeeder</c> stamps <c>LastCompiledVersion</c> on adopt without minting a release
/// at all — so this is the ordinary case, not a corner one.</description></item>
/// <item><description>Serve a <b>foreign lane's</b> build and nothing complains until activation,
/// where it surfaces as a <c>TypeLoadException</c> inside a collectible ALC: no compile error, no
/// overlay, nothing to grep.</description></item>
/// </list>
///
/// <para>Neither is observable from a passing request, which is why the choice is pinned directly
/// rather than through an HTTP round trip against a live portal.</para>
/// </summary>
public class PluginBundleLaneResolutionTest
{
    private const string OwnIdentity = "s1a2b3c4d5e6f708";
    private const string ForeignIdentity = "s9f8e7d6c5b4a302";
    private const string NodePath = "Pkg/Type";

    private static NodeTypeDefinition Compiled(long version) =>
        new() { Description = "test", LastCompiledVersion = version };

    private static IReadOnlyDictionary<string, IReadOnlyList<NodeTypeRelease>> Releases(
        params ReleaseArtifact[] artifacts) =>
        new Dictionary<string, IReadOnlyList<NodeTypeRelease>>(StringComparer.OrdinalIgnoreCase)
        {
            [NodePath] =
            [
                new NodeTypeRelease
                {
                    Path = $"{NodePath}/Release/v1",
                    NodeTypePath = NodePath,
                    Release = "hash",
                    FrameworkVersion = "3.0.0.0",
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    Artifacts = artifacts,
                },
            ],
        };

    [Fact]
    public void OwnLaneServesTheCurrentBuild_NotTheLaggingRelease()
    {
        // The release records store version 7; the portal actually RUNS 9. On its own lane the
        // identity claim is true by construction, so the current build is both correct and newer —
        // resolving through the release here would ship 7 and look perfectly healthy doing it.
        var misses = new List<string>();

        var version = PluginBundleEndpoints.ResolveStoreVersion(
            NodePath, Compiled(9),
            Releases(new ReleaseArtifact(OwnIdentity, "linux-x64", AssemblyStoreVersion: 7)),
            OwnIdentity, "linux-x64", servesOwnLane: true, misses);

        Assert.Equal(9, version);
        Assert.Empty(misses);
    }

    [Fact]
    public void OwnLaneServesEvenWhenNoReleaseRecordsALink()
    {
        // The pre-#1751 shape, which must stay byte-for-byte what it was: every type adopted from a
        // CI bake has a LastCompiledVersion and no release at all. Counting these as misses would
        // empty every bundle the registry serves today.
        var misses = new List<string>();

        var version = PluginBundleEndpoints.ResolveStoreVersion(
            NodePath, Compiled(9),
            new Dictionary<string, IReadOnlyList<NodeTypeRelease>>(),
            OwnIdentity, "linux-x64", servesOwnLane: true, misses);

        Assert.Equal(9, version);
        Assert.Empty(misses);
    }

    [Fact]
    public void ForeignLaneServesTheArtifactRecordedForIt()
    {
        // The capability the link adds: an arm64 caller can now be served, from the record the arm64
        // bake wrote — rather than told "not adoptable" because the registry's own lane is x64.
        var misses = new List<string>();

        var version = PluginBundleEndpoints.ResolveStoreVersion(
            NodePath, Compiled(9),
            Releases(
                new ReleaseArtifact(OwnIdentity, "linux-x64", AssemblyStoreVersion: 9),
                new ReleaseArtifact(ForeignIdentity, "linux-arm64", AssemblyStoreVersion: 4)),
            ForeignIdentity, "linux-arm64", servesOwnLane: false, misses);

        Assert.Equal(4, version);
        Assert.Empty(misses);
    }

    [Fact]
    public void ForeignLaneNeverFallsBackToTheCurrentBuild()
    {
        // 🚨 The one that must not be "helpful". LastCompiledVersion is THIS lane's build; handing it
        // to a caller under the identity it asked for would claim a compatibility nobody established,
        // and the consumer's own gate would then wave it through because the manifest says what it
        // asked for. Nothing served, and the miss is recorded.
        var misses = new List<string>();

        var version = PluginBundleEndpoints.ResolveStoreVersion(
            NodePath, Compiled(9),
            Releases(new ReleaseArtifact(OwnIdentity, "linux-x64", AssemblyStoreVersion: 9)),
            ForeignIdentity, "linux-arm64", servesOwnLane: false, misses);

        Assert.Null(version);
        var miss = Assert.Single(misses);
        Assert.Contains(NodePath, miss);
        Assert.Contains(ForeignIdentity, miss);
        Assert.Contains("linux-arm64", miss);
    }

    [Fact]
    public void ForeignLaneWithAnUnlocatableArtifactIsAMiss_NotASilentSkip()
    {
        // An artifact that proves the lane but records no store key cannot be fetched. Serving
        // nothing is right; saying nothing is not — the reason has to distinguish "no bake for your
        // lane" from "a bake exists but its bytes cannot be located", because they are different
        // things to go and fix.
        var misses = new List<string>();

        var version = PluginBundleEndpoints.ResolveStoreVersion(
            NodePath, Compiled(9),
            Releases(new ReleaseArtifact(ForeignIdentity, "linux-arm64")),
            ForeignIdentity, "linux-arm64", servesOwnLane: false, misses);

        Assert.Null(version);
        Assert.Contains("cannot be located", Assert.Single(misses));
    }
}
