using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// The untracked-module gate (MeshWeaver#3583): a MODULE's NodeType in a partition this mesh does
/// not sync from a repository never compiles here. What it settles to depends on whether it still
/// holds a build: a type WITH a usable build is HELD — <see cref="CompilationStatus.Ok"/>,
/// <see cref="BuildProvenance.StaleAdopted"/>, its coordinates untouched, its last build serving —
/// and a type with NOTHING to serve parks at <see cref="CompilationStatus.Error"/> with a reason
/// naming the partition and the fix. The same type in a partition that DOES track a repository,
/// and authored content anywhere, compile exactly as before.
///
/// <para>🚨 The hold is the 2026-09-09 measurement: this very gate settled <c>Essentials/Email</c>
/// at Error with a working assembly still on the record, and every page of the type rendered
/// "the platform refused to run it" for an afternoon. A NodeType never errors because of delivery.</para>
///
/// <para><b>A controlled experiment, in one mesh.</b> Four types differ from each other in exactly
/// one fact each: <c>untracked/Widget</c> carries adoption provenance and a usable build, and its
/// partition has no <c>_GitSync</c>; <c>untracked/Bare</c> is the same record with its build
/// coordinates cleared; <c>tracked/Widget</c> is the first record under a partition whose
/// <c>_GitSync</c> names a repository; <c>untracked/Authored</c> shares the untracked partition
/// but was never adopted. Asserting only the hold would pass just as well against a watcher that
/// had stopped compiling anything at all — the compiling arms and the Error arm are what make it
/// mean something. The tracking answer comes from the REAL GitHub provider over a REAL config
/// node, not a stand-in: the seam the gate depends on is part of what is under test.</para>
/// </summary>
public class UntrackedModuleContentNeverCompilesHereTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>The coordinates every type starts with. A compile REPLACES them (a real Roslyn
    /// pass writes a real MVID); a park leaves them untouched — so the MVID is the witness of
    /// whether Roslyn ran, independently of the status.</summary>
    private const string StaleMvid = "0000aaaa0000aaaa";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services;
            });

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private NodeTypeCompileParkRegistry Parks =>
        Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();

    private async Task CreateType(string path, bool adoptedBefore, bool withBuild = true)
    {
        var node = MeshNode.FromPath(path) with
        {
            Name = path[(path.LastIndexOf('/') + 1)..],
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = CompilationStatus.Ok,
                // A usable build: coordinates plus the LIVE framework stamp (HasUsableBuild is
                // metadata-only). `withBuild: false` models the record after a refusal cleared
                // them — nothing to serve, so the gate's Error park is the honest answer.
                LatestAssemblyCollection = withBuild ? "assemblies" : null,
                LatestAssemblyPath = withBuild ? $"adopted/{path.Replace('/', '_')}.dll" : null,
                LatestAssemblyMvid = StaleMvid,
                CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
                // Adoption provenance from an EARLIER identity: the bake for this one has not
                // landed, which is the roll-before-bake shape. AdoptedSourceFingerprint is the
                // field that survives a local compile, so it is the durable "this is a module's"
                // mark the gate reads.
                AdoptedSourceFingerprint = adoptedBefore ? "bundlefingerprint0000000" : null,
                BuildProvenance = adoptedBefore ? BuildProvenance.AdoptedVerified : BuildProvenance.Compiled,
            },
        };
        await MeshService.CreateNode(node).Should().Within(TestTimeouts.Convergence).Emit();
        await Mesh.GetMeshNodeStream(path).Should().Within(TestTimeouts.Convergence)
            .Match(n => n?.Content is NodeTypeDefinition d
                        && string.Equals(d.LatestAssemblyMvid, StaleMvid, StringComparison.Ordinal));
    }

    /// <summary>A real <c>{partition}/_GitSync</c> naming a repository — what the settings tab
    /// writes when a Space is connected. The seam is then read back directly, so a false negative
    /// in the arm below cannot be mistaken for a defect in the gate.</summary>
    private async Task TrackPartition(string partition)
    {
        await MeshService.CreateNode(new MeshNode(GitHubSyncService.ConfigId, partition)
            {
                Name = "GitHub Sync",
                NodeType = GitHubSyncService.ConfigNodeType,
                State = MeshNodeState.Active,
                Content = new GitHubSyncConfig
                {
                    RepositoryUrl = "https://github.com/Systemorph/Example",
                    Branch = "main",
                },
            })
            .Should().Within(TestTimeouts.Convergence).Emit();
        var tracked = await NodeTypeCompilationHelpers.PartitionTracksSources(Mesh, $"{partition}/Widget")
            .Should().Within(TestTimeouts.Convergence).Emit("the seam answers once, promptly");
        tracked.Should().BeTrue("a _GitSync naming a repository is what 'tracked' means");
    }

    /// <summary>The operator's door — the UI Compile button / the compile tool: a FORCED release
    /// request. Force means "build the live source, not whatever a bundle resolves", so it skips
    /// the on-demand adoption pass and lands straight on the gate; on an untracked module partition
    /// the live source is the problem, which is why the gate does not exempt it.</summary>
    private Task RequestForcedRelease(string path) =>
        Mesh.GetMeshNodeStream(path)
            .Update<NodeTypeDefinition>(d => d with
            {
                RequestedReleaseAt = DateTimeOffset.UtcNow,
                RequestedReleaseForce = true,
                RequestedReleaseBy = "operator",
            })
            .Should().Within(TestTimeouts.Convergence).Emit();

    /// <summary>The record after the request settled: an <c>Error</c>, an <c>Ok</c> whose MVID
    /// is no longer the seeded one (the seeded Ok is what the stream starts with, so it is not a
    /// settle), or an <c>Ok</c> HELD as <see cref="BuildProvenance.StaleAdopted"/> (the seeded
    /// record is never that either).</summary>
    private async Task<NodeTypeDefinition> Settled(string path)
    {
        var node = await Mesh.GetMeshNodeStream(path).Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n?.Content is NodeTypeDefinition d
                        && (d.CompilationStatus == CompilationStatus.Error
                            || (d.CompilationStatus == CompilationStatus.Ok
                                && (d.BuildProvenance == BuildProvenance.StaleAdopted
                                    || !string.Equals(d.LatestAssemblyMvid, StaleMvid, StringComparison.Ordinal)))),
                "every route into a compile settles: a Roslyn pass, a hold or a named park");
        return (NodeTypeDefinition)node.Content!;
    }

    [Fact]
    public async Task AModuleTypeInAnUntrackedPartition_IsHeldOrParks_WhileTrackedAndAuthoredTypesCompile()
    {
        const string untrackedModule = "untracked/Widget";
        const string untrackedBare = "untracked/Bare";
        const string trackedModule = "tracked/Widget";
        const string authored = "untracked/Authored";

        await TrackPartition("tracked");
        await CreateType(untrackedModule, adoptedBefore: true);
        await CreateType(untrackedBare, adoptedBefore: true, withBuild: false);
        await CreateType(trackedModule, adoptedBefore: true);
        await CreateType(authored, adoptedBefore: false);

        // ── THE ARM UNDER TEST: module content, partition tracks nothing, a usable build on the
        // record → HELD: the last build keeps serving, marked, never Error (#3583, 2026-09-09). ──
        await RequestForcedRelease(untrackedModule);
        var held = await Settled(untrackedModule);
        held.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"a type that still holds a build never ERRORS because of delivery (#3583); error: {held.CompilationError}");
        held.BuildProvenance.Should().Be(BuildProvenance.StaleAdopted,
            "…and says honestly that the build it serves is behind the source");
        held.LatestAssemblyMvid.Should().Be(StaleMvid,
            "no Roslyn pass ran and nothing touched the coordinates — the LAST build is what serves");
        held.LatestAssemblyPath.Should().Be("adopted/untracked_Widget.dll");
        Parks.IsParked(untrackedModule).Should().BeFalse(
            "a hold is not a park: no Roslyn ran, there is no storm to contain, and the next Pending "
            + "flip is exactly the re-check that lifts the hold once a bundle lands");
        Parks.GetCompileAttemptCount(untrackedModule).Should().Be(0,
            "the park registry's attempt counter is the observable proof no compile was dispatched");

        // ── THE ERROR ARM: the same record with NOTHING to serve → parked, named. ──
        await RequestForcedRelease(untrackedBare);
        var parked = await Settled(untrackedBare);
        parked.CompilationStatus.Should().Be(CompilationStatus.Error,
            "with no build on the record there is nothing to serve — the named park is the truth");
        parked.CompilationError.Should().Contain("tracks no source")
            .And.Contain("partition 'untracked'", "the reason names the partition to fix")
            .And.Contain("add a sync source", "…and the fix")
            .And.Contain("MeshWeaver#3583");
        parked.LatestAssemblyMvid.Should().Be(StaleMvid,
            "no Roslyn pass ran — a park leaves the record exactly as it was");
        Parks.IsParked(untrackedBare).Should().BeTrue("the refusal is a deterministic park");
        Parks.GetCompileAttemptCount(untrackedBare).Should().Be(0);

        // ── CONTROL 1: the SAME record, in a partition whose _GitSync names a repository. ──
        await RequestForcedRelease(trackedModule);
        var compiled = await Settled(trackedModule);
        compiled.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"a tracked partition's live source is the repository's, so compiling it is honest; "
            + $"error: {compiled.CompilationError}");
        compiled.LatestAssemblyMvid.Should().NotBe(StaleMvid, "a real Roslyn pass wrote real coordinates");
        Parks.IsParked(trackedModule).Should().BeFalse();

        // ── CONTROL 2: AUTHORED content in the untracked partition — never adopted, never offered. ──
        await RequestForcedRelease(authored);
        var authoredBuild = await Settled(authored);
        authoredBuild.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"content a user authored is not a module's, whatever partition it lives in; "
            + $"error: {authoredBuild.CompilationError}");
        authoredBuild.LatestAssemblyMvid.Should().NotBe(StaleMvid);
        Parks.IsParked(authored).Should().BeFalse();
    }
}

