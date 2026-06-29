using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins the "entire app unresponsive after launching a chat" wedge (2026-06-08).
///
/// <para><b>Trigger.</b> A thread's <c>Messages</c> referenced a response cell whose
/// node no longer existed (orphaned — allocated in a prior in-memory session, never
/// persisted, lost on restart). Reading that unresolvable child behaved
/// ASYMMETRICALLY between request types:</para>
/// <list type="bullet">
///   <item><c>SubscribeRequest</c> <b>failed fast</b> — <see cref="RoutingGrain"/>
///     posts a <c>DeliveryFailure{ErrorType=NotFound}</c> back to the sender, surfaced
///     as <c>OnError</c> (correct exception management).</item>
///   <item><c>GetDataRequest</c> to the SAME path <b>HUNG</b> — the caller's callback
///     stayed pending 57s+ (no exception ever delivered), wedging the thread hub, then
///     <c>portal/{user}</c>'s <c>SubscribeRequest@{user}</c> + <c>@{thread}</c>
///     (RequestPath <c>/_blazor</c>) — i.e. the user's whole Blazor circuit froze.</item>
/// </list>
///
/// <para><b>Invariant.</b> A request to an unresolvable / bad-content node must surface
/// a PROPER exception FAST for EVERY request type — never leave the callback pending and
/// cascade into a circuit wedge. The bounded <c>WaitAsync(10s)</c> converts the wedge
/// into a deterministic <see cref="TimeoutException"/>: while the defect is live the
/// <c>GetDataRequest</c> hangs → <c>TimeoutException</c> (RED); once the routing surfaces
/// the NotFound failure for <c>GetDataRequest</c> the way it already does for
/// <c>SubscribeRequest</c>, it throws <c>DeliveryFailureException</c> well inside 10s (GREEN).</para>
/// </summary>
public class OrleansUnresolvableNodeNoWedgeTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    [Fact]
    public async Task GetDataRequest_ToUnresolvableChild_SurfacesProperException_DoesNotWedge()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;

        // 1. An existing parent node — the "thread" ancestor that DOES resolve.
        var parentId = $"wedge-parent-{Guid.NewGuid():N}";
        var parentPath = $"TestUser/{parentId}";
        var creator = GetClient($"creator-{Guid.NewGuid():N}", "TestUser");
        var createResp = await creator.Observe(
                new CreateNodeRequest(new MeshNode(parentId, "TestUser")
                {
                    Name = "Parent",
                    NodeType = "Markdown",
                }),
                o => o.WithTarget(new Address("TestUser")))
            .FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");
        Output.WriteLine($"[test] parent created: {parentPath}");

        // 2. A child cell that was NEVER created — the orphaned-cell shape: the parent
        //    resolves, the child path carries a non-empty remainder → routing NotFound.
        //    This is the exact shape of rbuergi/_Thread/hello-world-e238/278c379f.
        var missingChildPath = $"{parentPath}/{Guid.NewGuid():N}";
        var reader = GetClient($"reader-{Guid.NewGuid():N}", "TestUser");

        // 3. GetDataRequest to the unresolvable child MUST fail fast with a proper
        //    exception. WaitAsync(10s) turns the wedge into a TimeoutException so the
        //    failure mode is deterministic rather than a 50s+ hang.
        Func<Task> read = () => reader.Observe(
                new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(missingChildPath)))
            .FirstAsync().ToTask()
            .WaitAsync(10.Seconds());

        var thrown = await read.Should().ThrowAsync<Exception>(
            "a GetDataRequest to an unresolvable node must surface a proper exception, "
            + "not leave the caller's callback pending (the circuit-wedge defect)");

        thrown.Which.Should().NotBeOfType<TimeoutException>(
            "a TimeoutException from the bounded wait means the GetDataRequest HUNG past 10s — "
            + "that is the wedge; it must instead surface a DeliveryFailure the way SubscribeRequest does");

        thrown.Which.Should().BeOfType<DeliveryFailureException>(
            "the routing must deliver NotFound back to the GetDataRequest caller as a "
            + "DeliveryFailureException, exactly as it already does for SubscribeRequest");
        Output.WriteLine($"[test] GetDataRequest failed fast: {thrown.Which.GetType().Name}: {thrown.Which.Message}");

        // 4. The hub must stay RESPONSIVE — a follow-up read of the EXISTING parent
        //    still answers promptly. No lingering wedge from the failed child read.
        var parentResp = await reader.Observe(
                new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(parentPath)))
            .FirstAsync().ToTask()
            .WaitAsync(10.Seconds());
        parentResp.Message?.Data.Should().NotBeNull(
            "after a failed-fast child read, the parent hub must still answer promptly — no wedge");
        Output.WriteLine("[test] parent still responsive after failed-fast child read");
    }

    /// <summary>
    /// An ILLEGAL (unregistered) node type must surface a PROPER outcome FAST — either a
    /// clean rejection or a <see cref="DeliveryFailureException"/> — and must never hang
    /// the caller or crash the owning hub. Every op is bounded with <c>WaitAsync</c>, so a
    /// wedge surfaces as a deterministic <see cref="TimeoutException"/> (RED) rather than a
    /// silent freeze. Pins the user's hypothesis: "it must be illegal node type" — a node
    /// the owning hub can't materialize must fail loudly, not wedge the circuit.
    /// </summary>
    [Fact]
    public async Task IllegalNodeType_DoesNotWedge_SurfacesProperOutcome()
    {
        var creator = GetClient($"creator-{Guid.NewGuid():N}", "TestUser");
        var badId = $"illegal-{Guid.NewGuid():N}";
        var illegalType = $"TotallyIllegalType_{Guid.NewGuid():N}";   // never registered anywhere

        // CREATE with an illegal node type must complete FAST — proper outcome, never wedge.
        try
        {
            var createResp = await creator.Observe(
                    new CreateNodeRequest(new MeshNode(badId, "TestUser") { Name = "Bad", NodeType = illegalType }),
                    o => o.WithTarget(new Address("TestUser")))
                .FirstAsync().ToTask().WaitAsync(15.Seconds());
            Output.WriteLine($"[create] success={createResp.Message.Success} error={createResp.Message.Error ?? "(none)"}");
        }
        catch (TimeoutException)
        {
            throw new Exception("WEDGE: CreateNodeRequest with an illegal node type hung >15s instead of surfacing a proper exception");
        }
        catch (DeliveryFailureException dfe)
        {
            Output.WriteLine($"[create] proper failure surfaced: {dfe.Message}");
        }

        // READ it back — must complete fast, never wedge/crash the owning hub.
        var reader = GetClient($"reader-{Guid.NewGuid():N}", "TestUser");
        try
        {
            await reader.Observe(
                    new GetDataRequest(new MeshNodeReference()),
                    o => o.WithTarget(new Address($"TestUser/{badId}")))
                .FirstAsync().ToTask().WaitAsync(15.Seconds());
        }
        catch (TimeoutException)
        {
            throw new Exception("WEDGE: reading an illegal-node-type node hung >15s instead of surfacing a proper exception");
        }
        catch (DeliveryFailureException) { /* proper exception surfaced — acceptable */ }

        // The partition hub must remain RESPONSIVE afterwards — a legal create still works promptly.
        var probe = await GetClient($"probe-{Guid.NewGuid():N}", "TestUser").Observe(
                new CreateNodeRequest(new MeshNode($"probe-{Guid.NewGuid():N}", "TestUser") { Name = "Probe", NodeType = "Markdown" }),
                o => o.WithTarget(new Address("TestUser")))
            .FirstAsync().ToTask().WaitAsync(15.Seconds());
        probe.Message.Success.Should().BeTrue("the partition hub must stay responsive after an illegal-node-type op — no wedge");
    }
}
