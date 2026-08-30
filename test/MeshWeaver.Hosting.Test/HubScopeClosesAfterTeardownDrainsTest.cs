using System;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>Queues and pools drain LAST; a hosted hub's lifetime scope closes AFTER them, never
/// before.</b>
///
/// <para><c>HostedHubsCollection.CloseScopeWhenDisposed</c> closes the Autofac scope a hosted hub owns
/// once that hub's <c>DisposalCompleted</c> fires. That signal covers the action block and the
/// message round-trips only. The offloaded work the hub issued — <c>IIoPool</c> leaves, synced-query
/// pipelines on the pool scheduler, async cleanup on the <c>AsyncDisposeQueue</c> — is joined by the
/// mesh's drain phases strictly AFTER every hub has signalled. Closing the scope on the hub's own
/// signal during a whole-mesh teardown therefore closed it UNDER that work: every CI teardown capture
/// is a list of <c>ObjectDisposedException: … LifetimeScope … has already been disposed</c> thrown
/// from exactly those leaves (<c>PermissionEvaluator.GetEffectivePermissions</c>,
/// <c>MeshNodeStreamCache.GetQueryRaw</c>, <c>IoPool.InvokeCore</c>, …), and the one that escapes
/// onto a bare scheduler thread is the anonymous "Catastrophic failure" that reds a green shard with
/// zero failed tests (MeshWeaver.Plugins#870, recurring).</para>
///
/// <para>The fix hands the close to <see cref="IHubScopeDisposalSequencer"/>, which the mesh
/// implements as <see cref="TeardownOrderedScopeDisposal"/>: on a LIVE mesh a recycled hub's scope
/// still closes immediately (an Autofac parent tracks every child scope until it is closed — the
/// leak the close was introduced for); while the mesh is TEARING DOWN the close waits for
/// <see cref="MeshTeardownSignal.Completed"/>, which fires after the last drain phase. These two
/// facts are pinned separately because they run different branches, and a sequencer that deferred
/// the recycle too would reintroduce the leak silently.</para>
/// </summary>
public class HubScopeClosesAfterTeardownDrainsTest : HubTestBase
{
    /// <summary>Tracks its own disposal; registered by TYPE so the hub's container constructs — and
    /// therefore owns and disposes — it.</summary>
    private sealed class TracksItsDisposal : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    public HubScopeClosesAfterTeardownDrainsTest(ITestOutputHelper output) : base(output)
    {
        // The mesh's teardown services — the IoPoolRegistry / AsyncDisposeQueue / MeshTeardownSignal
        // trio AND the sequencer under test — on the ROOT container, exactly where MeshBuilder puts
        // them. The mesh hub is that container's IMessageHub (HubTestBase registers it so), which is
        // what the sequencer reads IsDisposing off.
        Services.AddIoPools();
    }

    private IMessageHub HostWithTrackedSingleton(string id) =>
        Mesh.GetHostedHub(new Address("scope-order", id),
            c => c.WithServices(services => services.AddSingleton<TracksItsDisposal>()));

    /// <summary>
    /// Whole-mesh teardown: the child has signalled <c>DisposalCompleted</c>, the MESH has signalled
    /// <c>DisposalCompleted</c> — and the child's scope is STILL OPEN, because the drains have not
    /// run. It closes on the terminal teardown signal and not a moment earlier.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task DuringMeshTeardown_TheChildScopeClosesOnTheTeardownSignal_NotOnDisposalCompleted()
    {
        var hub = HostWithTrackedSingleton("teardown");
        var tracker = hub.ServiceProvider.GetRequiredService<TracksItsDisposal>();
        tracker.IsDisposed.Should().BeFalse("precondition: the singleton is alive while the hub is");

        Mesh.Dispose();
        await Mesh.DisposalCompleted.ObserveCompletion(
            ex => Output.WriteLine($"late disposal fault: {ex}"),
            TestContext.Current.CancellationToken);
        hub.RunLevel.Should().Be(MessageHubRunLevel.Dead,
            "the mesh's DisposeHostedHubs phase joins every child's disposal before it completes");

        tracker.IsDisposed.Should().BeFalse(
            "the mesh is tearing down and no drain has run yet — a scope closed here is closed under "
            + "the pooled leaves and query pipelines that still resolve from it (the "
            + "ObjectDisposedException straggler class, and Plugins#870 when one escapes)");

        // The drains' terminal report — what MeshTeardownExtensions.DrainAsync / the test base fire
        // AFTER IoPoolRegistry.DrainAll() and the AsyncDisposeQueue quiesce.
        ServiceProvider.GetRequiredService<MeshTeardownSignal>()
            .SignalCompleted(new TeardownReport(LeakedIoLeaves: 0, AsyncDisposeClean: true));

        tracker.IsDisposed.Should().BeTrue(
            "once every drain phase is accounted for, the scope must close — deferring is an "
            + "ordering, never a leak");
    }

    /// <summary>
    /// A recycle on a LIVE mesh must not be deferred to a signal only a teardown fires: the scope
    /// closes as soon as the recycled hub has terminated, exactly as before the sequencer existed.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task OnALiveMesh_ARecycledHubsScopeClosesOnItsOwnDisposalCompleted()
    {
        var hub = HostWithTrackedSingleton("recycle");
        var tracker = hub.ServiceProvider.GetRequiredService<TracksItsDisposal>();
        tracker.IsDisposed.Should().BeFalse("precondition: the singleton is alive while the hub is");

        hub.Dispose();
        await hub.DisposalCompleted.ObserveCompletion(
            ex => Output.WriteLine($"late disposal fault: {ex}"),
            TestContext.Current.CancellationToken);

        tracker.IsDisposed.Should().BeTrue(
            "the mesh is live, so nothing will ever fire the teardown signal — a recycle that waited "
            + "for it would leak its scope for the life of the process, which is the leak the close "
            + "was introduced to stop");
    }
}
