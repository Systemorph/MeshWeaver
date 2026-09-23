using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Issue #5358 — <b>a release create must not be ISSUED from the NodeType hub the compile settled
/// on</b>, because that hub is exactly the one a successful compile gets recycled out from under.
///
/// <para><b>The incident.</b> On memex-cloud, <c>Marketing/Event</c> compiled successfully
/// (lastCompiledVersion 644 → 648) and both the settle's own release create and the
/// post-condition's re-cut failed, one second apart, with
/// <c>HubDisposedBeforeResponseException: Hub Marketing/Event was disposed before the response
/// arrived (request type CreateNodeRequest, target portal/nodeops-…)</c>. The create was not
/// refused — its REPLY was addressed to the issuing hub, which was tearing down: when that hub's
/// Quiescing budget ran out it cancelled every pending callback, and the create was reported as a
/// failure while the owner was still working on it. The node was left advertising a build no
/// release names.</para>
///
/// <para><b>The control.</b> A stand-in for the NodeType hub — a hosted hub with its OWN lifetime
/// scope, whose scoped <see cref="IMeshService"/> is bound to it exactly as a per-node hub's is —
/// issues the create through the production <see cref="NodeTypeBuildState.TryCreateReleaseNode"/>.
/// A real creation validator parks the create at the owner (so it is provably IN FLIGHT, not merely
/// posted), the stand-in is disposed and its scope proven closed, and only then is the create let
/// through. With the create issued from the survivor the outcome is <c>Landed</c>; before the fix
/// it was <c>Failed</c> with <c>HubDisposedBeforeResponseException</c> naming the stand-in.</para>
///
/// <para>🚨 No hand-woven gate: the park is an <see cref="AsyncSubject{T}"/> the validator's answer
/// is composed from and the test completes in a <c>finally</c>; "entered" is a
/// <see cref="ReplaySubject{T}"/> awaited through the assertion helpers. Nothing sleeps.</para>
/// </summary>
public class AReleaseCreateSurvivesItsNodeTypeHubTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Path infix that opts a Release create INTO the park; nothing else is touched.</summary>
    private const string ParkMarker = "ReleaseSurvivorParked";

    /// <summary>
    /// A creation validator that holds a Release create under <see cref="ParkMarker"/> until the
    /// test lets it through — the create is then genuinely in flight at the owner while the issuing
    /// hub goes away. A real <see cref="INodeValidator"/> in the mesh's own service collection, not a
    /// mock of one.
    /// </summary>
    private sealed class ParkingReleaseValidator : INodeValidator
    {
        private readonly ReplaySubject<string> entered = new(1);
        private readonly AsyncSubject<Unit> letThrough = new();

        /// <summary>Emits the path of each Release create this validator parked.</summary>
        public IObservable<string> Entered => entered;

        /// <summary>Lets every parked create (and every later one) through. Idempotent.</summary>
        public void LetThrough()
        {
            letThrough.OnNext(Unit.Default);
            letThrough.OnCompleted();
        }

        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Create];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        {
            var node = context.Node;
            if (node.Path is not { } path
                || !path.Contains(ParkMarker, StringComparison.Ordinal)
                || !string.Equals(node.NodeType, GraphNodeTypeNames.Release, StringComparison.Ordinal))
                return Observable.Return(NodeValidationResult.Valid());

            return Observable.Defer(() =>
            {
                entered.OnNext(path);
                return letThrough.Select(_ => NodeValidationResult.Valid());
            });
        }
    }

    // Field initializers run before the base constructor calls ConfigureMesh, so this instance is
    // the one registered below. Instance state, never static.
    private readonly ParkingReleaseValidator parking = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INodeValidator>(parking));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static ImmutableDictionary<string, long> Sources(string typePath, long version) =>
        ImmutableDictionary<string, long>.Empty.Add($"{typePath}/Source/Event", version);

    /// <summary>The compile that just succeeded — store version 648, the incident's build.</summary>
    private static NodeCompilationResult Built(string typePath) => new(
        AssemblyLocation: $"/cache/{typePath.Replace('/', '_')}/Event.dll",
        NodeTypeConfigurations: [],
        CompiledSources: Sources(typePath, 648),
        Collection: "local",
        ContentPath: $"{typePath.Replace('/', '_')}/v648-s47313cc-16eb81505f5b.dll",
        Version: 648);

    /// <summary>The definition as the compile watcher observed it at dispatch: a CONSUMED release
    /// request and a release cut for the PREVIOUS build (644) — the incident's shape.</summary>
    private static NodeTypeDefinition Consumed(string typePath) => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Compiling,
        RequestedReleaseAt = new DateTimeOffset(2026, 8, 23, 8, 41, 53, TimeSpan.Zero),
        LastReleaseRequestHandledAt = new DateTimeOffset(2026, 8, 23, 8, 41, 53, TimeSpan.Zero),
        LastCompiledVersion = 644,
        LatestAssemblyCollection = "local",
        LatestAssemblyPath = $"{typePath.Replace('/', '_')}/v644-s47313cc-16eb81505f5b.dll",
        LatestReleasePath = $"{typePath}/Release/20260922113506-PJ-8Q5x5",
        CompiledSources = Sources(typePath, 644),
    };

    private async Task<MeshNode> SeedTypeAsync(string typePath)
    {
        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = typePath,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = Consumed(typePath),
        };
        await MeshService.CreateNode(typeNode)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the NodeType whose release is cut must exist",
                cancellationToken: TestContext.Current.CancellationToken);
        return typeNode;
    }

    /// <summary>
    /// A hub standing in for the NodeType hub: a hosted hub of the mesh, so it OWNS a child lifetime
    /// scope — and with it its own scoped <see cref="IMeshService"/>, bound to it as a per-node hub's
    /// is. That binding is the whole mechanism: a create issued through it has its reply addressed
    /// to it.
    /// </summary>
    private IMessageHub StandInForTheNodeTypeHub(string discriminator)
    {
        var standIn = Mesh.GetHostedHub(
            new Address("portal", $"release-issuer-{discriminator}-{Guid.NewGuid().ToString("N")[..8]}"),
            config => config,
            HostedHubCreation.Always);
        standIn.Should().NotBeNull("the stand-in NodeType hub must exist");
        return standIn!;
    }

    /// <summary>
    /// Disposes <paramref name="standIn"/>, waits for its terminal signal, and PROVES the
    /// precondition both cases rest on: its DI scope is closed. Asserted, not assumed — a scope that
    /// stayed open would let the cases pass having measured nothing.
    /// </summary>
    private static async Task TearDownAndProveTheScopeIsClosed(IMessageHub standIn)
    {
        var down = standIn.DisposalCompleted.Take(1);
        standIn.Dispose();
        await down.Should().Within(TestTimeouts.Convergence)
            .Emit("the stand-in NodeType hub must actually finish disposing",
                cancellationToken: TestContext.Current.CancellationToken);
        Action resolveFromTheDeadHub = () => standIn.ServiceProvider.GetService<ILoggerFactory>();
        resolveFromTheDeadHub.Should().Throw<ObjectDisposedException>(
            "the point of these cases is that the NodeType hub is GONE — if its scope still "
            + "resolves, they prove nothing about #5358");
    }

    /// <summary>Every release node under <paramref name="typePath"/>, by LISTING.</summary>
    private IObservable<IReadOnlyCollection<MeshNode>> ReleasesOf(string typePath) =>
        MeshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{typePath}/{GraphNodeTypeNames.ReleaseSegment} scope:children nodeType:{GraphNodeTypeNames.Release}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Select(c => (IReadOnlyCollection<MeshNode>)c.Items.ToArray())
            .Take(1);

    /// <summary>
    /// 🚨 THE REGRESSION, in the incident's shape: the create is in flight at the owner when the
    /// hub it was started on goes away. It must be reported as the landing it is.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACreateInFlight_WhenTheNodeTypeHubIsDisposed_IsReportedLanded()
    {
        var typePath = $"{TestPartition}/{ParkMarker}{Guid.NewGuid().ToString("N")[..8]}";
        var typeNode = await SeedTypeAsync(typePath);
        var result = Built(typePath);
        var standIn = StandInForTheNodeTypeHub("in-flight");

        var outcome = new ReplaySubject<NodeTypeBuildState.ReleaseCreateOutcome>(1);
        // Held for the whole test: the outcome arrives only after the park is released below.
        using var creating = NodeTypeBuildState
            .TryCreateReleaseNode(standIn, typePath, result, typeNode, activityPath: null, logger: null)
            .Subscribe(outcome.OnNext, outcome.OnError);
        try
        {
            var parkedAt = await parking.Entered
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the create must reach the owner and be held there — that is what makes it IN "
                      + "FLIGHT when its issuing hub goes away",
                    cancellationToken: TestContext.Current.CancellationToken);
            parkedAt.Should().StartWith($"{typePath}/{GraphNodeTypeNames.ReleaseSegment}/");

            await TearDownAndProveTheScopeIsClosed(standIn);
        }
        finally
        {
            // Never strand the owner's create chain, whatever the assertions above did.
            parking.LetThrough();
        }

        var answered = await outcome.Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("TryCreateReleaseNode always answers exactly once",
                cancellationToken: TestContext.Current.CancellationToken);

        answered.Failure.Should().BeNull(
            "the create was never refused — before #5358 its reply was addressed to the NodeType hub, "
            + "whose teardown cancelled the pending callback with HubDisposedBeforeResponseException "
            + "while the owner was still creating the node");
        answered.Succeeded.Should().BeTrue("the create landed, so a release exists for these bytes");
        answered.ReleasePath.Should().EndWith("-" + NodeTypeBuildState.ContentHashOf(result));

        var releases = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => ReleasesOf(typePath))
            .Where(items => items.Any(n => string.Equals(n.Path, answered.ReleasePath, StringComparison.OrdinalIgnoreCase)))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the release the outcome names must exist in the store",
                cancellationToken: TestContext.Current.CancellationToken);
        releases.Should().ContainSingle("one create, one release node");
    }

    /// <summary>
    /// The other half of the window: the post-condition's re-cut runs on the FIRST attempt's failure,
    /// i.e. typically after the NodeType hub has already gone. It must re-cut through the survivor,
    /// not resolve anything out of the dead hub's closed scope (before #5358 <c>Restore</c> read its
    /// <c>AccessService</c> through it and threw <see cref="ObjectDisposedException"/>).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheRecut_WhenTheNodeTypeHubIsAlreadyDown_LandsAtTheAttemptedId()
    {
        var typePath = $"{TestPartition}/ReleaseSurvivorRecut{Guid.NewGuid().ToString("N")[..8]}";
        var typeNode = await SeedTypeAsync(typePath);
        var result = Built(typePath);
        var standIn = StandInForTheNodeTypeHub("already-down");
        await TearDownAndProveTheScopeIsClosed(standIn);

        // The settle's own attempt, as the incident recorded it: made, and cancelled by the teardown.
        var attempted = $"{typePath}/{GraphNodeTypeNames.ReleaseSegment}/{DateTime.UtcNow:yyyyMMddHHmmss}-"
                        + NodeTypeBuildState.ContentHashOf(result);
        var firstAttempt = NodeTypeBuildState.ReleaseCreateOutcome.Failed(
            $"the create at '{attempted}' failed: HubDisposedBeforeResponseException", attempted);

        var settle = await ReleasePostCondition
            .Restore(standIn, typePath, result, typeNode, activityPath: null, firstAttempt, logger: null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("Restore always answers exactly once, and must not read through the dead hub",
                cancellationToken: TestContext.Current.CancellationToken);

        settle.ReleasePath.Should().Be(attempted,
            "the re-cut is issued from the survivor and lands at the id the first attempt was minting");
        settle.UnreleasedBuildPath.Should().BeNull("a release now exists for these bytes");
    }
}
