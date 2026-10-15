using System;
using System.Collections.Immutable;
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
/// MeshWeaver#6832 — a release request answered by a DELIVERY HOLD records WHY on the record.
///
/// <para>Measured on a production portal: five module NodeTypes read <c>buildProvenance:
/// StaleAdopted</c> with the adopted fingerprint different from the live one,
/// <c>requestedReleaseAt == lastReleaseRequestHandledAt</c>, the assembly path and the last-compile
/// time unmoved — the request was handled and produced no build — and nothing on the record said
/// why. Two recycles (which stamp a FORCED request) re-bound the same build, equally silently.</para>
///
/// <para>The hold itself is the documented rule (#3583, <c>Doc/Architecture/NodeTypeCompilation</c>
/// → "A module's content this mesh does not TRACK never compiles here"): a module partition nothing
/// syncs is never compiled here, a force does not bypass it, and a type that still holds a build
/// keeps serving it. The defect was that the gate's reason — which names the partition and the two
/// fixes — was dropped on the way to the hold: <c>BuildDeliveryHold.Settle</c> clears
/// <see cref="NodeTypeDefinition.CompilationError"/> because the record is <c>Ok</c>, and that was
/// the only place the reason went.</para>
///
/// <para><b>Arms.</b> The recycle's shape (forced) and an install's shape (unforced, over a record
/// whose compiled snapshot disagrees with the live one — the source moved past the build) on an
/// untracked module partition: each must settle held AND say why. Control: the same forced request
/// in a partition whose <c>_GitSync</c> names a repository compiles the live source and carries no
/// hold reason — so the held arms cannot pass against a watcher that stopped compiling anything.</para>
/// </summary>
public class AHeldReleaseRequestSaysWhyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string StaleMvid = "0000bbbb0000bbbb";
    private const string SeededReason = "seeded hold reason the compile must clear";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services;
            });

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>The issue's record: a module type, adopted from a bundle whose source fingerprint
    /// differs from the live one, serving that build as <see cref="BuildProvenance.StaleAdopted"/>.</summary>
    private async Task CreateStaleAdoptedType(
        string path, bool sourceMovedPastTheBuild, string? seededHoldReason = null)
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
                LatestAssemblyCollection = "assemblies",
                LatestAssemblyPath = $"adopted/{path.Replace('/', '_')}.dll",
                LatestAssemblyMvid = StaleMvid,
                CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
                AdoptedSourceFingerprint = "373f6dc14a424bcd",
                AdoptedModuleVersion = "1.3.1",
                BuildProvenance = BuildProvenance.StaleAdopted,
                // The unforced arm: a compiled snapshot naming a source the live set no longer
                // matches, so the record is DIRTY and the request is not "satisfied by the existing
                // build" — it reaches the compile watcher, exactly as an install's request did.
                CompiledSources = sourceMovedPastTheBuild
                    ? ImmutableDictionary<string, long>.Empty.Add($"{path}/Source/Settings", 1)
                    : null,
                DeliveryHoldReason = seededHoldReason,
            },
        };
        using (Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Messaging.AccessService>().ImpersonateAsSystem())
            await MeshService.CreateNode(node).Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: TestContext.Current.CancellationToken);
        await Mesh.GetMeshNodeStream(path).Should().Within(TestTimeouts.Convergence)
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        && string.Equals(d.LatestAssemblyMvid, StaleMvid, StringComparison.Ordinal)
                        && string.Equals(d.DeliveryHoldReason, seededHoldReason, StringComparison.Ordinal),
                cancellationToken: TestContext.Current.CancellationToken);
    }

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
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        var tracked = await NodeTypeCompilationHelpers.PartitionTracksSources(Mesh, $"{partition}/Widget")
            .Should().Within(TestTimeouts.Convergence).Emit("the seam answers once, promptly",
                cancellationToken: TestContext.Current.CancellationToken);
        tracked.Should().BeTrue("a _GitSync naming a repository is what 'tracked' means");
    }

    /// <summary>Stamps a release request and returns its trigger time.</summary>
    private async Task<DateTimeOffset> RequestRelease(string path, bool force)
    {
        var at = DateTimeOffset.UtcNow;
        using (Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Messaging.AccessService>().ImpersonateAsSystem())
            await Mesh.GetMeshNodeStream(path)
                .Update<NodeTypeDefinition>(d => d with
                {
                    RequestedReleaseAt = at,
                    RequestedReleaseForce = force,
                    RequestedReleaseBy = "operator",
                })
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        return at;
    }

    /// <summary>The record once THIS request was handled and whatever it dispatched settled. The
    /// wait names no field the fix adds, so the negative control fails on the assertion, not on a
    /// timeout.</summary>
    private async Task<NodeTypeDefinition> Handled(string path, DateTimeOffset requestedAt)
    {
        var node = await Mesh.GetMeshNodeStream(path).Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        && d.LastReleaseRequestHandledAt is { } handled && handled >= requestedAt
                        && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error
                            or CompilationStatus.Unavailable,
                "the request is handled and what it dispatched settles",
                cancellationToken: TestContext.Current.CancellationToken);
        return node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
    }

    private static void ShouldBeHeldAndSayWhy(NodeTypeDefinition held, string because)
    {
        held.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"a type that still holds a build never errors because of delivery (#3583); error: {held.CompilationError}");
        held.BuildProvenance.Should().Be(BuildProvenance.StaleAdopted);
        held.LatestAssemblyMvid.Should().Be(StaleMvid, "no build was produced — the last build serves");
        held.DeliveryHoldReason.Should().NotBeNullOrEmpty(
            $"{because}: a handled release request that produced no build must record WHY on the record (#6832)");
        held.DeliveryHoldReason.Should().Contain("tracks no source", "the gate that refused is named")
            .And.Contain("partition 'untracked'", "…with the partition to fix")
            .And.Contain("add a sync source", "…and the fix")
            .And.Contain("1.3.1", "…and the module version of the build that keeps serving");
    }

    [Fact]
    public async Task AReleaseRequestAnsweredByAHold_RecordsWhy_AndACompileClearsIt()
    {
        const string recycled = "untracked/Recycled";
        const string installed = "untracked/Installed";
        const string tracked = "tracked/Widget";

        await TrackPartition("tracked");
        await CreateStaleAdoptedType(recycled, sourceMovedPastTheBuild: false);
        await CreateStaleAdoptedType(installed, sourceMovedPastTheBuild: true);
        // The control starts HELD with a reason, so its final assertion proves the compile CLEARS
        // an existing reason rather than merely never writing one.
        await CreateStaleAdoptedType(tracked, sourceMovedPastTheBuild: false, seededHoldReason: SeededReason);

        // ── The recycle's shape: a FORCED request. Force skips on-demand adoption (#2818) and
        // lands on the untracked-module gate, which holds — and must now say so. ──
        var forcedAt = await RequestRelease(recycled, force: true);
        ShouldBeHeldAndSayWhy(await Handled(recycled, forcedAt), "a forced release (the recycle verb)");

        // ── An install's shape: an UNFORCED request over a dirty record. ──
        var unforcedAt = await RequestRelease(installed, force: false);
        ShouldBeHeldAndSayWhy(await Handled(installed, unforcedAt), "an unforced release (an install)");

        // ── CONTROL: the same forced request where the partition tracks a repository compiles the
        // live source, and a build of the live source carries no hold reason. ──
        (await Mesh.GetMeshNodeStream(tracked).Should().Within(TestTimeouts.Convergence)
                .Emit("the control's record right before its release request",
                    cancellationToken: TestContext.Current.CancellationToken))
            .ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!.DeliveryHoldReason
            .Should().Be(SeededReason, "the control enters its compile holding a reason");
        var trackedAt = await RequestRelease(tracked, force: true);
        var compiled = await Handled(tracked, trackedAt);
        compiled.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"a tracked partition's live source compiles; error: {compiled.CompilationError}");
        compiled.LatestAssemblyMvid.Should().NotBe(StaleMvid, "a real Roslyn pass wrote real coordinates");
        compiled.BuildProvenance.Should().Be(BuildProvenance.Compiled);
        compiled.DeliveryHoldReason.Should().BeNull("a build of the live source lifts the hold, and its reason with it");
    }
}
