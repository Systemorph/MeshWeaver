using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>Case 4 — the steward's first write after its create</b> (MeshWeaver.Plugins#2530). The PR steward
/// created a triage item with <c>CreateNode</c> and then routed its first patch to it; that route
/// answered <c>[ROUTE] NotFound: No node found at 'Hosting/Triage/pull-request/…'. Closest ancestor is
/// 'Hosting/Triage'</c> for a path whose create had already been acknowledged. Plugins stopped routing
/// before the create; the platform half — can a routed write reach a node right after its create? —
/// is what these cases pin, on two silos.
/// </summary>
public class ARoutedWriteRightAfterItsCreateTest(ARoutedWriteRightAfterItsCreateTest.Cluster mesh)
    : IClassFixture<ARoutedWriteRightAfterItsCreateTest.Cluster>
{
    private IMeshService MeshService(int silo) => mesh.Silo(silo).GetRequiredService<IMeshService>();
    private AccessService Access(int silo) => mesh.Silo(silo).GetRequiredService<AccessService>();
    private IMeshNodeStreamCache Cache(int silo) => mesh.Silo(silo).GetRequiredService<IMeshNodeStreamCache>();

    private static MeshNode Item(string ns) =>
        new("Item", ns) { Name = "created", NodeType = "Markdown", State = MeshNodeState.Active };

    /// <summary>Create on one silo, then at once a routed write from the other.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ACreateAcknowledged_IsWritableAtOnce_FromTheOtherSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = $"steward-{Guid.NewGuid():N}";
        var path = $"{ns}/Item";

        await Access(1).RunAsSystem(() => MeshService(1).CreateNode(Item(ns)))
            .Should().Within(TestTimeouts.Convergence).Emit("the create is acknowledged", ct);
        var written = await Access(0).RunAsSystem(() =>
                Cache(0).Update(path, n => n with { Name = "patched" }, mesh.Hub(0).JsonSerializerOptions))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a routed write to a node whose create was acknowledged must reach it", ct);
        written.Name.Should().Be("patched");
    }

    /// <summary>
    /// The steward's shape before #2530: the issuing silo PROBES the path first (a point read of a node
    /// that does not exist yet), then creates it, then routes the first write. The probe's NotFound opens
    /// the storm breaker's window on the path — for writes too — and what closes it is the create's
    /// change event reaching the issuing process.
    ///
    /// <para><b>Negative control:</b> with <c>MeshNodeStreamCache.ResetFailureState</c> made a no-op,
    /// this fails with the steward's exact line, <c>No node found at '…/Item'</c>.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task APreCreateProbe_DoesNotPoisonTheFirstWriteAfterTheCreate()
        => await ProbeCreateWrite(createOn: 0, withholdTheCreatesNotification: false, "steward-probe", TestContext.Current.CancellationToken);

    /// <summary>
    /// 🚨 <b>The gap the harness found.</b> The same probe, but the create is made by ANOTHER replica
    /// and its notification is late (the cross-process relay — PostgreSQL LISTEN in production — is
    /// HELD). The prober's own write then fails with the steward's exact line, <c>No node found at
    /// '…/Item'</c>, although the create has been acknowledged: the probe's NotFound opened the storm
    /// breaker's window on this process, the breaker fast-fails WRITES on a read-minted window
    /// (<c>MeshNodeStreamCache.UpdateRaw</c>), and the only thing that closes it early is the create's
    /// change event reaching this process. Released, the notification closes it and the write lands.
    ///
    /// <para>This PINS today's behaviour so a change to it is deliberate: a fix (a write not
    /// suppressed by a READ's window, or a window that an acknowledged create elsewhere can close)
    /// turns the first assertion red and must rewrite it. It is not yet established that the steward
    /// met THIS shape — its create and its write ran in one process, which
    /// <see cref="APreCreateProbe_DoesNotPoisonTheIssuersFirstWrite_WhenNotificationsAreLate"/> shows
    /// is safe. See Doc/Architecture/FaultInjectionHarness, "What the cases found".</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACrossReplicaCreate_WithALateNotification_LeavesTheProbersWriteShut_UntilTheNotificationArrives()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = $"steward-probe-late-{Guid.NewGuid():N}";
        var path = $"{ns}/Item";
        await Probe(path, ct);

        var late = mesh.Relay.Hold();
        try
        {
            await Access(1).RunAsSystem(() => MeshService(1).CreateNode(Item(ns)))
                .Should().Within(TestTimeouts.Convergence).Emit("the other replica's create is acknowledged", ct);
            var inTheGap = await Write(path, "in-the-gap").Materialize()
                .Should().Within(TestTimeouts.Convergence).Emit("the prober's write settles", ct);
            inTheGap.Kind.Should().Be(System.Reactive.NotificationKind.OnError,
                "TODAY a read-minted window fast-fails the write until the create's notification arrives");
            (inTheGap.Exception?.Message ?? "").Should().Contain(path, "and it fails loudly, naming the path");
            await late.Arrivals.Where(a => a.Contains(path, StringComparison.OrdinalIgnoreCase))
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the create's notification really is the one being held", ct);
        }
        finally
        {
            late.Release();
        }

        var landed = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .Select(_ => Write(path, "after-the-notification").Materialize()
                .Where(n => n.Kind != System.Reactive.NotificationKind.OnCompleted).Take(1))
            .Concat()
            .Where(n => n.Kind == System.Reactive.NotificationKind.OnNext)
            .Select(n => n.Value)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("once the notification arrives the window is closed and the write lands", ct);
        landed.Name.Should().Be("after-the-notification");
    }

    private IObservable<MeshNode> Write(string path, string name)
        => Access(0).RunAsSystem(() =>
            Cache(0).Update(path, n => n with { Name = name }, mesh.Hub(0).JsonSerializerOptions));

    private async Task Probe(string path, System.Threading.CancellationToken ct)
    {
        var probe = await Access(0).RunAsSystem(() =>
                Cache(0).GetStream(path, mesh.Hub(0).JsonSerializerOptions).Materialize())
            .Where(n => n.Kind != System.Reactive.NotificationKind.OnNext || n.Value is not null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the probe of the not-yet-created path answers", ct);
        probe.Kind.Should().Be(System.Reactive.NotificationKind.OnError,
            "the node does not exist yet, so the probe answers NotFound — the answer that opens the window");
    }

    /// <summary>
    /// The steward's exact shape: the SAME process probes, issues the create (acknowledged to it) and
    /// routes the first write, while the cross-process notifications are held. Whatever the create's
    /// acknowledgement proves to the issuer must be enough to route its own next write.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task APreCreateProbe_DoesNotPoisonTheIssuersFirstWrite_WhenNotificationsAreLate()
        => await ProbeCreateWrite(createOn: 0, withholdTheCreatesNotification: true, "steward-probe-issuer", TestContext.Current.CancellationToken);

    private async Task ProbeCreateWrite(int createOn, bool withholdTheCreatesNotification, string prefix,
        System.Threading.CancellationToken ct)
    {
        var ns = $"{prefix}-{Guid.NewGuid():N}";
        var path = $"{ns}/Item";

        await Probe(path, ct);

        using var late = withholdTheCreatesNotification ? mesh.Relay.Hold() : null;
        await Access(createOn).RunAsSystem(() => MeshService(createOn).CreateNode(Item(ns)))
            .Should().Within(TestTimeouts.Convergence).Emit("the create is acknowledged", ct);
        var written = await Access(0).RunAsSystem(() =>
                Cache(0).Update(path, n => n with { Name = "patched" }, mesh.Hub(0).JsonSerializerOptions))
            .Materialize()
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the first routed write after the create settles", ct);
        written.Kind.Should().Be(System.Reactive.NotificationKind.OnNext,
            $"the first routed write after an ACKNOWLEDGED create must reach the node the probe missed: "
            + $"{written.Exception?.Message}");
        written.Value?.Name.Should().Be("patched");
        if (late is not null)
            await late.Arrivals.Where(a => a.Contains(path, StringComparison.OrdinalIgnoreCase))
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the create's notification really was held back — otherwise this measured nothing", ct);
    }

    /// <summary>
    /// The NotFound window made explicit: the create is acknowledged, but the issuing silo's store
    /// cannot see the node yet (<see cref="Testing.FaultInjection.FaultInjectingStorageAdapter.HidePath"/>
    /// — a resolver that has not caught up). This is the shape the steward's log showed, and it pins
    /// what the platform does with it TODAY:
    ///
    /// <list type="number">
    ///   <item>a write routed INTO the window fails, LOUDLY — a <c>NotFound</c> that names the path,
    ///     never a silent hang (the route verdict is terminal by design: the router cannot tell a lag
    ///     from a node that does not exist);</item>
    ///   <item>once the window closes, a write lands. The route cache stores no negative, but the
    ///     write-side storm breaker does: it holds the path shut for one base cooldown (2 s) after the
    ///     NotFound, because no change event follows to clear it. Measured here, never widened.</item>
    /// </list>
    ///
    /// <para>What this does NOT establish: that production has such a window. On a shared PostgreSQL
    /// store an acknowledged create is visible to every replica's resolver; the steward's NotFound is
    /// still unexplained (Plugins#2530, filed to triage as <c>route-notfound-right-after-createnode</c>).
    /// The injector is how the next reproduction attempt can hold the window open on purpose.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AWriteIntoTheNotFoundWindow_FailsLoudly_AndTheWindowDoesNotOutliveItself()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = $"steward-window-{Guid.NewGuid():N}";
        var path = $"{ns}/Item";

        await Access(1).RunAsSystem(() => MeshService(1).CreateNode(Item(ns)))
            .Should().Within(TestTimeouts.Convergence).Emit("the create is acknowledged", ct);

        using (var notYetVisible = mesh.Storage(0).HidePath(path))
        {
            var intoTheWindow = await Access(0).RunAsSystem(() =>
                    Cache(0).Update(path, n => n with { Name = "into-the-window" }, mesh.Hub(0).JsonSerializerOptions))
                .Materialize()
                .Should().Within(TestTimeouts.Convergence)
                .Emit("a write routed into the NotFound window settles — it must never hang", ct);
            await notYetVisible.Arrivals.Should().Within(TestTimeouts.Convergence)
                .Emit("the write's route resolution met the window (otherwise this measured nothing)", ct);
            intoTheWindow.Kind.Should().Be(System.Reactive.NotificationKind.OnError,
                "the router answers a path it cannot resolve with NotFound");
            (intoTheWindow.Exception?.Message ?? "").Should().Contain(path,
                "the failure names the path it could not reach, so the caller can tell which write was lost");
        }

        // The write-side storm breaker (MeshNodeStreamCache) cached that NotFound for the path with
        // its base cooldown, and no change event will clear it — the create's event came BEFORE the
        // NotFound. So the window outlives the lag by up to one cooldown, by design, and then closes
        // on the next natural write. Retrying the write is the caller's move here, exactly as the
        // steward's later delivery was; the assertion is that it LANDS, and within the budget.
        var released = DateTimeOffset.UtcNow;
        var afterTheWindow = await Observable.Interval(TimeSpan.FromMilliseconds(250)).StartWith(0L)
            .Select(_ => Access(0).RunAsSystem(() =>
                    Cache(0).Update(path, n => n with { Name = "after-the-window" }, mesh.Hub(0).JsonSerializerOptions))
                .Materialize()
                .Where(n => n.Kind != System.Reactive.NotificationKind.OnCompleted)
                .Take(1))
            .Concat()
            .Where(n => n.Kind == System.Reactive.NotificationKind.OnNext)
            .Select(n => n.Value)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("once the window has closed, a write lands — a NotFound must not outlive the condition "
                  + "that produced it by more than the storm breaker's cooldown", ct);
        TestContext.Current.SendDiagnosticMessage(
            $"first landed write {(DateTimeOffset.UtcNow - released).TotalMilliseconds:F0} ms after the window closed");
        afterTheWindow.Name.Should().Be("after-the-window");
    }

    /// <summary>Its own cluster, so the storm-breaker and route caches start cold.</summary>
    public class Cluster : FaultInjectionCluster;
}
