using System;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 <b>The link is WRITTEN and RESOLVES (#1751).</b> A compile that produces a
/// <c>Release</c> node must record, on that node, where its assemblies live — for which framework
/// identity and on which architecture — and a consumer standing in the producing lane must resolve
/// it through <see cref="ReleaseArtifactResolver"/>.
///
/// <para><b>Why an integration test and not just the unit pins.</b>
/// <see cref="ReleaseArtifactResolverTest"/> proves the RULE; nothing there proves anyone WRITES a
/// record the rule can act on. That half fails silently in the worst possible way: a release with no
/// artifact resolves to nothing, the consumer compiles, and compiling is indistinguishable from
/// normal behaviour — which is exactly how an arm64 install adopting nothing from an amd64-published
/// lane went unnoticed (#1728). So the assertion is made against a release the compile watcher
/// actually minted, with the live identity read the same way the adoption gate reads it.</para>
/// </summary>
public class ReleaseArtifactLinkTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "TestReleaseLink";
    private const string NodeTypeId = "Linked";
    private static readonly string NodeTypePath = $"{Partition}/{NodeTypeId}";

    // One cold Roslyn compile end-to-end (the first-build kickoff), which on a 2-core CI Linux
    // runner is the 60–90 s range the sibling NodeTypeReleaseTest documents. The base class's
    // dispose watchdog would otherwise kill the test at 60 s — before its own declared budget.
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(120);

    [Fact(Timeout = 120_000)]
    public async Task CompiledRelease_CarriesAnArtifactResolvableInTheProducingLane()
    {
        var workspace = Mesh.GetWorkspace();

        await NodeFactory.CreateNode(new MeshNode(NodeTypeId, Partition)
        {
            Name = "Linked Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Sample for the Release artifact-link test.",
                Configuration = "config => config.AddDefaultLayoutAreas()"
            }
        }).Should().Emit();

        // Read through the live node stream, never a query: the release path is known and a query
        // is eventually consistent, so it would race the post-compile tick.
        var settled = await workspace
            .GetMeshNodeStream(NodeTypePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestReleasePath));
        var releasePath = ((NodeTypeDefinition)settled.Content!).LatestReleasePath!;

        var releaseNode = await workspace.GetMeshNodeStream(releasePath)
            .Should().Within(15.Seconds())
            .Match(n => n is not null && n.Content is NodeTypeRelease);

        var release = (NodeTypeRelease)releaseNode.Content!;

        release.Artifacts.Should().NotBeNull(
            "a succeeded release must record WHERE its assemblies live — that link is the whole "
            + "point of #1751, and a release without it silently resolves to nothing");
        release.Artifacts.Should().NotBeEmpty();

        var artifact = release.Artifacts!.Single();

        // The identity is read exactly as the adoption gate reads it. A hard-coded constant would
        // pass while producer and gate disagreed — which is precisely what #1696 was.
        artifact.FrameworkIdentity.Should().Be(PrebuiltAssemblySeeder.LiveFrameworkMvid);
        artifact.Architecture.Should().Be(ReleaseArchitecture.Live);
        artifact.AssemblyStoreVersion.Should().Be(
            release.AssemblyStoreVersion,
            "the artifact's store key must name the SAME version the upload used, or a resolver "
            + "hands back a key with no bytes behind it");

        // …and the resolver, standing in this process's lane, finds it.
        var match = ReleaseArtifactResolver.Resolve(
            [release], PrebuiltAssemblySeeder.LiveFrameworkMvid, ReleaseArchitecture.Live);

        match.IsResolved.Should().BeTrue(
            "the record the producer wrote must be resolvable by the rule the consumer applies — "
            + "a producer and a resolver that disagree is an adoption lane that never adopts");
        match.Artifact.Should().BeSameAs(artifact);
        match.Release!.Path.Should().Be(release.Path);

        // A consumer in ANOTHER lane resolves nothing, and is TOLD which lane this release is for.
        // That sentence is the difference between "adoption regressed" and "no bake exists for my
        // architecture" — the two look identical from an adopted-count alone.
        var otherLane = ReleaseArtifactResolver.Resolve(
            [release], "s0000000000000000", ReleaseArchitecture.Live);

        otherLane.IsResolved.Should().BeFalse();
        otherLane.DeclineReason.Should().Contain(PrebuiltAssemblySeeder.LiveFrameworkMvid);
        otherLane.DeclineReason.Should().Contain(ReleaseArchitecture.Live);
    }
}
