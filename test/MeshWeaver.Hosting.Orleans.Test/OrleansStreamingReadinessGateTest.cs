using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins the #1129 and #3983/#3984 root fixes: <c>OrleansRoutingService.RegisterStream</c> must NOT
/// touch either the Orleans stream provider or the pod-hub grain before the Orleans lifecycle
/// reports the silo usable
/// (<see cref="OrleansStreamingReadiness"/>, completed at <c>ServiceLifecycleStage.Active</c>).
/// <c>GetStream</c> on a <c>PersistentStreamProvider</c> whose lifecycle Init has not run yet
/// NREs from deep inside Orleans — the eagerly-created cache/mesh hubs lost exactly that race
/// on every memex pod boot (2 Error-level NREs per boot), papered over by a poll-retry loop.
/// #3983/#3984 were the same invalid early <c>IPodHubGrain.Attach</c> calls seen through Orleans'
/// per-attempt logger and Polly's exhausted-call logger: no active silo advertised the grain type
/// yet. Deterministic unit test, no cluster: both dependencies are recording probes — REACHING one
/// is the observable, so both directions of the gate are asserted without polling or sleeping.
/// </summary>
public class OrleansStreamingReadinessGateTest
{
    private sealed class RecordingStreamProvider : IStreamProvider
    {
        private readonly ISubject<Unit> touches =
            Subject.Synchronize(new ReplaySubject<Unit>(bufferSize: 1));
        private int getStreamCalls;
        public int GetStreamCalls => Volatile.Read(ref getStreamCalls);
        public IObservable<Unit> Touches => touches.AsObservable();
        public string Name => StreamProviders.Memory;
        public bool IsRewindable => false;

        public IAsyncStream<T> GetStream<T>(StreamId streamId)
        {
            Interlocked.Increment(ref getStreamCalls);
            touches.OnNext(Unit.Default);
            // Any touch BEFORE readiness would have been the #1129 NRE; the gate must make this
            // unreachable until the lifecycle observer has run. The throw keeps the fake minimal —
            // the attach-success path is covered by the real-cluster routing tests.
            throw new NotSupportedException("attach probe — reaching the provider is the assertion");
        }
    }

    [Fact]
    public async Task RegisterStream_TouchesOrleans_OnlyAfterLifecycleReportsReady()
    {
        var provider = new RecordingStreamProvider();
        var grainFactory = new RecordingGrainFactory();
        var readiness = new OrleansStreamingReadiness();
        var services = new ServiceCollection();
        services.AddSingleton(readiness);
        services.AddKeyedSingleton<IStreamProvider>(StreamProviders.Memory, provider);
        await using var sp = services.BuildServiceProvider();

        using var routing = new OrleansRoutingService(
            grainFactory,
            sp,
            NullLogger<OrleansRoutingService>.Instance);

        using var registration = routing.RegisterStream(
            AddressExtensions.CreateMeshAddress("streaming-gate-test"),
            (d, _) => Observable.Return(d));

        // Negative controls: there is no positive "still not ready" signal, so NotEmit is the
        // sanctioned bounded assertion for exactly this direction. Both recorders replay, which
        // means an invalid touch made before the assertion subscribed cannot slip past it.
        await provider.Touches.Should().NotEmit(
            within: TimeSpan.FromMilliseconds(300),
            because: "the stream provider must never be touched before Active — that is #1129's NRE");
        await grainFactory.Requests
            .Where(t => t == typeof(IPodHubGrain))
            .Should().NotEmit(
                within: TimeSpan.FromMilliseconds(300),
                because: "the pod-hub claim must never ask Orleans for placement before Active — "
                    + "that is #3983/#3984's 'Known nodes with grain type: none' burst");

        // Open the gate exactly the way Orleans does: the lifecycle observer's OnStart at Active.
        await ((ILifecycleObserver)readiness).OnStart(CancellationToken.None);

        // Positive controls: after Active both independent Orleans legs must actually run. A
        // one-sided test would let a "fix" disable cross-process routing altogether.
        await provider.Touches.Should().Within(TimeSpan.FromSeconds(10)).Emit(
            "after readiness the stream subscription must attach");
        await grainFactory.Requests
            .Where(t => t == typeof(IPodHubGrain))
            .Should().Within(TimeSpan.FromSeconds(10)).Emit(
                "after readiness the silo must claim the locally registered hub");
        provider.GetStreamCalls.Should().Be(1,
            "after readiness the subscription attaches exactly once — no retry loop");
    }