/// <summary>The two pure halves of the gate, pinned without a mesh.</summary>
public class UntrackedModuleContentDecisionTest
{
    private static NodeTypeDefinition Authored() => new() { Configuration = "config => config" };

    [Fact]
    public void AuthoredContent_NeverOfferedNeverAdopted_IsNotAModules()
        => NodeTypeCompilationHelpers.IsModuleContent(Authored(), offeredByBundle: false)
            .Should().BeFalse("a user's own NodeType compiles wherever it lives");

    [Fact]
    public void ATypeABundleEntryNamed_IsAModules_WhateverTheRecordSays()
        => NodeTypeCompilationHelpers.IsModuleContent(Authored(), offeredByBundle: true)
            .Should().BeTrue("a bundle that names the type — adopted or DECLINED — is the module's own statement");

    [Fact]
    public void ATypeAdoptedUnderAnEarlierIdentity_IsAModules_EvenAfterALocalCompile()
        => NodeTypeCompilationHelpers.IsModuleContent(
                Authored() with { AdoptedSourceFingerprint = "abc", BuildProvenance = BuildProvenance.Compiled },
                offeredByBundle: false)
            .Should().BeTrue("AdoptedSourceFingerprint survives ApplyCompileSuccess; BuildProvenance does not");

    [Theory]
    [InlineData(BuildProvenance.AdoptedVerified)]
    [InlineData(BuildProvenance.AdoptedUnverified)]
    [InlineData(BuildProvenance.AdoptionRefused)]
    [InlineData(BuildProvenance.StaleAdopted)]
    public void AnyAdoptionProvenance_IsAModules(BuildProvenance provenance)
        => NodeTypeCompilationHelpers.IsModuleContent(Authored() with { BuildProvenance = provenance }, false)
            .Should().BeTrue();

    [Theory]
    [InlineData("Feedback/Feedback/Source/FeedbackContent", "Feedback")]
    [InlineData("Crm/Migration", "Crm")]
    [InlineData("Solo", "Solo")]
    public void ThePartition_IsTheTopLevelSegment(string path, string partition)
        => NodeTypeCompilationHelpers.PartitionOf(path).Should().Be(partition);

    [Fact]
    public void TheReason_NamesThePartition_TheFinding_AndBothFixes()
    {
        var declined = PrebuiltAssemblySeeder.UntrackedPartitionParkReason("Feedback/Feedback/Source/FeedbackContent", offered: true);
        declined.Should().Contain("partition 'Feedback'").And.Contain("tracks no source")
            .And.Contain("DECLINED", "an offered-and-refused bundle says the FILES are stale, not the bundle")
            .And.Contain("add a sync source").And.Contain("rebake").And.Contain("MeshWeaver#3583");

        var absent = PrebuiltAssemblySeeder.UntrackedPartitionParkReason("Feedback/Feedback/Source/FeedbackContent", offered: false);
        absent.Should().Contain("no prebuilt bundle", "no bundle for this identity named the type — the bake has not landed")
            .And.NotContain("DECLINED");
    }
}
