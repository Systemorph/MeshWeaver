using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Issue #5731, end to end: a delivery to an ephemeral compile-activity hub
/// (<c>{owner}/_Activity/compile-{id}</c>) whose message EXPIRES while Orleans is still placing the
/// target grain must reach its caller as ONE transient <see cref="ErrorType.ShuttingDown"/>
/// <see cref="DeliveryFailure"/> - no hang, no unhandled error, no silent loss, no second attempt.
///
/// <para><b>The rig.</b> An Orleans outgoing grain-call filter on the silo, armed for one test's
/// grain keys, answers <c>IMessageHubGrain.DeliverMessage</c> by throwing Orleans' own
/// <see cref="OperationCanceledException"/> from an async completion - a CANCELLED task that stores
/// the exception, the shape PlacementService hands the caller. The call never leaves the caller, so
/// no activation exists, exactly as in production. Everything else is real: the issuing hub's
/// <c>Observe</c>, the silo's routing service, <see cref="RoutingGrain"/>'s route, its classifier
/// and its NACK back to the sender.</para>
///
/// <para><b>Negative controls.</b> Any other cancellation through the same rig stays terminal
/// (<see cref="ErrorType.Failed"/>); and the pure fact at the bottom shows the pre-fix task bridge
/// loses Orleans' sentence, so without the exception preservation and the classifier arm the first
/// test's ShuttingDown assertion fails.</para>
/// </summary>
public class PlacementExpiryThroughTheRouteTest(ITestOutputHelper output) : OrleansMeshTestBase(output)
{
    /// <inheritdoc />
    protected override Type SiloConfiguratorType => typeof(PlacementExpirySiloConfigurator);

    private const string Marker = "Message expired before placement could complete";

    private IMessageHub SiloMesh =>
        ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services.GetRequiredService<IMessageHub>();

