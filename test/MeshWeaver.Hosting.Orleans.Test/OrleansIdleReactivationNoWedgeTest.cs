using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// End-to-end reactivation smoke test for the production bug where navigating to a page whose
/// per-node grain just went idle intermittently CRASHES, and a manual browser reload "fixes" it.
///
/// <para><b>Context.</b> Orleans idle-collects a per-node grain. The next read's
/// <c>SubscribeRequest</c> can hit the mid-<c>DeactivateOnIdle</c> activation; Orleans forwards it
/// up to <c>MaxForwardCount</c>=2 times and then transiently rejects
/// ("Forwarding failed … to invalid activation. Rejecting now."), or — if reactivation is slow —
/// the 60&#160;s hub-request timeout fires. The silo routing grain surfaces that transient failure
/// to the reading cache hub, which (before the fix) recorded it into its storm-breaker negative
/// cache exactly like a genuine missing node — replaying the raw Orleans reject to every reader for
/// the backoff window and refusing to re-probe the grain that had ALREADY reactivated. The manual
/// reload merely outlasted the window. The precise transient-vs-missing decision the fix turns on
/// is pinned deterministically by <c>MeshNodeStreamCacheNegativeClassifierTest</c> (the exact prod
/// message strings); the recording site is now symmetric with the write path.</para>
///
/// <para><b>What this test asserts (the smoke test).</b> A read issued right after the owning grain
/// is asked to deactivate must transparently REACTIVATE and return the node — never crash, never
/// wedge. A single in-memory test silo reactivates cleanly enough that it does not reliably hit the
/// two-hop forwarding-reject window, so this is a coverage smoke test for the reactivation path (it
/// would catch a gross regression that broke reactivation-after-idle), NOT the before/after
/// regression pin — that role belongs to the classifier test. A <c>WaitAsync</c> budget turns any
/// wedge into a deterministic <see cref="TimeoutException"/> (RED); a value inside the budget is
/// GREEN.</para>
/// </summary>
public class OrleansIdleReactivationNoWedgeTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    private IMessageHub GetClient([CallerMemberName] string? name = null)
        => base.GetClient($"idle-react-{name}-{Guid.NewGuid():N}", "TestUser");

    private async Task<string> CreateNode(IMessageHub client, string prefix)
    {
        var id = $"{prefix}-{Guid.NewGuid():N}";
        var response = await client.Observe(
                new CreateNodeRequest(new MeshNode(id, "TestUser") { Name = "Original", NodeType = "Markdown" }),
                o => o.WithTarget(new Address("TestUser")))
            .FirstAsync().ToTask().WaitAsync(45.Seconds());
        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        return response.Message.Node!.Path!;
    }

    // Live, CQRS-correct read via the per-node MeshNode stream (the exact path a GUI layout area
    // subscription takes — through the cache hub's SubscribeRequest to the node grain).
    private IObservable<MeshNode?> ReadNode(IMessageHub client, string path)
        => client.GetWorkspace().GetMeshNodeStream(path).Where(n => n is not null);

    [Fact(Timeout = 120_000)]
    public async Task ReadRightAfterGrainDeactivation_TransparentlyReactivates_NeverWedges()
    {
        var client = GetClient();
        var path = await CreateNode(client, "reactivate");

        // 1. Warm read — activates the node grain and opens the cache entry.
        var warm = await ReadNode(client, path).FirstAsync().ToTask().WaitAsync(45.Seconds());
        warm!.Path.Should().Be(path);
        Output.WriteLine($"[warm] {path} read, grain active");

        // 2. Force the owning per-node grain to deactivate (DeactivateOnIdle), reproducing an
        //    Orleans idle collection. This is exactly the state in which the next delivery hits a
        //    mid-deactivation activation and Orleans transiently rejects it.
        Fixture.CleanupSiloHubsWithPrefix(path);
        Output.WriteLine("[deactivate] requested DeactivateOnIdle on the owning grain");

        // 3. Read AGAIN, immediately and repeatedly, straight into the deactivation window. Each
        //    read must transparently reactivate the grain and return the node — never surface the
        //    Orleans forwarding-reject, never spin to the 60 s timeout, and never get stuck behind a
        //    poisoned negative-cache window. Several back-to-back reads catch the race regardless of
        //    exactly when the grain finishes tearing down.
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var reactivated = await ReadNode(client, path)
                .FirstAsync().ToTask()
                .WaitAsync(30.Seconds());   // a wedge (poisoned breaker / lost reactivation) trips this
            reactivated!.Path.Should().Be(path,
                $"read #{attempt} after deactivation must transparently reactivate the grain and return the node");
            Output.WriteLine($"[reactivated] read #{attempt} succeeded — grain came back transparently");
        }
    }

    /// <summary>
    /// A stronger form: after a deactivation-window read, the SAME path must keep answering promptly
    /// — proving no negative-cache window was opened by a transient reject (a poisoned window would
    /// make this second read fast-fail with the replayed Orleans reject well inside the storm
    /// backoff, surfacing as a NON-timeout exception rather than a value).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AfterReactivation_ImmediateReReadStaysHealthy_NoPoisonedNegativeCache()
    {
        var client = GetClient();
        var path = await CreateNode(client, "healthy");

        await ReadNode(client, path).FirstAsync().ToTask().WaitAsync(45.Seconds());
        Fixture.CleanupSiloHubsWithPrefix(path);

        // First read into the window: reactivates (or the grain already finished). Either way it
        // must produce the node, not throw the transient reject.
        var first = await ReadNode(client, path).FirstAsync().ToTask().WaitAsync(30.Seconds());
        first!.Path.Should().Be(path);

        // Immediate re-read — if the first read had poisoned the negative cache with the transient
        // reject, THIS read would replay that error instantly (a DeliveryFailureException, NOT a
        // TimeoutException) instead of returning the node.
        var again = await ReadNode(client, path).FirstAsync().ToTask().WaitAsync(15.Seconds());
        again!.Path.Should().Be(path,
            "an immediate re-read after reactivation must return the node — a poisoned negative-cache "
            + "window from a transient reject would instead replay the raw Orleans reject");
        Output.WriteLine("[healthy] immediate re-read after reactivation returned the node — breaker clean");
    }
}
