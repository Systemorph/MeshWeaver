using System;
using System.Collections.Immutable;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 Issue #7072: <b>a release re-cut the store never saw is re-issued on the SAME activation once
/// the store answers again.</b>
///
/// <para><b>The incident.</b> On memex on 2026-10-22 a managed database restart (#7068) refused writes
/// for about 40 seconds. Three Hosting NodeTypes settled inside that window; their release create and
/// its same-id re-cut were both answered <i>"could not be attempted: the data store was unreachable …
/// retrying the same request (with the same node id) is meaningful"</i>, and the settle stamped
/// <c>unreleasedBuildPath</c>. The pod stayed up and ready, and nothing on it ever made that create
/// again: the types advertised builds no release named for 76 minutes and 3 hours, until another pod
/// booted or the next compile ran.</para>
///
/// <para><b>The isolated fault.</b> <see cref="FaultInjectingStorageAdapter.RefuseWritesAsUnreachable"/>
/// on the ONE release path — a driver connection failure (SQLSTATE 08006), so the create handler's own
/// store-unreachable arm answers <c>Unavailable</c>, the production answer. Nothing else is perturbed.
/// The owner is activated BEFORE the stamp exists, so the next-activation obligation
/// (<see cref="LateReleaseAdoption.CompleteInheritedRelease"/>) cannot be what repairs it: only the
/// activation that holds the stamp can. Releasing the switch is the store coming back, and the
/// declared change-feed gap is what a PostgreSQL LISTEN reconnect announces after a restart.</para>
///
/// <para><b>Negative controls.</b> (1) While the store still refuses, the stamp stands — the re-issue
/// is not a loop that lands by persistence. (2) A stamp of class <see cref="ReleaseFailureClass.Refused"/>
/// is never re-issued, however often the store answers. (3) Recorded on the PR: with the re-issue
/// watch not installed, <see cref="AnUnavailableRecut_IsReissuedOnTheSameActivation_WhenTheStoreAnswers"/>
/// fails at its final <c>Match</c>, because nothing else on this activation ever creates the node.</para>
/// </summary>
public class AnUnavailableReleaseRecutIsReissuedWhenTheStoreAnswersTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string PreviousRelease = "Release/20261020115342-L-vbFJob";

    private FaultInjectingStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<FaultInjectingStorageAdapter>();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>The injector goes in BEFORE the base adds in-memory persistence.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.AddFaultInjectingStorage());

    /// <summary>
    /// A settled, usable build of THIS framework whose bytes are in the assembly store, with NO stamp,
    /// its owner activated. Returns the build and the release path the settle would mint for it.
    /// </summary>
    private async Task<(NodeCompilationResult Build, string Stamped, MeshNode Seeded)> SeedActivatedAsync(string typePath)
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = File.ReadAllBytes(typeof(AnUnavailableReleaseRecutIsReissuedWhenTheStoreAnswersTest).Assembly.Location);
        var location = await Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>()
            .PutWithLocation(typePath, 736, bytes, null)
            .Should().Within(TestTimeouts.WriteConvergence).Emit(cancellationToken: ct);
        var definition = new NodeTypeDefinition
        {
            Configuration = "config => config",
            CompilationStatus = CompilationStatus.Ok,
            LastCompiledVersion = 736,
            LatestReleasePath = $"{typePath}/{PreviousRelease}",
            LatestAssemblyCollection = location.Collection,
            LatestAssemblyPath = location.ContentPath,
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
            CompiledSources = ImmutableDictionary<string, long>.Empty,
        };
        await MeshService.CreateNode(MeshNode.FromPath(typePath) with
            {
                Name = typePath,
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = definition,
            })
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the NodeType must exist", cancellationToken: ct);

        // Reading the node through the stream activates its owner, and with it the watchers that
        // MeshDataSource installs on every NodeType hub. The first record it sees has no stamp, so
        // the next-activation obligation (#6056) has nothing to do.
        var seeded = await Mesh.GetMeshNodeStream(typePath)
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { LastCompiledVersion: 736 } d
                        && d.UnreleasedBuildPath is null,
                "the owner serves the settled, unstamped state");

        var build = LateReleaseAdoption.RecordedBuild(definition)!;
        var stamped = $"{typePath}/{GraphNodeTypeNames.ReleaseSegment}/20261022212254-{NodeTypeBuildState.ContentHashOf(build)}";
        return (build, stamped, seeded);
    }

    /// <summary>
    /// The settle's re-cut, through the production create path, while the store refuses it — and the
    /// settle's terminal stamp, through the production field-set (<c>ApplyCompileSuccess</c>).
    /// </summary>
    private async Task<NodeTypeBuildState.ReleaseCreateOutcome> RecutRefusedAndStampAsync(
        string typePath, NodeCompilationResult build, string stamped, MeshNode seeded, string? classOverride = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("MeshWeaver.Graph.CompileWatcher");
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var recut = await access
            .RunAsSystem(() => NodeTypeBuildState.TryCreateReleaseNode(
                Mesh, typePath, build, seeded, activityPath: null, logger, reusePath: stamped))
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the re-cut answers", cancellationToken: ct);
        Output.WriteLine($"re-cut: path={recut.ReleasePath} class={recut.FailureClass} because={recut.Because}");

        recut.Succeeded.Should().BeFalse("the store refused the write");
        recut.AttemptedPath.Should().Be(stamped, "the re-cut names the id it was minting");
        recut.FailureClass.Should().Be(ReleaseFailureClass.Unavailable,
            "a create the store never saw is classified Unavailable AT THE ARM, from the typed rejection "
            + "reason — never parsed out of the sentence");

        await Mesh.GetMeshNodeStream(typePath).Update(current =>
            {
                var def = current.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
                return current with
                {
                    Content = NodeTypeCompilationHelpers.ApplyCompileSuccess(
                        def, build, current.Version, activityPath: null, releasePath: null,
                        modulesHash: null, unreleasedBuildPath: recut.AttemptedPath,
                        unreleasedBuildReason: recut.Failure,
                        unreleasedBuildFailureClass: classOverride ?? recut.FailureClass),
                };
            })
            .Should().Within(TestTimeouts.WriteConvergence)
            .Emit("the settle's terminal stamp lands", cancellationToken: ct);
        return recut;
    }

    private void TheStoreAnswersAgain()
        => Storage.DeliverChangeFeedGap(new ChangeFeedGap(
            "postgresql:mesh_node_changes",
            DateTimeOffset.UtcNow.AddSeconds(-40),
            DateTimeOffset.UtcNow,
            "the managed database restarted (#7068)"));

    /// <summary>
    /// 🚨 THE CASE: refused as Unavailable, stamped, the store comes back, and the SAME activation
    /// creates the release at the stamped id and adopts it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnUnavailableRecut_IsReissuedOnTheSameActivation_WhenTheStoreAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        var typePath = $"{TestPartition}/PrWatch{Guid.NewGuid().ToString("N")[..8]}";
        var (build, stamped, seeded) = await SeedActivatedAsync(typePath);

        var refusal = Storage.RefuseWritesAsUnreachable(stamped);
        try
        {
            await RecutRefusedAndStampAsync(typePath, build, stamped, seeded);
            await refusal.Arrivals.Should().Within(TestTimeouts.Convergence)
                .Emit("the re-cut's write reached the unreachable store", ct);

            var standing = await Mesh.GetMeshNodeStream(typePath)
                .Should().Within(TestTimeouts.WriteConvergence)
                .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)?.UnreleasedBuildPath == stamped,
                    "the build with no release is stamped on the node");
            standing.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!.UnreleasedBuildFailureClass
                .Should().Be(ReleaseFailureClass.Unavailable, "the class travels with the stamp");

            // Negative control 1: while the store still refuses, nothing lands — the pointer stays
            // on the earlier build's release.
            await Mesh.GetMeshNodeStream(typePath)
                .Where(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)?.LatestReleasePath == stamped)
                .Should().NotEmit(TestTimeouts.Quick, "the store has not come back yet");
        }
        finally
        {
            // The store comes back.
            refusal.Release();
        }

        TheStoreAnswersAgain();

        var adopted = await Mesh.GetMeshNodeStream(typePath)
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)?.LatestReleasePath == stamped,
                "the activation that holds an Unavailable stamp must re-issue the create at the stamped "
                + "id once the store answers again, and adopt it — nothing else on this activation ever "
                + "creates that node (#7072)");
        var def = adopted.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
        def.UnreleasedBuildPath.Should().BeNull("the build has a release now");
        def.UnreleasedBuildReason.Should().BeNull("the reason never outlives the path");
        def.UnreleasedBuildFailureClass.Should().BeNull("the class never outlives the path");
        def.LastCompiledVersion.Should().Be(736L, "the re-issue recompiles nothing");

        var release = await Mesh.GetMeshNodeStream(stamped)
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n.ContentAs<NodeTypeRelease>(Mesh.JsonSerializerOptions) is not null,
                "the release node exists at the ORIGINAL stamped id — not a fresh one");
        release.ContentAs<NodeTypeRelease>(Mesh.JsonSerializerOptions)!.AssemblyStoreVersion
            .Should().Be(736L, "the release names the recorded build");
    }

    /// <summary>
    /// Negative control 2: a stamp the store DID judge (class <see cref="ReleaseFailureClass.Refused"/>)
    /// is a named failure, never re-issued — the same refusal and the same recovery signals as the
    /// case above, and the stamp stands.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARefusedRecut_IsNeverReissued_HoweverOftenTheStoreAnswers()
    {
        var typePath = $"{TestPartition}/TriageItem{Guid.NewGuid().ToString("N")[..8]}";
        var (build, stamped, seeded) = await SeedActivatedAsync(typePath);

        using (Storage.RefuseWritesAsUnreachable(stamped))
            await RecutRefusedAndStampAsync(typePath, build, stamped, seeded, ReleaseFailureClass.Refused);

        await Mesh.GetMeshNodeStream(typePath)
            .Should().Within(TestTimeouts.WriteConvergence)
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        && d.UnreleasedBuildPath == stamped
                        && d.UnreleasedBuildFailureClass == ReleaseFailureClass.Refused,
                "the refused build is stamped as such", TestContext.Current.CancellationToken);

        TheStoreAnswersAgain();
        TheStoreAnswersAgain();

        await Mesh.GetMeshNodeStream(typePath)
            .Where(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        && (d.UnreleasedBuildPath is null || d.LatestReleasePath == stamped))
            .Should().NotEmit(TestTimeouts.Quick, "a verdict is never re-issued — the stamp is the report");
    }

    // ───────────── the decision, pure ─────────────

    [Fact]
    public void UnavailableObligation_NamesTheStamp_OnlyForTheUnavailableClass()
    {
        const string collection = "assemblies";
        const string contentPath = "T/v736-build.dll";
        var hash = NodeTypeBuildState.ContentHashOf(new NodeCompilationResult(
            null, [], Collection: collection, ContentPath: contentPath));
        var stamped = $"T/Release/20261022212254-{hash}";
        var framework = NodeTypeCompilationHelpers.FrameworkVersion;
        var owed = new NodeTypeDefinition
        {
            CompilationStatus = CompilationStatus.Ok,
            LatestAssemblyCollection = collection,
            LatestAssemblyPath = contentPath,
            CompiledFrameworkVersion = framework,
            UnreleasedBuildPath = stamped,
            UnreleasedBuildFailureClass = ReleaseFailureClass.Unavailable,
        };

        LateReleaseAdoption.UnavailableObligation(owed, "T", framework).Should().Be(stamped);
        foreach (var other in new[] { ReleaseFailureClass.Refused, ReleaseFailureClass.Expired,
                     ReleaseFailureClass.Faulted, "SomeFutureClass", null })
            LateReleaseAdoption.UnavailableObligation(owed with { UnreleasedBuildFailureClass = other }, "T", framework)
                .Should().BeNull($"class '{other ?? "(none)"}' is not one the store never saw");
        LateReleaseAdoption.UnavailableObligation(owed with { LatestAssemblyPath = "T/v737-build.dll" }, "T", framework)
            .Should().BeNull("a stamp naming other bytes is never cut for these");
        LateReleaseAdoption.UnavailableObligation(owed with { CompilationStatus = CompilationStatus.Compiling }, "T", framework)
            .Should().BeNull("a compile in flight rewrites the stamp at its own settle");
    }
}
