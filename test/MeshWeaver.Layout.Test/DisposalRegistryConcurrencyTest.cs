using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the thread-safety of <see cref="LayoutAreaHost"/>'s area-keyed disposal registry (#6975).
///
/// <para><b>The incident.</b> A re-render of <c>Overview</c> failed with "Collection was modified;
/// enumeration operation may not execute." from <c>List.ForEach</c> inside
/// <c>DisposeExistingAreas</c>. The registry is written from more than one thread by design — a
/// top-level re-render runs on the generator's emission thread (the Layout I/O pool), a nested
/// re-render runs through <c>UpdateArea</c> on the stream's action block, and view code registers from
/// its own subscriptions — and it held one shared mutable <c>List</c> per area. A registration that had
/// resolved an area's list just before a teardown pass detached it then added into the list the pass
/// was enumerating (the incident), or into a list nobody would ever enumerate again (a subscription that
/// is never disposed).</para>
///
/// <para>That exact window — between <c>GetOrAdd</c> returning and <c>Add</c> — has no seam a test can
/// park on. The SAME defect is deterministically reachable on the host's own teardown, which enumerated
/// the LIVE lists: a registration under an area while its list is being disposed, from another thread or
/// from inside a disposable's own teardown. Those are the two failing-first tests here; the third pins
/// the defined semantics for an entry registered during an area pass.</para>
/// </summary>
public class DisposalRegistryConcurrencyTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string ProbeView = nameof(ProbeView);
    private const string RegistryArea = ProbeView + "/issue6975";

    // Instance, never static (NoStaticState.md): the REAL host of this test's render pass.
    private readonly ReplaySubject<LayoutAreaHost> renderedHost = new(1);

    // Held so the area stream (and with it the LayoutAreaHost) stays rooted for the test's duration.
    private ISynchronizationStream<EntityStore>? areaStream;

    private UiControl RenderProbe(LayoutAreaHost host, RenderingContext ctx)
    {
        renderedHost.OnNext(host);
        return Controls.Html("probe");
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(ProbeView, RenderProbe));

    private async Task<LayoutAreaHost> RenderProbeAreaAsync()
    {
        areaStream = GetHost().GetWorkspace().GetStream(new LayoutAreaReference(ProbeView));
        await areaStream!.GetControlStream(ProbeView)
            .Should().Within(10.Seconds()).Match(c => c is not null,
                cancellationToken: TestContext.Current.CancellationToken);
        return await renderedHost.Should().Within(10.Seconds())
            .Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A disposable whose teardown registers another disposable under the SAME area. On the old
    /// registry the host's teardown added into the list it was enumerating and threw "Collection was
    /// modified" — the incident's exception, deterministically, on one thread. Now the registration meets
    /// a disposed host and is disposed at once: nothing throws and nothing leaks.
    /// </summary>
    [HubFact]
    public async Task RegisteringFromATeardown_DuringHostDispose_DisposesTheLateEntryAndDoesNotThrow()
    {
        var host = await RenderProbeAreaAsync();

        var lateDisposals = 0;
        var late = Disposable.Create(() => Interlocked.Increment(ref lateDisposals));
        host.RegisterForDisposal(RegistryArea, Disposable.Create(() => host.RegisterForDisposal(RegistryArea, late)));
        // A second entry under the same area keeps the enumeration going past the re-entrant one.
        host.RegisterForDisposal(RegistryArea, Disposable.Empty);

        var act = () => host.Dispose();

        act.Should().NotThrow(
            "a teardown that registers must never mutate the collection the host's disposal enumerates (#6975)");
        Volatile.Read(ref lateDisposals).Should().Be(1,
            "an entry registered after the host's disposal began is disposed at once — nothing else would ever reap it");
    }

    /// <summary>
    /// The incident's shape across two threads: the host's teardown is parked INSIDE one entry's
    /// <c>Dispose</c> while another thread registers under the same area. On the old registry that
    /// registration went into the very list being enumerated, and the teardown threw when it resumed.
    /// </summary>
    [HubFact]
    public async Task RegisteringFromAnotherThread_WhileTeardownIsParkedMidPass_DoesNotCorruptTheRegistry()
    {
        var host = await RenderProbeAreaAsync();

        var entered = 0;
        var release = 0;
        var lateDisposals = 0;
        host.RegisterForDisposal(RegistryArea, Disposable.Create(() =>
        {
            Volatile.Write(ref entered, 1);
            SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TimeSpan.FromSeconds(10));
        }));
        host.RegisterForDisposal(RegistryArea, Disposable.Empty);

        // Producer → test signal: the teardown thread's outcome (null = completed without throwing).
        var teardownOutcome = new AsyncSubject<Exception?>();
        var teardown = new Thread(() =>
        {
            Exception? fault = null;
            try { host.Dispose(); }
            catch (Exception ex) { fault = ex; }
            teardownOutcome.OnNext(fault);
            teardownOutcome.OnCompleted();
        }) { IsBackground = true, Name = "issue6975-teardown" };

        try
        {
            teardown.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref entered) == 1, TimeSpan.FromSeconds(10))
                .Should().BeTrue("the teardown must reach the parked entry for this test to measure anything");

            // Registered while the teardown is mid-pass on the same area.
            host.RegisterForDisposal(RegistryArea, Disposable.Create(() => Interlocked.Increment(ref lateDisposals)));
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        var outcome = await teardownOutcome.Should().Within(10.Seconds())
            .Emit(cancellationToken: TestContext.Current.CancellationToken);
        outcome.Should().BeNull(
            "a registration on another thread must never land in the collection a teardown is enumerating (#6975)");
        Volatile.Read(ref lateDisposals).Should().Be(1,
            "the late registration met a disposed host and was disposed exactly once — not lost, not doubled");
    }

    /// <summary>
    /// Defined semantics for an AREA pass (<c>UpdateArea</c> → <c>DisposeChildAreas</c>): an entry
    /// registered while the pass disposes the area's previous entries is NOT part of that pass — it
    /// belongs to the registry the pass left behind, and the host's own disposal reaps it exactly once.
    /// </summary>
    [HubFact]
    public async Task RegisteringFromATeardown_DuringAnAreaPass_IsHeldAndDisposedByTheHost()
    {
        var host = await RenderProbeAreaAsync();

        var lateDisposals = 0;
        var late = Disposable.Create(() => Interlocked.Increment(ref lateDisposals));
        var firstDisposed = new AsyncSubject<Unit>();
        host.RegisterForDisposal(RegistryArea, Disposable.Create(() =>
        {
            host.RegisterForDisposal(RegistryArea, late);
            firstDisposed.OnNext(Unit.Default);
            firstDisposed.OnCompleted();
        }));

        // Re-render the parent area: its child pass disposes RegistryArea's entries on the action block.
        host.UpdateArea(new RenderingContext(ProbeView), Controls.Html("re-rendered"));
        await firstDisposed.Should().Within(10.Seconds())
            .Emit(cancellationToken: TestContext.Current.CancellationToken);

        Volatile.Read(ref lateDisposals).Should().Be(0,
            "an entry registered during the pass is not taken by it — it is held for the next pass");

        host.Dispose();

        Volatile.Read(ref lateDisposals).Should().Be(1,
            "the held entry is reaped by the host's disposal exactly once — no leak");
    }
}
