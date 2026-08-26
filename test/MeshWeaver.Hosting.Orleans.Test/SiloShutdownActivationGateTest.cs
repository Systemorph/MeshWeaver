using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The invariant, pinned at the seam: <b>a silo that has begun shutting down does not ask Orleans to
/// create a grain activation</b> — not for a routed message, not for a "goodbye" announcement, not
/// for anything.
///
/// <para><b>Orleans already enforces the rule; it is the WINDOW that was ours.</b>
/// <c>Catalog.GetOrCreateActivation</c> (Orleans 10.2.2) creates an activation only while
/// <c>_siloStatusOracle.CurrentStatus == SiloStatus.Active</c> — otherwise it takes the
/// <c>UnableToCreateActivation</c> path and returns null — and <c>PlacementService.GetCompatibleSilos</c>
/// intersects candidates with the ACTIVE silo set. So nothing here is a parallel gate. What Orleans
/// cannot know is that we have ALREADY decided to go away:
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/> fires strictly BEFORE the Orleans silo
/// hosted service stops (that ordering is exactly why <c>DeliverMessage</c> can use it as a readiness
/// signal), so for some seconds the membership oracle still reports <c>Active</c> and Orleans will
/// faithfully create any activation we ask for — on the silo that is leaving. <b>Asking is ours to
/// stop; refusing is Orleans'.</b></para>
///
/// <para><b>The measured instance is the pod-hub claim/release pair.</b>
/// <c>OrleansRoutingService.AttachPodHub</c> reached <c>IGrainFactory</c> directly, three lines away
/// from the shutdown gate <c>DeliverMessage</c> already applied. The release leg is the "goodbye
/// announcement" in its purest form: <see cref="IPodHubGrain"/> is <c>[PreferLocalPlacement]</c>, so
/// <c>Detach()</c> against an activation that has already gone does not release anything — it CREATES
/// a fresh activation on the dying silo in order to tell it nothing. PR #2252 is the same defect one
/// level up (an announcement escaping through a healthy ancestor re-activated a grain that was
/// mid-deactivation, so it never left the silo catalog and the straggler test timed out).</para>
///
/// <para><b>Both directions are asserted.</b> A gate that refuses everything would pass a one-sided
/// test and break the portal, so every "does not reach Orleans" case has a running-host control that
/// proves the very same call still does.</para>
///
/// <para><b>No cluster, no mocks, no timing.</b> The stopping signal is a REAL
/// <see cref="IHostApplicationLifetime"/> cancelled through the same
/// <see cref="IHostApplicationLifetime.StopApplication"/> the runtime calls on SIGTERM, and
/// <see cref="RecordingGrainFactory"/> is a RECORDER rather than a mock: the assertion is over what
/// the router actually asked Orleans for. Same vehicle as
/// <see cref="OrleansRoutingShutdownClassificationTest"/>, which pins the classification half.</para>
/// </summary>
public class SiloShutdownActivationGateTest : TestBase
{
    private static readonly Address SenderAddress = new("portal", "silo-shutdown-sender");
    private static readonly Address TargetAddress = new("SomeNamespace", "SomeNode");

    // RunContinuationsAsynchronously: without it everything awaiting this resumes INLINE on the
    // hub's message-handling thread, so the awaiting test body would run on the single-threaded
    // action block it is still driving. Same reason as OrleansRoutingShutdownClassificationTest.
    private readonly TaskCompletionSource<DeliveryFailure> nack =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly IHostApplicationLifetime lifetime;
    private readonly OrleansStreamingReadiness readiness = new();