    [Fact(Timeout = 120_000)]
    public async Task APlacementExpiry_OnTheWayToACompileActivity_ReachesTheCallerOnce_AsShuttingDown()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(110));

        var outcome = await DeliverToACompileActivityUnder(
            target => $"{Marker} for grain {target}.", deadline.Token);

        outcome.Kind.Should().Be(NotificationKind.OnError,
            "a delivery that died in placement must be REPORTED to its caller - completing or answering "
            + "it would be a silent loss");
        var failure = FindDeliveryFailure(outcome.Error);
        Assert.True(failure is not null,
            $"the caller must see a DeliveryFailure, not an unhandled error; got {outcome.Error}");
        failure!.Failure.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "the message reached no activation - a statement about the cluster, not the target - and "
            + "consumers that ride out ShuttingDown tear down on Failed (#5731)");
        (failure.Failure.Message ?? failure.Message).Contains(Marker, StringComparison.OrdinalIgnoreCase)
            .Should().BeTrue("the caller is told what happened, not 'A task was canceled.'");
        outcome.Attempts.Should().Be(1,
            "classification only: a re-send under a persistent placement stall would hold a "
            + "routing-pool slot for a full time-to-live per attempt");
        Assert.All(outcome.Targets, t => Assert.Contains("/_Activity/compile-", t));
    }

    [Fact(Timeout = 120_000)]
    public async Task AnyOtherCancellation_OnTheSameRoute_StaysTerminal()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(110));

        var outcome = await DeliverToACompileActivityUnder(_ => "The operation was canceled.", deadline.Token);

        outcome.Kind.Should().Be(NotificationKind.OnError);
        var failure = FindDeliveryFailure(outcome.Error);
        Assert.True(failure is not null,
            $"the caller must see a DeliveryFailure, not an unhandled error; got {outcome.Error}");
        failure!.Failure.ErrorType.Should().Be(ErrorType.Failed,
            "only Orleans' placement-expiry sentence is demoted to transient; anything else stays terminal");
        outcome.Attempts.Should().Be(1);
    }

    /// <summary>
    /// The negative control of the exception preservation: the pre-fix bridge
    /// (<c>Observable.Defer(() =&gt; call().ToObservable())</c>) replaces the cancelled task's stored
    /// exception with a message-less TaskCanceledException, so the classifier answers Failed; the
    /// route's own bridge keeps Orleans' sentence and answers ShuttingDown.
    /// </summary>
    [Fact]
    public async Task WithoutThePreservation_TheSentenceIsLost_AndTheVerdictIsTerminal()
    {
        var ct = TestContext.Current.CancellationToken;

        Exception? viaOldBridge = null;
        try
        {
            await Observable.Defer(() => ExpiredBeforePlacement().ToObservable()).Await(ct);
        }
        catch (Exception e)
        {
            viaOldBridge = e;
        }

        Assert.NotNull(viaOldBridge);
        RoutingGrain.IsPlacementExpired(viaOldBridge!).Should().BeFalse(
            "Rx's task bridge drops the stored exception - this is what the fix repairs");
        RoutingGrain.ClassifyDeliveryException(viaOldBridge!).Should().Be(ErrorType.Failed);

        Exception? viaRoute = null;
        try
        {
            await RoutingGrain.DeliverToGrainObservable(
                    grainCall: ExpiredBeforePlacement,
                    grainKey: "messagehub/placement-expiry/x/_Activity/compile-x",
                    deliveryId: "placement-expiry-control",
                    logger: Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                    backoff: _ => TimeSpan.Zero,
                    scheduler: System.Reactive.Concurrency.Scheduler.Immediate)
                .Await(ct);
        }
        catch (Exception e)
        {
            viaRoute = e;
        }

        Assert.NotNull(viaRoute);
        RoutingGrain.IsPlacementExpired(viaRoute!).Should().BeTrue();
        RoutingGrain.ClassifyDeliveryException(viaRoute!).Should().Be(ErrorType.ShuttingDown);
    }

    private static async Task<IMessageDelivery> ExpiredBeforePlacement()
    {
        await Task.Yield();
        throw new OperationCanceledException(
            $"{Marker} for grain messagehub/placement-expiry/x/_Activity/compile-x.");
    }

    private sealed record Outcome(NotificationKind Kind, Exception? Error, int Attempts, string[] Targets);

    /// <summary>
    /// Seeds an owner node and its compile activity, arms the expiry for this test's keys only
    /// (AFTER the seeding, whose own deliveries must go through), then issues a request to the
    /// activity and returns the caller's whole first notification plus the attempts the rig saw.
    /// </summary>
    private async Task<Outcome> DeliverToACompileActivityUnder(
        Func<string, string> cancellationText, CancellationToken ct)
    {
        var access = SiloMesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = SiloMesh.ServiceProvider.GetRequiredService<IMeshService>();
        var issuing = SiloMesh.NodeOperationIssuingHub();
        var injector = Cluster.SiloServices().GetRequiredService<PlacementExpiryInjector>();

        var id = Guid.NewGuid().ToString("N");
        var owner = $"placement-expiry/{id}";
        var activity = $"{owner}/_Activity/compile-{id}";

        await access.RunAsSystem(() => meshService.CreateNode(MeshNode.FromPath(owner) with
        {
            Name = "The owner of a compile activity",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        })).FirstAsync().Await(ct);
        await access.RunAsSystem(() => meshService.CreateNode(MeshNode.FromPath(activity) with
        {
            Name = "An ephemeral compile activity",
            NodeType = "Activity",
            MainNode = owner,
            State = MeshNodeState.Active,
        })).FirstAsync().Await(ct);

        using var armed = injector.Arm(id, cancellationText);

        var outcome = access.RunAsSystem(() => issuing.Observe(
                new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(activity))))
            .Take(1)
            .Materialize()
            .Replay();
        using var connected = outcome.Connect();

        var first = await outcome.Should().Within(TestTimeouts.Convergence).Emit(
            "the caller of a delivery that expired in placement must hear about it - a hang here is "
            + "the silent loss #5731 asks about", ct);
        var attempts = injector.Attempts;

        await injector.Attempted.Skip(1).Should().NotEmit(TimeSpan.FromSeconds(3),
            "the route answers the sender once and does not forward the delivery again", ct);

        Output.WriteLine($"caller saw {first.Kind}: {first.Exception?.Message}; attempts {attempts}; "
            + $"targets {string.Join(", ", injector.Targets)}");
        return new Outcome(first.Kind, first.Exception, attempts, injector.Targets);
    }

    private static DeliveryFailureException? FindDeliveryFailure(Exception? error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is DeliveryFailureException failure)
                return failure;
            if (e is AggregateException aggregate)
                return aggregate.Flatten().InnerExceptions.Select(FindDeliveryFailure)
                    .FirstOrDefault(f => f is not null);
        }
        return null;
    }
}

