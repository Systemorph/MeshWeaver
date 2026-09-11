using System;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#3510 — a package root must not be recycled out from under the install that is writing
/// under it, and the deferral must not block the ordinary recycle.</b>
///
/// <para><b>Measured, core CD 7950 and five occurrences after it.</b> The <c>Hosting</c> root hub
/// was disposed at 23:37:51Z while <c>Hosting</c>'s own 145-file install was in flight. Its
/// per-node children went down with it, so the writes those children owed acks for were stranded
/// (<c>[UpdateQueue] ADVANCE_WITHOUT_HANDOFF … the owner never acknowledged this write</c>), the
/// <c>nodeops</c> handler that owed its reply to one of those acks never replied, the installer's
/// <c>Observe</c> never fired, and the install ran out the gate's ten-minute bound as
/// <c>[FAIL] Hosting — install: TimeoutException</c>. Four lost bake seals.</para>
///
/// <para><b>The remedy pinned here is DEFER, not NACK.</b> A recycle aimed at a root an install
/// holds waits for the install to release it, and then runs. The alternative — failing every
/// in-flight write under the root — was measured WRONG one lane over in #3112: the caller's
/// terminal has already fired at <c>LOCAL_EMIT</c> and the write may well have committed, so a
/// fault there reports failure for a write that landed.</para>
///
/// <para>🚨 <b>Both directions, because a fix that blocked EVERY recycle would pass a one-sided
/// test</b> — and #3965 measured that a root recycling under a reconcile is the COMMON case. Each
/// regression case below is paired with the identical composition holding NO lease, which must
/// still recycle promptly.</para>
///
/// <para>🚨 <b>No hand-woven gate.</b> The release is an <c>IDisposable</c> disposed by the test,
/// and every wait is either an assertion helper's bounded await or the registry's own cold
/// observable. Nothing sleeps, nothing polls, and no <see cref="TimeSpan"/> literal appears — the
/// windows are <c>TestTimeouts.*</c>.</para>
///
/// <para>Drives <see cref="NodeTypeRebindWatcher.Arm"/> against a REAL, hosted
/// <see cref="IMessageHub"/> (<see cref="HubTestBase"/>) — never a mocked one — exactly as
/// <c>NodeTypeRebindWatcherTest</c> beside it does, and for the same reason: the watcher's only
/// hub-shaped side effect is a self-<see cref="DisposeRequest"/>, so "the hub actually disposed"
/// is a stronger proof than "Post was called".</para>
/// </summary>
public class ARecycleWaitsForTheInstallHoldingTheRootTest(ITestOutputHelper output)
    : HubTestBase(output)
{
    private const string RootPath = "Hosting";
    private const string BenignRootPath = "HostingBenign";
    private const string NeighbourRootPath = "HostingNeighbour";
    private const string NoRegistryRootPath = "HostingNoRegistry";
    private const string BoundType = "Space";
    private const string RealType = "Store/Plugin";

    private const string Holder =
        "PackageInstaller: the install of package 'Hosting' is writing under this root";

    /// <summary>
    /// The same hot, non-replaying stand-in <c>NodeTypeRebindWatcherTest</c> uses: the production
    /// <c>InProcessMeshChangeFeed</c> never replays, and that property is load-bearing.
    /// </summary>
    private sealed class TestChangeFeed : IMeshChangeFeed
    {
        // 🚨 Instance ConcurrentDictionary, never a mutable List: the repository's collections
        // policy holds in test/ too, and this feed is published from one thread while a watcher's
        // disposal removes from another — the exact shape a bare List gets wrong.
        private readonly ConcurrentDictionary<Guid, Action<MeshChangeEvent>> handlers = new();

        public void Publish(MeshChangeEvent change)
        {
            foreach (var handler in handlers.Values)
                handler(change);
        }

        public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
        {
            var key = Guid.NewGuid();
            handlers[key] = e =>
            {
                if (filter is null || e.Kind == filter)
                    handler(e);
            };
            return System.Reactive.Disposables.Disposable.Create(() => handlers.TryRemove(key, out _));
        }
    }

    private (IMessageHub Hub, IObservable<bool> Disposed) BuildRootHub(string path)
    {
        var hub = Mesh.GetHostedHub(new Address(path), c => c);
        var disposed = new ReplaySubject<bool>(1);
        hub.RegisterForDisposal(_ =>
        {
            disposed.OnNext(true);
            disposed.OnCompleted();
        });
        return (hub, disposed);
    }

    private static MeshChangeEvent Retype(string path, string nodeType)
        => new("", path.Split('/')[^1], path, MeshChangeKind.Updated, nodeType, 1,
            DateTimeOffset.UtcNow);

    // ---- The registry's own contract: the release ALWAYS arrives -------------------------------

    /// <summary>
    /// 🚨 <b>The whole deferral rests on this.</b> "Defer" is only allowed to mean <i>wait for a
    /// state that always arrives</i> — so the lease is taken through <c>Observable.Using</c>, whose
    /// resource is disposed on every one of the three ways an Rx subscription can end. If any arm
    /// leaked, a recycle deferred behind it would wait for ever, and the cure would be worse than
    /// #3510 itself.
    /// </summary>
    [Fact]
    public async Task AnInstallReleasesItsRoot_OnCompletion_OnFault_AndOnUnsubscribe()
    {
        var leases = new PackageRootInstallLeases();

        // 1. Completion.
        await leases.HoldDuring(RootPath, Holder, Observable.Return(1))
            .Should().Emit("the wrapped install runs normally");
        leases.IsHeld(RootPath).Should().BeFalse(
            "an install that completed has released its root");

        // 2. Fault — the arm #3510 cares about most, because an install that FAILS is exactly when
        //    a recycle behind it must not be stranded.
        var faulted = false;
        using var faultedInstall = leases
            .HoldDuring<int>(RootPath, Holder,
                Observable.Throw<int>(new InvalidOperationException("install failed")))
            .Subscribe(_ => { }, _ => faulted = true);
        faulted.Should().BeTrue("Observable.Throw faults on subscribe");
        leases.IsHeld(RootPath).Should().BeFalse(
            "an install that FAULTED has released its root — otherwise every recycle behind it "
            + "waits for ever");

        // 3. Unsubscribe — the caller gave up (its own request budget elapsed, the hub went down).
        var subscription = leases
            .HoldDuring(RootPath, Holder, Observable.Never<int>())
            .Subscribe(_ => { });
        leases.IsHeld(RootPath).Should().BeTrue("the install is still running");
        subscription.Dispose();
        leases.IsHeld(RootPath).Should().BeFalse(
            "an install whose caller gave up has released its root");

        await leases.WhenReleased(RootPath).Should().Emit(
            "nothing holds the root, so the wait is answered at once");
    }

    /// <summary>
    /// 🚨 THE CONTROL for the registry: with nobody holding it, the wait is not a wait at all. A
    /// registry that answered only on a release event would deadlock every ordinary recycle —
    /// which is the one-sided failure this whole file is arranged to catch.
    /// </summary>
    [Fact]
    public async Task WithNoInstallHoldingIt_TheWaitIsAnsweredAtOnce()
    {
        var leases = new PackageRootInstallLeases();

        leases.IsHeld(RootPath).Should().BeFalse();
        leases.HeldBy(RootPath).Should().BeNull("nobody holds it, so there is nobody to name");
        await leases.WhenReleased(RootPath).Should().Emit("no lease, no wait");
    }

    /// <summary>
    /// Two installs can legitimately target one partition (<c>targetPartition</c>), and one
    /// finishing must not tell a recycle the other is done. The holder list IS the reference count,
    /// and it is also what the deferral log prints — so "held" and "held by whom" cannot disagree.
    /// </summary>
    [Fact]
    public async Task TheRootStaysHeldUntilTheLASTInstallReleasesIt()
    {
        var leases = new PackageRootInstallLeases();
        var first = leases.Hold(RootPath, Holder);
        var second = leases.Hold(RootPath, "PackageInstaller: the install of package 'Hosting.Instance' …");

        leases.HeldBy(RootPath).Should().Contain("Hosting.Instance", "every holder is named");

        first.Dispose();
        leases.IsHeld(RootPath).Should().BeTrue("the second install is still writing under it");

        second.Dispose();
        await leases.WhenReleased(RootPath).Should().Emit("the last holder has gone");
    }

    // ---- The rebind watcher: the recycler #3965 proved can fire against the installing root -----

    /// <summary>
    /// 🚨 THE REGRESSION CASE. <see cref="NodeTypeRebindWatcher"/> is armed on EVERY instance hub
    /// at activation, package roots included, and <see cref="NodeTypeRebindWatcher.RequiresRebind"/>
    /// fires on precisely the event the installer's placeholder dance produces — the root's retype.
    /// So before this change it could, by construction, tear a package root down in the middle of
    /// that package's own install. It now waits for the install to release the root.
    ///
    /// <para>Note what the second half asserts: the recycle is DEFERRED, not dropped. A fix that
    /// swallowed the recycle would satisfy the first assertion and silently re-break #1104 — a hub
    /// left serving the configuration it was born with for the rest of its life.</para>
    /// </summary>
    [Fact]
    public async Task ARetypeOfARootAnInstallHolds_DoesNotRecycleUntilTheInstallReleasesIt()
    {
        var leases = new PackageRootInstallLeases();
        var lease = leases.Hold(RootPath, Holder);
        var (hub, disposed) = BuildRootHub(RootPath);
        var feed = new TestChangeFeed();
        using var watcher = NodeTypeRebindWatcher.Arm(
            feed, hub, RootPath, BoundType, logger: null, leases: leases);

        feed.Publish(Retype(RootPath, RealType));

        await disposed.Should().NotEmit(
            TestTimeouts.Quick,
            "an install is writing under this root — recycling it now strands the writes its "
            + "per-node children owe acks for (#3510)");

        lease.Dispose();

        await disposed.Should().Within(TestTimeouts.Convergence).Emit(
            "the recycle is DEFERRED, never dropped — the install released the root, so the rebind "
            + "must now happen or the hub keeps its wrong binding for ever (#1104)");
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL, and it is the one that matters here: #3965 measured that a root
    /// recycling with no install of its own is the COMMON case. A change that deferred every
    /// recycle would pass the regression case above and break the mesh's ability to rebind a hub at
    /// all.
    /// </summary>
    [Fact]
    public async Task ARetypeWithNoInstallHoldingTheRoot_RecyclesAtOnce()
    {
        var leases = new PackageRootInstallLeases();
        var (hub, disposed) = BuildRootHub(BenignRootPath);
        var feed = new TestChangeFeed();
        using var watcher = NodeTypeRebindWatcher.Arm(
            feed, hub, BenignRootPath, BoundType, logger: null, leases: leases);

        feed.Publish(Retype(BenignRootPath, RealType));

        await disposed.Should().Within(TestTimeouts.Convergence).Emit(
            "nothing holds this root, so the ordinary rebind recycle proceeds exactly as before");
    }

    /// <summary>
    /// A lease on a DIFFERENT root must not defer this one. The key is the whole path, so a fix
    /// that keyed on a prefix — or on nothing at all — is caught here rather than in production,
    /// where it would look like a recycle that mysteriously never happened.
    /// </summary>
    [Fact]
    public async Task ALeaseOnANeighbouringRoot_DoesNotDeferThisOne()
    {
        var leases = new PackageRootInstallLeases();
        using var elsewhere = leases.Hold("SomeOtherPackage", Holder);
        var (hub, disposed) = BuildRootHub(NeighbourRootPath);
        var feed = new TestChangeFeed();
        using var watcher = NodeTypeRebindWatcher.Arm(
            feed, hub, NeighbourRootPath, BoundType, logger: null, leases: leases);

        feed.Publish(Retype(NeighbourRootPath, RealType));

        await disposed.Should().Within(TestTimeouts.Convergence).Emit(
            "the lease names another root entirely");
    }

    /// <summary>
    /// 🚨 A host that registers no lease registry (a bare test hub, a composition without
    /// <c>MeshBuilder</c>'s registrations) must behave exactly as it did before #3510 — no lease
    /// can exist there, so there is nothing to wait for. Without this case the deferral could
    /// silently become a hard dependency and turn "no registry" into "never recycles".
    /// </summary>
    [Fact]
    public async Task WithNoLeaseRegistryAtAll_TheRecycleIsUnchanged()
    {
        var (hub, disposed) = BuildRootHub(NoRegistryRootPath);
        var feed = new TestChangeFeed();
        using var watcher = NodeTypeRebindWatcher.Arm(
            feed, hub, NoRegistryRootPath, BoundType, logger: null, leases: null);

        feed.Publish(Retype(NoRegistryRootPath, RealType));

        await disposed.Should().Within(TestTimeouts.Convergence).Emit(
            "no registry means no lease can exist, so the recycle posts as it always did");
    }
}
