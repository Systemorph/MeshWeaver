using System;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Deterministic repro for the sync-storm memory-leak / wedge: a hub whose init gate never opens (a
/// client sync/cache hub whose <c>[Initialize]</c> never fires) used to defer EVERY inbound message +
/// arm a 30s timer each, so a writer flooding it accumulated the deferred buffer without bound until
/// the action block starved and <c>/healthz</c> timed out (memory 4.3Gi on memex.localhost, cleared
/// only by a restart). The stuck-gate safety net (<c>MessageService.MaxDeferredMessages</c>) caps the
/// deferred backlog and drops the overflow, so memory stays bounded no matter how hard the stuck hub
/// is flooded. This test floods a never-opening-gate hub well past the cap and asserts the deferred
/// backlog does NOT grow past it.
/// </summary>
public class DeferredBacklogBoundedTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>A plain non-system, non-[CanBeIgnored] message → defers behind a closed gate.</summary>
    private record Filler(int N);

    /// <summary>Mirror of the internal <c>MessageService.MaxDeferredMessages</c> — kept in sync deliberately.</summary>
    private const int Cap = 512;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(Filler), typeof(Awaited), typeof(Ack))
            // A user gate that never opens: every non-bypass message defers behind it forever —
            // exactly a client sync/cache hub whose [Initialize] never fires.
            .WithInitializationGate("test-never-opens", _ => false);

    [Fact(Timeout = 30000)]
    public async Task DeferredBacklog_StaysBounded_WhenGateNeverOpens()
    {
        var host = GetHost();

        // Flood well past the cap. WITHOUT the safety net every one of these queues in the deferred
        // buffer (+ a 30s timer each) — the unbounded accumulation that OOM-wedged the portal.
        const int posts = Cap + 300;
        for (var i = 0; i < posts; i++)
            host.Post(new Filler(i), o => o.WithTarget(host.Address));

        // Poll the PUBLIC disposal diagnostics (reports "deferred=<N>" per hub) until the deferred
        // backlog is stable across two reads — the single-threaded action block defers messages one at
        // a time, so the count climbs to the cap and then holds as overflow is dropped.
        // The HOST hub is the ONLY hub this test floods, so it holds the deepest deferred backlog;
        // other hubs (mesh router / hosted hubs) never approach it. The max deferred= across the
        // diagnostics therefore IS the host's backlog in this test.
        int Deferred()
        {
            var diag = host.GetDisposalDiagnostics();
            var max = 0;
            foreach (Match m in Regex.Matches(diag, @"deferred=(\d+)"))
                max = Math.Max(max, int.Parse(m.Groups[1].Value));
            return max;
        }

        // Wait until the backlog has CLIMBED and SETTLED at a NON-ZERO value (two consecutive equal,
        // non-zero reads). Never exit on the initial 0 before the action block starts deferring — that
        // would flake under CI load. If it never climbs, the loop runs out and `settled` stays 0, so the
        // first assert fails CLEARLY rather than silently passing.
        var settled = 0;
        for (var i = 0; i < 150; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            var d = Deferred();
            if (d > 0 && d == settled) break;   // stable + non-zero → the backlog has settled
            settled = d;
        }

        Assert.True(settled > 0,
            $"backlog should climb and settle behind the never-opening gate (last saw {settled})");
        Assert.True(settled <= Cap,
            $"deferred backlog must stay bounded at the cap {Cap}; settled at {settled} — unbounded accumulation is the wedge");
    }

    /// <summary>A request somebody is awaiting — the shape the overflow drop used to silence.</summary>
    private record Awaited : IRequest<Ack>;

    /// <summary>Its reply. Never posted in this test: the request is dropped before any handler.</summary>
    private record Ack;

    /// <summary>
    /// 🚨 <b>Bounding memory is right; doing it SILENTLY to a request/response caller is not
    /// (MeshWeaver#1174).</b>
    ///
    /// <para>The overflow branch returned <c>delivery.Ignored()</c> and answered nobody,
    /// justified in comment as <i>"a fire-and-forget writer's retry isn't fed; a client re-syncs a
    /// fresh Full on reconnect"</i>. That holds for the traffic it was written about and is simply
    /// false for an awaited <see cref="IRequest"/>: nothing re-syncs a <c>CreateNodeRequest</c>, so
    /// its caller burns its ENTIRE <c>RequestTimeout</c> on a drop this hub had already decided —
    /// and the timeout it finally raises cannot name the cause, because from the caller's side an
    /// intake drop and a handler that never replied look identical.</para>
    ///
    /// <para>That path is reachable on the mesh's node-CRUD hub: <c>portal/nodeops-{meshId}</c> is
    /// built with <c>.AddData()</c>, which installs the <c>DataContextInit</c> gate whose only
    /// bypass predicate is <c>PingRequest</c> — so node CRUD IS deferrable there.</para>
    ///
    /// <para>The drop itself is unchanged. What changes is that a sender waiting on it is told, at
    /// once, with a classification that says "no verdict was reached, retry" rather than "denied"
    /// or "absent".</para>
    /// </summary>
    // 🚨 No hand-written [Fact(Timeout = …)] here, deliberately. The bound this test needs is the
    // hub's OWN RequestTimeout: if the drop went back to being silent, the Observe below waits it
    // out and raises a TimeoutException instead of the DeliveryFailureException asserted — which
    // fails the test with the right reason. A literal would spend TestTimeoutLiteralRatchetGuard's
    // inventory, which may only shrink, to add a second, weaker net.
    [Fact]
    public async Task ARequestDroppedByTheOverflowCap_IsAnswered_NotSilenced()
    {
        var host = GetHost();

        // Fill the deferred queue past the cap FIRST. The main queue is strict FIFO and each
        // filler is deferred on its own turn, so by the time the request below takes its turn the
        // deferred queue is provably at the cap — no polling, no sleeping, no load loop.
        for (var i = 0; i < Cap + 50; i++)
            host.Post(new Filler(i), o => o.WithTarget(host.Address));

        var act = async () => await host
            .Observe<Ack>(new Awaited(), o => o.WithTarget(host.Address))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        // A DeliveryFailureException — NOT a TimeoutException. The distinction is the whole test:
        // a TimeoutException here would mean the request was dropped in silence and the caller
        // spent its whole budget discovering it.
        var failure = (await act.Should().ThrowAsync<DeliveryFailureException>()).Which;

        failure.Message.Should().Contain("cap",
            "the answer must name the stuck-gate overflow that dropped it, not a generic failure");
        failure.Message.Should().Contain("retry",
            "a stuck gate is 'no verdict was reached' — the same request is meaningful again once "
            + "the gate opens, so the answer must not read as a denial or an absence");
    }
}