/// <summary>
/// The silo-side switch of <see cref="PlacementExpiryThroughTheRouteTest"/>: while armed, every
/// <c>DeliverMessage</c> to a grain whose key carries the armed fragment is failed the way Orleans'
/// PlacementService fails an expired message, and counted. Instance state on a silo singleton -
/// never static - so it dies with the cluster.
/// </summary>
public sealed class PlacementExpiryInjector
{
    private sealed record Arming(string Fragment, Func<string, string> Text);

    private Arming? arming;
    private int attempts;
    private readonly ReplaySubject<string> attempted = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> targets = new();

    /// <summary>The intercepted deliveries so far.</summary>
    public int Attempts => Volatile.Read(ref attempts);

    /// <summary>Emits the target key of every intercepted delivery.</summary>
    public IObservable<string> Attempted => attempted;

    /// <summary>The target keys of the intercepted deliveries.</summary>
    public string[] Targets => targets.ToArray();

    /// <summary>Arms the expiry for grain keys containing <paramref name="fragment"/>.</summary>
    /// <param name="fragment">A fragment unique to one test's grain keys.</param>
    /// <param name="text">The cancellation's message for a target key.</param>
    /// <returns>The disarm.</returns>
    public IDisposable Arm(string fragment, Func<string, string> text)
    {
        var armedNow = new Arming(fragment, text);
        Volatile.Write(ref arming, armedNow);
        return Disposable.Create(() => Interlocked.CompareExchange(ref arming, null, armedNow));
    }

    internal Exception? ExpiryFor(string targetId, string methodName)
    {
        var current = Volatile.Read(ref arming);
        if (current is null
            || methodName != "DeliverMessage"
            || !targetId.Contains(current.Fragment, StringComparison.Ordinal))
            return null;

        Interlocked.Increment(ref attempts);
        targets.Enqueue(targetId);
        attempted.OnNext(targetId);
        return new OperationCanceledException(current.Text(targetId));
    }
}

/// <summary>
/// Fails an armed delivery from an ASYNC completion, so the caller gets a cancelled task that
/// stores the exception - the shape Orleans hands it when a message expires before placement.
/// </summary>
internal sealed class PlacementExpiryFilter(PlacementExpiryInjector injector) : IOutgoingGrainCallFilter
{
    /// <inheritdoc />
    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        if (injector.ExpiryFor(context.TargetId.ToString(), context.InterfaceMethod?.Name ?? string.Empty)
            is { } expiry)
        {
            await Task.Yield();
            throw expiry;
        }

        await context.Invoke();
    }
}

/// <summary>The stock silo with <see cref="PlacementExpiryFilter"/> on its outgoing grain calls.</summary>
public class PlacementExpirySiloConfigurator : SharedSiloConfigurator, ISiloConfigurator
{
    /// <inheritdoc />
    void ISiloConfigurator.Configure(ISiloBuilder siloBuilder)
    {
        Configure(siloBuilder);
        siloBuilder.Services.AddSingleton<PlacementExpiryInjector>();
        siloBuilder.Services.AddSingleton<IOutgoingGrainCallFilter, PlacementExpiryFilter>();
    }
}
