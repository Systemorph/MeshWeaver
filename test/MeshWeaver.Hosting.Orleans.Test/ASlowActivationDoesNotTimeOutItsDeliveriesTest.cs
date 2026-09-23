using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Issues #5286 / #5417: a per-node grain whose hub takes longer to build than Orleans'
/// <c>ResponseTimeout</c> must still serve the deliveries that arrived while it was building.
///
/// <para><b>The defect.</b> <c>MessageHubGrain.DeliverMessage</c> returned a task that completed only
/// when <c>HubReady</c> emitted, so the Orleans call's ACKNOWLEDGEMENT waited on the whole hub
/// activation — node resolution, NodeType binding, a cold compile — which the design deliberately
/// lets run past 30 s. Orleans' <c>ResponseTimeout</c> (a caller-side give-up timer, 30 s in
/// production) therefore fired first, <c>RoutingGrain</c> received a <see cref="TimeoutException"/>
/// and NACKed the sender with the terminal <c>"Response did not arrive on time in 00:00:30"</c> —
/// while the delivery was still parked in the grain and was posted to the hub moments later.
/// Production printed that shape for <c>messagehub/Store</c>, <c>messagehub/Underwriting</c> and
/// <c>messagehub/AgenticEngineering</c>: a young activation (<c>Total Enqueued=5</c>),
/// <c>NumRunning=5</c>, <c>QueuedWorkItems=0</c> — every running item a parked DeliverMessage.</para>
///
/// <para><b>The reproduction.</b> The cluster's <c>ResponseTimeout</c> is shortened (the only thing
/// that makes the window affordable in a test — the ratio between the two clocks is what matters,
/// not their absolute size) and the node's hub-configuration resolution is HELD by a
/// test-controlled gate for several response timeouts. A request is issued while the hold is in
/// place: it must not fail during the hold, and it must be ANSWERED once the hold is released.</para>
///
/// <para><b>The second half of the same contract.</b> Answering on acceptance leaves no grain call
/// in flight, and a call in flight is what kept the activation from idle collection while it was
/// building. The cluster's <c>CollectionAge</c> is therefore also shortened well below the hold:
/// an activation that is not pinned for its build is collected mid-hold, which completes
/// <c>HubReady</c> and NACKs the accepted delivery as <c>ShuttingDown</c> — the same failed
/// assertion, for a different reason.</para>
/// </summary>
public class ASlowActivationDoesNotTimeOutItsDeliveriesTest(ITestOutputHelper output)
    : OrleansMeshTestBase(output)
{
    /// <inheritdoc />
    protected override Type SiloConfiguratorType => typeof(HeldActivationSiloConfigurator);

    /// <summary>
    /// How long the activation is held: several of the cluster's response timeouts, so an
    /// acknowledgement that waits on the activation is certain to be given up on by the caller
    /// (Orleans checks expiry on its own coarse timer, hence more than one), while staying well
    /// inside the 60 s request ceiling the sender itself carries.
    /// </summary>
    private static readonly TimeSpan HoldFor = HeldActivationSiloConfigurator.ResponseTimeout * 3;

    private IMessageHub SiloMesh =>
        ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services.GetRequiredService<IMessageHub>();

    [Fact(Timeout = 120_000)]
    public async Task ADeliveryToAHubStillBeingBuilt_IsAnsweredOnceTheBuildCompletes_NotTimedOut()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(110));
        var ct = deadline.Token;

        // Issued as SYSTEM from the node-operation issuing hub: the node lives in a partition no
        // test identity holds a grant on, and the subject here is the grain's acknowledgement, not
        // the access gate.
        var access = SiloMesh.ServiceProvider.GetRequiredService<AccessService>();
        var issuing = SiloMesh.NodeOperationIssuingHub();
        var holds = SiloMesh.ServiceProvider.GetRequiredService<ActivationHolds>();
        var meshService = SiloMesh.ServiceProvider.GetRequiredService<IMeshService>();

        var path = $"held/{Guid.NewGuid():N}";
        var address = new Address(path);
        var release = holds.Hold(path);
        try
        {
            await access.RunAsSystem(() => meshService.CreateNode(MeshNode.FromPath(path) with
            {
                Name = "A node whose hub takes longer to build than a grain call may wait",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            })).FirstAsync().Await(ct);

            // The sender's whole outcome, materialized and REPLAYED so nothing it says is missed
            // between the assertions below, whichever arm it arrives on.
            var outcome = access.RunAsSystem(() => issuing.Observe(
                    new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(address)))
                .Take(1)
                .Materialize()
                .Replay();
            using var connected = outcome.Connect();

            await holds.Entered(path).Should().Within(TestTimeouts.Convergence).Emit(
                "the request reaches the grain and starts its hub build, which parks on the hold — "
                + "the precondition for everything below", ct);

            // Projected to the failure TEXT so a red run names which of the two defects it caught:
            // "Response did not arrive on time" (the ack waited on the build) or "Hub disposed
            // before delivery" (the building activation was collected, nothing pinned it).
            await outcome.Where(n => n.Kind == NotificationKind.OnError)
                .Select(n => $"{n.Exception!.GetType().Name}: {n.Exception.Message}")
                .Should().NotEmit(HoldFor,
                $"the activation is merely SLOW (held {HoldFor.TotalSeconds:0}s against a "
                + $"{HeldActivationSiloConfigurator.ResponseTimeout.TotalSeconds:0}s ResponseTimeout); "
                + "the delivery is parked in the grain and will be posted the moment the hub exists, "
                + "so the sender must not be told it failed — that is the production "
                + "\"Response did not arrive on time\" DeliveryFailure of #5286 / #5417", ct);

            release.Dispose();

            var answered = await outcome.Should().Within(TestTimeouts.Convergence).Emit(
                "once the build completes, the parked delivery is served by the hub", ct);
            answered.Kind.Should().Be(NotificationKind.OnNext,
                $"the parked request is ANSWERED, not failed — got {answered.Kind}: {answered.Exception?.Message}");
        }
        finally
        {
            // Never strand the grain on the hold if an assertion above failed first.
            release.Dispose();
        }
    }
}