    public SiloShutdownActivationGateTest(ITestOutputHelper output) : base(output)
    {
        // A real host purely for its real ApplicationLifetime — the concrete type the runtime
        // cancels on shutdown. Nothing is started; StopApplication fires ApplicationStopping
        // regardless, and that token is the signal under test.
        lifetime = new HostBuilder().Build().Services.GetRequiredService<IHostApplicationLifetime>();
        Services.AddSingleton(lifetime);
        Services.AddSingleton(readiness);
        Services.AddSingleton<AccessService>();
        // The hub sits AT the sender address, so the DeliveryFailure the router posts back to
        // delivery.Sender lands on this handler — the real NACK, not a stand-in.
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(SenderAddress, conf => conf
            .WithHandler<DeliveryFailure>((_, d) => { nack.TrySetResult(d.Message); return d.Processed(); })
            .WithPostingIdentity(PostingIdentity.System)));
    }

    private OrleansRoutingService CreateRouter(RecordingGrainFactory factory) =>
        new(factory, ServiceProvider, ServiceProvider.GetRequiredService<ILogger<OrleansRoutingService>>());

    private static IMessageDelivery NewDelivery() =>
        new MessageDelivery<string>(SenderAddress, TargetAddress, "payload", JsonSerializerOptions.Default);

    private static IObservable<IMessageDelivery> Ignore(IMessageDelivery d, CancellationToken _) =>
        Observable.Return(d);

    /// <summary>
    /// THE INVARIANT, on the leg the maintainer named: a "goodbye" announcement must not create the
    /// activation it is saying goodbye to.
    ///
    /// <para>The claim is made while the host is healthy (so there is a real registration to
    /// release), the host then begins stopping, and the registration is disposed — the ordinary
    /// shutdown sequence, since every hub's stream registration is disposed as the process winds
    /// down. Pre-fix the disposal called <c>GetGrain&lt;IPodHubGrain&gt;(addr).Detach()</c>
    /// unconditionally; with <c>[PreferLocalPlacement]</c> and the oracle still reporting Active,
    /// that is a brand-new activation on the silo that is leaving.</para>
    /// </summary>
    [Fact]
    public void HostStopping_TheGoodbyeAnnouncement_NeverAsksOrleansForAnActivation()
    {
        var factory = new RecordingGrainFactory();
        var routing = CreateRouter(factory);

        var registration = routing.RegisterStream(SenderAddress, Ignore);
        factory.Requests.Should().NotBeEmpty("the claim must be made while the host is healthy");

        lifetime.StopApplication();
        factory.Clear();

        registration.Dispose();

        // 🚨 The whole invariant in one assertion. Nothing was released — there was nothing left to
        // release — and nothing was created in order to try.
        factory.Requests.Should().BeEmpty(
            "a silo that has begun shutting down must not ask Orleans for ANY activation, and a "
            + "PreferLocalPlacement Detach() against a gone activation creates one on the dying silo "
            + "purely to announce to it");
    }

    /// <summary>
    /// The control for the guard above, and the half that a one-sided gate would break: on a HEALTHY
    /// host the very same disposal still releases the claim, so a hub that MOVES between pods leaves
    /// no activation stranded on the pod it left.
    /// </summary>
    [Fact]
    public void HostRunning_TheGoodbyeAnnouncement_StillReachesOrleans()
    {
        var factory = new RecordingGrainFactory();
        var routing = CreateRouter(factory);

        var registration = routing.RegisterStream(SenderAddress, Ignore);
        factory.Clear();

        registration.Dispose();

        factory.Requests.Should().Contain(r => r.Interface == typeof(IPodHubGrain),
            "while the host is running the claim must still be released — otherwise an address that "
            + "moves silos leaves its activation stranded on the pod it left");
    }

    /// <summary>
    /// The claim leg of the same pair. Claiming an address for a process that is going away is
    /// meaningless work whose only effect is to place an activation on the silo that is leaving —
    /// and it carries a bounded RETRY, so pre-fix a claim still bouncing between pods when shutdown
    /// began spent its remaining attempts doing exactly that.
    /// </summary>
    [Fact]
    public void HostStopping_ThePodHubClaim_NeverAsksOrleansForAnActivation()
    {
        var factory = new RecordingGrainFactory();
        var routing = CreateRouter(factory);

        lifetime.StopApplication();

        using var registration = routing.RegisterStream(SenderAddress, Ignore);

        factory.Requests.Should().BeEmpty(
            "claiming an address for a process that is shutting down only places an activation on "
            + "the silo that is leaving");
    }

    /// <summary>
    /// The control for the claim: a healthy silo still activates normally. This is the assertion that
    /// stops the gate from being widened into "this process no longer talks to Orleans" — the claim
    /// is what makes a hub reachable by directed grain call at all (#1742).
    /// </summary>
    [Fact]
    public void HostRunning_ThePodHubClaim_StillReachesOrleans()
    {
        var factory = new RecordingGrainFactory();
        var routing = CreateRouter(factory);

        using var registration = routing.RegisterStream(SenderAddress, Ignore);

        factory.Requests.Should().Contain(
            r => r.Interface == typeof(IPodHubGrain) && r.Key == SenderAddress.ToString(),
            "a healthy silo must still claim its addresses — a gate that refuses everything would "
            + "pass the shutdown assertions above and break the portal");
    }

    /// <summary>
    /// The refusal must be TRANSIENT, and it must stay transient when the stopping token flips LATE —
    /// after <c>DeliverMessage</c>'s own gate has already let the delivery through. A terminal
    /// verdict is what tears down <c>SynchronizationStream</c>'s keep-alive + resubscribe latch and
    /// <c>JsonSynchronizationStream</c>, so a routine pod roll would NACK every live subscription as
    /// permanently failed (CI 30003419841).
    ///
    /// <para><b>The window is opened deterministically, not raced.</b> An outbound dispatch is held
    /// while the SENDER's own inbound attach is still pending (#1081), and that attach is ordered on
    /// <see cref="OrleansStreamingReadiness"/>. Registering the readiness signal without ever firing
    /// it therefore parks the dispatch exactly between <c>DeliverMessage</c>'s gate and the seam;
    /// stopping the host and then firing the signal runs the seam with the token already cancelled.
    /// No sleeps, no polling.</para>
    /// </summary>
    [Fact]
    public async Task ShutdownFlippingAfterTheGate_IsRefusedAsTransient_NotTerminal()
    {
        var factory = new RecordingGrainFactory();
        var routing = CreateRouter(factory);

        // Sender registered => its inbound attach is pending on the (unfired) readiness signal, so
        // the outbound dispatch below is held rather than dispatched inline.
        using var registration = routing.RegisterStream(SenderAddress, Ignore);
        factory.Clear();

        // Passes DeliverMessage's gate — the host is still running at this point.
        await routing.DeliverMessage(NewDelivery()).FirstAsync().ToTask();

        lifetime.StopApplication();
        // Release the held dispatch. The seam now runs with the host stopping.
        await ((ILifecycleObserver)readiness).OnStart(CancellationToken.None);

        var failure = await nack.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A reject caused by THIS process going away is transient: consumers with recovery machinery
        // ride ShuttingDown out and tear down on the terminal Failed.
        failure.ErrorType.Should().Be(ErrorType.ShuttingDown);
        // ...and the refusal happened WITHOUT asking Orleans to place the routing grain. Not asking
        // is also what removes Orleans' own `Orleans.Messaging[100071] Failed to address message`
        // error per attempt — that log exists because WE asked for an unplaceable grain.
        factory.Requests.Should().NotContain(r => r.Interface == typeof(IRoutingGrain),
            "the seam must refuse before placement, never discover the refusal as an exception");
    }

    /// <summary>
    /// Records what the router asked Orleans for. A recorder, not a mock: the assertions above are
    /// over the ACTUAL grain requests the production code made. Only the string-key overload is
    /// implemented because it is the only one the mesh uses — every other member throws, so a new
    /// call shape fails loudly here instead of passing silently.
    /// </summary>
    private sealed class RecordingGrainFactory : IGrainFactory
    {
        private readonly ConcurrentQueue<GrainRequest> requests = new();

        public IReadOnlyList<GrainRequest> Requests => requests.ToArray();

        public void Clear() => requests.Clear();

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            requests.Enqueue(new GrainRequest(typeof(TGrainInterface), primaryKey));
            if (typeof(TGrainInterface) == typeof(IPodHubGrain))
                return (TGrainInterface)(object)new StubPodHubGrain();
            if (typeof(TGrainInterface) == typeof(IRoutingGrain))
                return (TGrainInterface)(object)new StubRoutingGrain();
            throw new NotSupportedException($"Unexpected grain interface {typeof(TGrainInterface)}");
        }

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();

        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();

        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();

        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId)
            where TGrainInterface : IAddressable => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
    }

    /// <summary>One grain reference the router asked Orleans for.</summary>
    private sealed record GrainRequest(Type Interface, string Key);

    private sealed class StubPodHubGrain : IPodHubGrain
    {
        public Task<bool> Attach() => Task.FromResult(true);
        public Task Detach() => Task.CompletedTask;
        public Task<IMessageDelivery> Deliver(IMessageDelivery delivery) => Task.FromResult(delivery);
    }

    private sealed class StubRoutingGrain : IRoutingGrain
    {
        public Task<IMessageDelivery> RouteMessage(IMessageDelivery delivery) => Task.FromResult(delivery);
    }
}
