using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>A hosted hub's lifetime scope must be closed when that hub is disposed.</b>
///
/// <para><c>MessageHubConfiguration.CreateServiceProvider</c> builds a provider per hub —
/// <c>ConfigureServices(parent).SetupModules(...)</c>, which on Autofac is
/// <c>BeginLifetimeScope</c> for a hosted hub and a fresh root container for a root hub. Nothing
/// closed it: the call was present in <c>HandleShutdownCore</c>'s ShutDown phase and
/// <b>commented out</b>.</para>
///
/// <para><b>Where the close lives, and why not in the hub.</b> It is
/// <c>HostedHubsCollection.CloseScopeWhenDisposed</c>, on the parent side, subscribed to the child's
/// <c>DisposalCompleted</c>. The hub is a singleton IN the scope it would otherwise be destroying,
/// so closing it from its own ShutDown phase pulls its logger and the rest of that method out from
/// under it; and the collection is the only place that also sees a hub disposed ON ITS OWN — the
/// recycle, which is the case that actually leaks.</para>
///
/// <para><b>What that costs, in two ways that look unrelated.</b></para>
/// <list type="number">
///   <item>Every <see cref="IDisposable"/> singleton in the hub's container outlives the hub —
///   Roslyn load contexts, Npgsql connections, file handles, native buffers. Their finalizers run
///   later, against a graph already torn down, which is the classic route to a <b>SIGSEGV at
///   process exit</b>. MeshWeaver.Reinsurance#98 is exactly that shape: the gate exits <b>139</b>,
///   and whichever NodeType happened to be mid-compile is reported as "never came live".</item>
///   <item>An Autofac parent scope <b>tracks every child scope it creates</b> until the parent
///   itself is disposed. A portal that activates and recycles hubs for hours therefore accumulates
///   one undisposed scope per hub, with everything each one holds — which is why a disposable-mesh
///   e2e runner reaches ~7 GB of 7 GB and starts taking 20-second GC pauses.</item>
/// </list>
///
/// <para><b>Why this test and not a memory assertion.</b> A leak is awkward to assert on directly —
/// GC timing, finalizer queues, and the runner's own pressure all confound it. Ownership is not:
/// if the hub owns the container, a disposable registered in that container must be disposed when
/// the hub is. That is a single, deterministic fact, and it was false.</para>
/// </summary>
public class HubDisposesItsServiceProviderTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly Address ScopeOwnerAddress = new("dispose-container", "1");

    /// <summary>Tracks its own disposal. Registered as a singleton so it lives in — and only in —
    /// the hub's own container.</summary>
    private sealed class TracksItsDisposal : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    /// <summary>
    /// The contract: dispose the hub, and the disposable singleton in its container is disposed.
    ///
    /// <para>Before the fix this failed with <c>IsDisposed == false</c> — the hub was fully torn
    /// down (RunLevel Dead, disposal completed) while its entire container, and everything in it,
    /// stayed alive.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task DisposingTheHub_DisposesTheSingletonsInItsOwnContainer()
    {
        var host = GetHost();

        // 🚨 Registered by TYPE, not as a pre-built instance. `AddSingleton(instance)` hands the
        // container an object it did not create, and both MS DI and Autofac deliberately never
        // dispose those — the caller owns what the caller built. A test written that way can never
        // pass, however correct the fix, and reads as "the fix does not work". The container must
        // CONSTRUCT it to own it, which is also the shape that actually leaks in production.
        var hub = host.GetHostedHub(ScopeOwnerAddress,
            c => c.WithServices(services => services.AddSingleton<TracksItsDisposal>()));

        // Resolve it, so the container is genuinely holding an instance rather than merely a
        // registration it never realised — an unrealised singleton would be disposed by nobody
        // either way, and would make this test pass for the wrong reason.
        var tracker = hub.ServiceProvider.GetRequiredService<TracksItsDisposal>();
        Assert.False(tracker.IsDisposed, "precondition: the singleton is alive while the hub is");

        hub.Dispose();
        await hub.DisposalCompleted.FirstOrDefaultAsync().Await().WaitAsync(TimeSpan.FromSeconds(120));

        Assert.True(tracker.IsDisposed,
            "the hub owns its container, so disposing the hub must dispose what the container holds — "
            + "otherwise every recycled hub leaks its singletons and their finalizers run against a "
            + "torn-down graph");
    }

    /// <summary>
    /// The same contract reached the OTHER way: nobody disposes the child, the PARENT goes down and
    /// takes it with it.
    ///
    /// <para>Worth pinning separately because the two routes run different code. The recycle above
    /// arrives at <c>CloseScopeWhenDisposed</c> through the child's own <c>Dispose()</c>; this one
    /// arrives through the parent's <c>DisposeHostedHubs</c> phase and its
    /// <c>DisposeHubsReactive</c> join. A close wired onto only one of them would leave the other
    /// leaking exactly as before, and the leak is invisible at both call sites.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task DisposingTheParent_ClosesAHostedHubsScopeToo()
    {
        var host = GetHost();
        var hub = host.GetHostedHub(new Address("dispose-container-parent-teardown", "1"),
            c => c.WithServices(services => services.AddSingleton<TracksItsDisposal>()));
        var tracker = hub.ServiceProvider.GetRequiredService<TracksItsDisposal>();
        Assert.False(tracker.IsDisposed, "precondition: the singleton is alive while the parent is");

        host.Dispose();
        await host.DisposalCompleted.FirstOrDefaultAsync().Await().WaitAsync(TimeSpan.FromSeconds(120));

        Assert.True(tracker.IsDisposed,
            "a parent's teardown must close the scopes it opened for its children, not merely dispose "
            + "the child hubs and leave their containers tracked on itself");
    }
}