/// <summary>
/// A mesh-scoped registry of held hub builds: a path listed here has its hub-configuration
/// resolution parked until the test releases it. Instance state on a singleton the silo's mesh
/// owns — never static — so it dies with the cluster.
/// </summary>
public sealed class ActivationHolds
{
    private readonly ConcurrentDictionary<string, AsyncSubject<Unit>> held = new();
    private readonly ReplaySubject<string> entered = new();

    /// <summary>Holds every hub build for <paramref name="path"/> until the returned handle is disposed.</summary>
    /// <param name="path">The node path whose activation is held.</param>
    /// <returns>The release; disposing it more than once is harmless.</returns>
    public IDisposable Hold(string path)
    {
        var gate = held.GetOrAdd(path, _ => new AsyncSubject<Unit>());
        return System.Reactive.Disposables.Disposable.Create(() =>
        {
            gate.OnNext(Unit.Default);
            gate.OnCompleted();
        });
    }

    /// <summary>Emits once a hub build for <paramref name="path"/> has reached its hold.</summary>
    /// <param name="path">The node path.</param>
    /// <returns>A signal that emits when the build is parked on the hold.</returns>
    public IObservable<string> Entered(string path) => entered.Where(p => p == path).Take(1);

    /// <summary>The wait a hub build for <paramref name="path"/> must pass before it may proceed.</summary>
    /// <param name="path">The node path being built.</param>
    /// <returns>Completes at once for a path that is not held.</returns>
    internal IObservable<Unit> Gate(string path)
    {
        if (!held.TryGetValue(path, out var gate))
            return Observable.Return(Unit.Default);
        entered.OnNext(path);
        return gate;
    }
}

/// <summary>
/// Wraps the mesh's real <see cref="IMeshNodeHubFactory"/> so a held path's configuration
/// resolution waits on <see cref="ActivationHolds"/> first — the stand-in for a cold compile or a
/// slow NodeType binding, and the one step of a grain activation that is allowed to be slow.
/// </summary>
internal sealed class HoldingMeshNodeHubFactory(IMeshNodeHubFactory inner, ActivationHolds holds)
    : IMeshNodeHubFactory
{
    /// <inheritdoc />
    public IObservable<MeshNode> ResolveHubConfiguration(MeshNode node) =>
        holds.Gate(node.Path).Take(1).SelectMany(_ => inner.ResolveHubConfiguration(node));
}

/// <summary>
/// The stock silo, with a short <c>ResponseTimeout</c> and the hub factory wrapped by
/// <see cref="HoldingMeshNodeHubFactory"/>.
/// </summary>
public class HeldActivationSiloConfigurator : SharedSiloConfigurator, ISiloConfigurator
{
    /// <summary>The cluster's Orleans ResponseTimeout — production's is 30 s.</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long an activation may sit idle before Orleans collects it — production's is 15 min.
    /// Shortened well below the hold, so an activation that is NOT pinned while it builds is
    /// collected mid-build (which completes HubReady and NACKs every accepted delivery as
    /// ShuttingDown). Before #5286 the parked grain calls pinned it; now the build must.
    /// </summary>
    public static readonly TimeSpan CollectionAge = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    void ISiloConfigurator.Configure(ISiloBuilder siloBuilder)
    {
        Configure(siloBuilder);
        siloBuilder.Configure<SiloMessagingOptions>(o => o.ResponseTimeout = ResponseTimeout);
        siloBuilder.Configure<GrainCollectionOptions>(o =>
        {
            o.CollectionQuantum = TimeSpan.FromSeconds(1);
            o.CollectionAge = CollectionAge;
        });
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureAdditional(MeshBuilder builder) =>
        builder.ConfigureHub(config => config.WithServices(services =>
        {
            var real = services.Last(d => d.ServiceType == typeof(IMeshNodeHubFactory));
            services.Remove(real);
            services.AddSingleton<ActivationHolds>();
            services.AddSingleton<IMeshNodeHubFactory>(sp => new HoldingMeshNodeHubFactory(
                (IMeshNodeHubFactory)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!),
                sp.GetRequiredService<ActivationHolds>()));
            return services;
        }));
}
