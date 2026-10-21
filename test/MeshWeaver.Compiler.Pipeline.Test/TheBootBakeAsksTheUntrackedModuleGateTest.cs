using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// MeshWeaver#6930 — <b>one type, one verdict, whichever route reaches it first.</b>
///
/// <para>Measured on memex.systemorph.com between 2026-10-17 18:55Z and 2026-10-18 07:56Z: the leftover
/// NodeType <c>SocialMedia/SocialTests</c> (a module's content in a partition that tracks no source)
/// was COMPILED, and failed, by the boot sweep's batch compile on 51 pod boots (<c>BatchBake:
/// SocialMedia/SocialTests → CompileError</c>), and PARKED by the untracked-module gate exactly twice
/// (<c>Compile watcher: … PARKING … (offered by a bundle: True)</c>). Both parks fired on pods whose
/// own boot had batch-compiled the type minutes earlier — same pod, same image, same bundle. The
/// bundle offer did not flap: the batch compile drives Roslyn directly and never reached the gate,
/// which lives in the compile watcher's <c>DispatchOrPark</c>.</para>
///
/// <para>The experiment: two identical module records in an untracked partition. One is reached by
/// the compile watcher (a forced release — the operator's door), the other by the boot sweep's batch
/// compile. They must end in the SAME state: parked, named, no Roslyn pass. Before the fix the batch
/// compiled its copy to <see cref="CompilationStatus.Ok"/> with a fresh MVID. The controls pin the
/// other direction — the question the batch now asks names ONLY the module type in the untracked
/// partition, never authored content beside it and never the same record under a tracked partition —
/// and the sweep must report an outcome for the type, so a sweep that reached nothing cannot pass.</para>
/// </summary>
public class TheBootBakeAsksTheUntrackedModuleGateTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>The coordinates a record starts with; a Roslyn pass replaces them, a park does not.</summary>
    private const string SeededMvid = "0000bbbb0000bbbb";

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

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static NodeTypeDefinition Definition(bool moduleContent) => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Ok,
        // NOTHING to serve: no coordinates, so the gate's honest answer is the named park and the
        // sweep sees a type that needs building — the leftover's shape.
        LatestAssemblyMvid = SeededMvid,
        // Module content by the record's own witness (it adopted a bundle under an earlier
        // identity). The production type was named by the bundle OFFER instead; the gate reads
        // both through the same IsModuleContent, and the route question is identical.
        AdoptedSourceFingerprint = moduleContent ? "bundlefingerprint0000000" : null,
        BuildProvenance = moduleContent ? BuildProvenance.AdoptedVerified : BuildProvenance.Compiled,
    };

    private async Task CreateType(string path, bool moduleContent)
    {
        var node = MeshNode.FromPath(path) with
        {
            Name = path[(path.LastIndexOf('/') + 1)..],
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = Definition(moduleContent),
        };
        using (Access.ImpersonateAsSystem())
            await MeshService.CreateNode(node).Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: TestContext.Current.CancellationToken);
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

    /// <summary>The record once something has SETTLED it after the seed: a park/compile Error, or an
    /// Ok whose MVID is no longer the seeded one (a Roslyn pass).</summary>
    private async Task<NodeTypeDefinition> Settled(string path, string because)
    {
        // Converted, never type-tested or cast: content that arrived as JSON is the same record.
        var definition = await Mesh.GetMeshNodeStream(path)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(d => d is not null
                        && (d.CompilationStatus == CompilationStatus.Error
                            || (d.CompilationStatus == CompilationStatus.Ok
                                && !string.Equals(d.LatestAssemblyMvid, SeededMvid, StringComparison.Ordinal))),
                because, cancellationToken: TestContext.Current.CancellationToken);
        return definition!;
    }

    [Fact]
    public async Task AModuleTypeInAnUntrackedPartition_GetsTheGatesVerdict_FromTheBatchAsFromTheWatcher()
    {
        var ct = TestContext.Current.CancellationToken;
        const string viaWatcher = "untracked/ViaWatcher";
        const string viaBatch = "untracked/ViaBatch";
        const string authored = "untracked/Authored";
        const string trackedModule = "tracked/Widget";

        await TrackPartition("tracked");
        await CreateType(viaWatcher, moduleContent: true);
        await CreateType(viaBatch, moduleContent: true);
        await CreateType(authored, moduleContent: false);
        await CreateType(trackedModule, moduleContent: true);

        // ── THE QUESTION THE BATCH NOW ASKS: exactly the gate's set, and nothing beside it. ──
        var refused = await NodeTypeCompilationHelpers.RefusedByUntrackedModuleGate(
                Mesh,
                new Dictionary<string, NodeTypeDefinition?>(StringComparer.OrdinalIgnoreCase)
                {
                    [viaBatch] = Definition(moduleContent: true),
                    [authored] = Definition(moduleContent: false),
                    [trackedModule] = Definition(moduleContent: true),
                },
                null)
            .Should().Within(TestTimeouts.Convergence).Emit("the question answers once", cancellationToken: ct);
        refused.Should().ContainSingle().Which.Should().Be(viaBatch,
            "only a module's content in a partition that tracks no source is refused a local compile — "
            + "authored content beside it and the same record under a tracked partition still compile");

        // ── ROUTE 1: the compile watcher (a forced release). This was always gated. ──
        using (Access.ImpersonateAsSystem())
            await Mesh.GetMeshNodeStream(viaWatcher)
                .Update<NodeTypeDefinition>(d => d with
                {
                    RequestedReleaseAt = DateTimeOffset.UtcNow,
                    RequestedReleaseForce = true,
                    RequestedReleaseBy = "operator",
                })
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var watcherVerdict = await Settled(viaWatcher, "the watcher's route settles the type");
        watcherVerdict.CompilationStatus.Should().Be(CompilationStatus.Error);
        watcherVerdict.CompilationError.Should().Contain("tracks no source").And.Contain("MeshWeaver#3583");

        // ── ROUTE 2: the boot sweep's batch compile — the route that bypassed the gate. ──
        var outcomes = await DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, batchBake: true, betweenTypes: TimeSpan.Zero)
            .ToList()
            .Should().Within(TestTimeouts.CrossSilo * 3).Emit("the sweep completes", cancellationToken: ct);

        var batchOutcome = outcomes.Where(o => string.Equals(o.TypePath, viaBatch, StringComparison.OrdinalIgnoreCase))
            .ToList();
        batchOutcome.Should().ContainSingle("the sweep REACHED the type — a sweep that skipped it proves nothing");
        batchOutcome[0].Status.Should().NotBe(PreWarmStatus.Compiled,
            $"the batch must not compile what the gate refuses; outcome detail: {batchOutcome[0].Detail}");

        var batchVerdict = await Settled(viaBatch, "the batch's route settles the type");
        batchVerdict.CompilationStatus.Should().Be(CompilationStatus.Error,
            $"the same record gets the same verdict whichever route reached it; error: {batchVerdict.CompilationError}");
        batchVerdict.CompilationError.Should().Be(watcherVerdict.CompilationError?.Replace(viaWatcher, viaBatch),
            "…word for word — the gate's named park, not a compile's diagnostics");
        batchVerdict.LatestAssemblyMvid.Should().Be(SeededMvid, "no Roslyn pass ran on the batch's route either");
        Parks.IsParked(viaBatch).Should().BeTrue("the refusal is the gate's deterministic park");
        Parks.GetCompileAttemptCount(viaBatch).Should().Be(0,
            "the park registry's attempt counter is the observable proof no compile was dispatched");
    }
}
