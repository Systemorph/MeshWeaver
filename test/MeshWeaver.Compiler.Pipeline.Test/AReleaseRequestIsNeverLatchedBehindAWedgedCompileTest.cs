using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 <b>A release request is never latched behind an in-flight status that will never settle.</b>
///
/// <para>Measured on a production portal (control triage item fb-3dfefa64): a NodeType compiled once,
/// then every <c>compile</c> call timed out with no activity created — <c>requestedReleaseAt</c>
/// advanced on each request while <c>lastReleaseRequestHandledAt</c> never moved, and three recycles
/// re-bound the same record. <c>Pending</c>/<c>Compiling</c> is the single-flight lock, and the
/// release watcher deferred a request behind it SILENTLY: a compile that never lands (a Pending flip
/// nobody dispatched, a Compiling whose activity never started) therefore held every later request
/// for good.</para>
///
/// <para>Now a status that has outlived any real compile is settled <c>Unavailable</c> with its reason
/// (and a Warning in the log), the own node re-emits at a settled status, and the same watcher
/// dispatches the request. The control: a RECENT in-flight compile still defers the request.</para>
/// </summary>
public class AReleaseRequestIsNeverLatchedBehindAWedgedCompileTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string TypePath = "type/Wedged" + Guid.NewGuid().ToString("N")[..8];

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact]
    public void AnInFlightStatusIsStaleOnlyPastTheBound()
    {
        var now = DateTimeOffset.UtcNow;
        var old = now - NodeTypeCompilationHelpers.InFlightStaleAfter - TimeSpan.FromMinutes(1);
        var compiling = new NodeTypeDefinition { CompilationStatus = CompilationStatus.Compiling };
        NodeTypeCompilationHelpers.InFlightCompileIsStale(compiling with { LastCompileStartedAt = old }, now)
            .Should().Be(old);
        NodeTypeCompilationHelpers.InFlightCompileIsStale(compiling with { LastCompileStartedAt = now.AddSeconds(-30) }, now)
            .Should().BeNull("a recent compile is genuinely in flight");
        NodeTypeCompilationHelpers.InFlightCompileIsStale(compiling, now)
            .Should().BeNull("with no start recorded nothing can be said");

        var pending = new NodeTypeDefinition { CompilationStatus = CompilationStatus.Pending };
        NodeTypeCompilationHelpers.InFlightCompileIsStale(pending with { LastReleaseRequestHandledAt = old }, now)
            .Should().Be(old);
        NodeTypeCompilationHelpers.InFlightCompileIsStale(
                pending with { LastCompileStartedAt = old, LastReleaseRequestHandledAt = now.AddSeconds(-5) }, now)
            .Should().BeNull("the later of the two marks is when this Pending began");
        NodeTypeCompilationHelpers.InFlightCompileIsStale(
                new NodeTypeDefinition { CompilationStatus = CompilationStatus.Ok, LastCompileStartedAt = old }, now)
            .Should().BeNull("only an in-flight status can be wedged");
    }

    [Theory(Timeout = 180_000)]
    [InlineData(CompilationStatus.Compiling)]
    [InlineData(CompilationStatus.Pending)]
    public async Task ARequestAfterAWedgedInFlightStatus_IsHandledAndBuilds(CompilationStatus wedge)
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateType();
        var first = await Settled(d => d.LatestAssemblyPath is not null && d.LastCompileStartedAt is not null);
        // The first compile's own activity must have FINISHED: a Running, just-written activity is a
        // live owner by definition, and the wedge below is the one whose owner is gone.
        if (first.LastCompilationActivityPath is { } firstActivity)
            await Mesh.GetMeshNodeStream(firstActivity).Should().Within(60.Seconds())
                .Match(n => n.ContentAs<MeshWeaver.Data.ActivityLog>(Mesh.JsonSerializerOptions)
                        is { Status: not MeshWeaver.Data.ActivityStatus.Running },
                    "the first compile's activity has finished", ct);

        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        await Mesh.GetMeshNodeStream(TypePath)
            .Update<NodeTypeDefinition>(d => d with
            {
                CompilationStatus = wedge,
                LastCompileStartedAt = hourAgo,
                LastReleaseRequestHandledAt = hourAgo,
                RequestedReleaseAt = hourAgo,
            })
            .Should().Within(60.Seconds()).Emit(cancellationToken: ct);

        var requestedAt = DateTimeOffset.UtcNow;
        await Mesh.GetMeshNodeStream(TypePath)
            .Update<NodeTypeDefinition>(d => d with { RequestedReleaseAt = requestedAt, RequestedReleaseBy = "test" })
            .Should().Within(60.Seconds()).Emit(cancellationToken: ct);

        var after = await Settled(d => d.LastReleaseRequestHandledAt >= requestedAt && d.LastCompileStartedAt > hourAgo,
            "the request must be HANDLED, not latched behind the wedged status");
        after.CompilationStatus.Should().Be(CompilationStatus.Ok);
    }

    [Fact(Timeout = 120_000)]
    public async Task ARequestWhileARecentCompileIsInFlight_StillWaitsForIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateType();
        await Settled(d => d.LatestAssemblyPath is not null && d.LastCompileStartedAt is not null);

        await Mesh.GetMeshNodeStream(TypePath)
            .Update<NodeTypeDefinition>(d => d with
            {
                CompilationStatus = CompilationStatus.Compiling,
                LastCompileStartedAt = DateTimeOffset.UtcNow,
            })
            .Should().Within(60.Seconds()).Emit(cancellationToken: ct);
        var requestedAt = DateTimeOffset.UtcNow;
        await Mesh.GetMeshNodeStream(TypePath)
            .Update<NodeTypeDefinition>(d => d with { RequestedReleaseAt = requestedAt, RequestedReleaseBy = "test" })
            .Should().Within(60.Seconds()).Emit(cancellationToken: ct);

        await Mesh.GetMeshNodeStream(TypePath)
            .Where(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                && (d.LastReleaseRequestHandledAt >= requestedAt
                    || d.CompilationStatus is not CompilationStatus.Compiling))
            .Should().NotEmit(5.Seconds(), "a genuinely running compile owns the single-flight lock",
                cancellationToken: ct);
    }

    /// <summary>
    /// A compile past the age floor that a LIVE owner still holds — its activity is Running and was
    /// written to just now — is never settled out from under it: age is only the floor.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ALongCompileWithALiveOwnerPastTheFloor_IsNotSettled()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateType();
        await Settled(d => d.LatestAssemblyPath is not null && d.LastCompileStartedAt is not null);

        var activityId = "live" + Guid.NewGuid().ToString("N")[..8];
        var activityPath = $"{TypePath}/_Activity/{activityId}";
        await MeshService.CreateNode(new MeshNode(activityId, $"{TypePath}/_Activity")
            {
                Name = "Compile (live owner)",
                NodeType = GraphNodeTypeNames.Activity,
                MainNode = TypePath,
                State = MeshNodeState.Active,
                Content = new MeshWeaver.Data.ActivityLog(MeshWeaver.Data.ActivityCategory.Compilation)
                {
                    Id = activityId,
                    HubPath = TypePath,
                    Status = MeshWeaver.Data.ActivityStatus.Running,
                },
            })
            .Should().Within(60.Seconds()).Emit(cancellationToken: ct);

        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        await Mesh.GetMeshNodeStream(TypePath)
            .Update<NodeTypeDefinition>(d => d with
            {
                CompilationStatus = CompilationStatus.Compiling,
                LastCompileStartedAt = hourAgo,
                LastReleaseRequestHandledAt = hourAgo,
                RequestedReleaseAt = hourAgo,
                LastCompilationActivityPath = activityPath,
            })
            .Should().Within(60.Seconds()).Emit(cancellationToken: ct);
        var requestedAt = DateTimeOffset.UtcNow;
        await Mesh.GetMeshNodeStream(TypePath)
            .Update<NodeTypeDefinition>(d => d with { RequestedReleaseAt = requestedAt, RequestedReleaseBy = "test" })
            .Should().Within(60.Seconds()).Emit(cancellationToken: ct);

        await Mesh.GetMeshNodeStream(TypePath)
            .Where(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                && (d.LastReleaseRequestHandledAt >= requestedAt
                    || d.CompilationStatus is not CompilationStatus.Compiling))
            .Should().NotEmit(8.Seconds(), "a compile whose activity is alive owns the lock however long it has run",
                cancellationToken: ct);
    }

    private async Task CreateType()
    {
        await MeshService.CreateNode(MeshNode.FromPath(TypePath) with
            {
                Name = TypePath[(TypePath.LastIndexOf('/') + 1)..],
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition
                {
                    Configuration = "config => config.WithContentType<WedgedContent>()",
                },
            })
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("api", $"{TypePath}/Source")
            {
                NodeType = "Code",
                Name = "api",
                State = MeshNodeState.Active,
                Content = new CodeConfiguration
                {
                    Language = "csharp",
                    Code = "public record WedgedContent { public string Title { get; init; } = \"\"; }",
                },
            }))
            .Should().Within(60.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await Mesh.GetMeshNodeStream(TypePath)
            .Update<NodeTypeDefinition>(d => d with { RequestedReleaseAt = DateTimeOffset.UtcNow, RequestedReleaseBy = "test" })
            .Should().Within(60.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<NodeTypeDefinition> Settled(Func<NodeTypeDefinition, bool> predicate, string because = "")
    {
        var node = await Mesh.GetMeshNodeStream(TypePath)
            .Should().Within(90.Seconds())
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                    is { CompilationStatus: CompilationStatus.Ok } d && predicate(d),
                because, TestContext.Current.CancellationToken);
        return node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
    }
}