    /// <summary>
    /// The early-disposal half of the same ordering. A registration that ends before Active never
    /// made a claim, so releasing it must not create a pod-hub activation merely to detach it.
    /// </summary>
    [Fact]
    public async Task RegistrationDisposedBeforeReady_NeverTouchesOrleans()
    {
        var provider = new RecordingStreamProvider();
        var grainFactory = new RecordingGrainFactory();
        var services = new ServiceCollection();
        services.AddSingleton(new OrleansStreamingReadiness());
        services.AddKeyedSingleton<IStreamProvider>(StreamProviders.Memory, provider);
        await using var sp = services.BuildServiceProvider();
        using var routing = new OrleansRoutingService(
            grainFactory, sp, NullLogger<OrleansRoutingService>.Instance);

        var registration = routing.RegisterStream(
            AddressExtensions.CreateMeshAddress("disposed-before-ready"),
            (d, _) => Observable.Return(d));
        registration.Dispose();

        await provider.Touches.Should().NotEmit(
            within: TimeSpan.FromMilliseconds(300),
            because: "a cancelled pre-readiness stream attach must never reach the provider");
        await grainFactory.Requests.Should().NotEmit(
            within: TimeSpan.FromMilliseconds(300),
            because: "a never-made claim has nothing to detach, and Detach would itself ask Orleans "
                + "to place a pod-hub grain before any compatible silo exists");
    }

    /// <summary>
    /// Disposal can land after the claim has reserved its attempt but while the grain factory is
    /// still selecting the target. The release must be handed to the claim caller so <c>Detach</c>
    /// cannot overtake the <c>Attach</c> that was already committed to run.
    /// </summary>
    [Fact]
    public async Task DisposalDuringGrainSelection_AttachesBeforeItDetaches()
    {
        IDisposable? registration = null;
        var provider = new RecordingStreamProvider();
        var readiness = new OrleansStreamingReadiness();
        var grainFactory = new RecordingGrainFactory(() => registration!.Dispose());
        var services = new ServiceCollection();
        services.AddSingleton(readiness);
        services.AddKeyedSingleton<IStreamProvider>(StreamProviders.Memory, provider);
        await using var sp = services.BuildServiceProvider();
        using var routing = new OrleansRoutingService(
            grainFactory, sp, NullLogger<OrleansRoutingService>.Instance);

        registration = routing.RegisterStream(
            AddressExtensions.CreateMeshAddress("disposed-during-grain-selection"),
            (d, _) => Observable.Return(d));

        await ((ILifecycleObserver)readiness).OnStart(CancellationToken.None);

        var operations = await grainFactory.Operations
            .Buffer(2)
            .Where(buffer => buffer.Count == 2)
            .Should().Within(TimeSpan.FromSeconds(10)).Emit(
                "the disposal fired inside the first GetGrain call, so the reserved claim must both "
                + "attach and release without leaking either operation");
        operations.Should().Equal(["attach", "detach"]);
    }

    /// <summary>
    /// Records the exact grain interface requested by the router. Every unused overload throws, so
    /// a new production call shape makes the test fail loudly instead of passing without evidence.
    /// </summary>
    private sealed class RecordingGrainFactory(Action? onFirstPodHubRequest = null) : IGrainFactory
    {
        private readonly ISubject<Type> requests =
            Subject.Synchronize(new ReplaySubject<Type>(bufferSize: 1));
        private readonly ISubject<string> operations =
            Subject.Synchronize(new ReplaySubject<string>(bufferSize: 2));
        private int firstRequest;

        public IObservable<Type> Requests => requests.AsObservable();
        public IObservable<string> Operations => operations.AsObservable();

        public TGrainInterface GetGrain<TGrainInterface>(
            string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            requests.OnNext(typeof(TGrainInterface));
            if (typeof(TGrainInterface) == typeof(IPodHubGrain))
            {
                if (Interlocked.Exchange(ref firstRequest, 1) == 0)
                    onFirstPodHubRequest?.Invoke();
                return (TGrainInterface)(object)new StubPodHubGrain(operations);
            }
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

    private sealed class StubPodHubGrain(ISubject<string> operations) : IPodHubGrain
    {
        public Task<bool> Attach()
        {
            operations.OnNext("attach");
            return Task.FromResult(true);
        }

        public Task Detach()
        {
            operations.OnNext("detach");
            return Task.CompletedTask;
        }
        public Task<IMessageDelivery> Deliver(IMessageDelivery delivery) => Task.FromResult(delivery);
    }
}
