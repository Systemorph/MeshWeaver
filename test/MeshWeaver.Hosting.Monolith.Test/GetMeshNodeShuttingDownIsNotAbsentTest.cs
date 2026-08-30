using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 A RECYCLING NODE IS NOT A MISSING NODE — AND A RECYCLE IS RIDDEN OUT, NOT SURFACED EARLY.
///
/// <para><see cref="ErrorType.ShuttingDown"/> has an explicit contract (Events.cs): "retry-worthy,
/// never terminal … the sender must read this as 'ask again', not 'gone'". Routing mints it
/// DELIBERATELY instead of NotFound for a live-but-recycling address
/// (<c>MonolithRoutingService</c>: <c>isShuttingDown ? ErrorType.ShuttingDown : ErrorType.NotFound</c>),
/// and the live-stream path honours that — <c>MeshNodeStreamCache.IsTransientOwnerFailure</c>
/// classifies "is shutting down" as transient.</para>
///
/// <para><b>The read primitive's history, in two defects.</b> First it collapsed ShuttingDown into
/// the same <c>null</c> a genuine not-found emits — a caller was told the node does not exist while
/// it was merely recycling (the <c>ThreadAgentIntegrationTest</c> CI failure: "ACME/ProductLaunch
/// node should exist" 6.4 s into a 60 s budget). Then it surfaced the transient after exactly ONE
/// immediate re-probe — both probes landed within milliseconds of each other, so ANY recycle longer
/// than a beat (the 8 s force-teardown watchdog window of a wedged package-root dispose,
/// MeshWeaver#1701) turned into a terminal error with almost the caller's entire budget unused.
/// Every NodeType compile reading the root in that window settled
/// <c>CompilationStatus=Error</c> and the satellite gates reported phantom, module-varying
/// "compile failures" (Reinsurance run 31992742420: 2 types; the same tree an hour later: 17).</para>
///
/// <para><b>The contract now:</b> re-probe WITHIN the caller's budget — first NACK immediately (the
/// healthy sub-second recycle stays zero-latency), later NACKs on the pacing timer — and terminate
/// authoritatively at the budget: a recycler that outlasts it surfaces the typed
/// <see cref="AddressRecyclingException"/> (Throw callers) or the Unavailable outcome (EmitNull
/// callers, whose documented contract is "indeterminate ⇒ treat as absent"). Bounded means
/// BOUNDED BY THE CALLER'S BUDGET — never by a probe count that discards it, and never a loop that
/// outlives it.</para>
///
/// <para>The sibling <c>DeferredDeliveryNackedOnDisposeTest</c> pins that the NACK is produced and
/// carries ShuttingDown. This pins what the READER does with it.</para>
/// </summary>
public class GetMeshNodeShuttingDownIsNotAbsentTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Counts how many <c>GetDataRequest</c>s reach the address, so the re-probing is not merely
    /// inferred from the outcome — the count IS the assertion that it happened.
    /// </summary>
    private int _reads;

    [Fact(Timeout = 120_000)]
    public async Task ShuttingDownNack_PersistingPastTheBudget_IsSurfaced_NeverCollapsedToNull()
    {
        var address = new Address("recycling", "forever");
        // A hub that ALWAYS answers as one caught mid-recycle — deterministic: there is no
        // millisecond window to lose, unlike the production race.
        var recycling = Mesh.GetHostedHub(
            address,
            c => c.WithHandler<GetDataRequest>((hub, delivery) =>
            {
                Interlocked.Increment(ref _reads);
                hub.Post(
                    new DeliveryFailure(delivery)
                    {
                        ErrorType = ErrorType.ShuttingDown,
                        Message = $"Hub {address} is shutting down — cannot read. "
                                  + "The address may reactivate (recycle / restart); retry to get "
                                  + "the authoritative answer."
                    },
                    o => o.ResponseFor(delivery));
                return delivery.Processed();
            }));
        recycling.Should().NotBeNull();

        var read = Mesh
            .GetMeshNode(address.ToString(), TimeSpan.FromSeconds(3))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        // The whole point: a Throw-mode caller must NOT be handed null. Null is what "node not
        // found" means, and a recycling node is present — that conflation is what made the CI
        // failure claim a node in the test's own fixture did not exist.
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => read);

        Output.WriteLine($"surfaced: {failure.GetType().Name}: {failure.Message}");
        failure.Should().BeOfType<AddressRecyclingException>(
            "the verdict is TYPED so downstream classifiers (ApplyCompileFailure → "
            + "CompilationStatus.Unavailable) can file it as availability, never as a code verdict");
        failure.Message.Should().Contain("ShuttingDown",
            "the error must name the classification, so the next reader knows to retry rather than "
            + "concluding the node is gone");
        failure.Message.Should().Contain("NOT absent",
            "…and must say explicitly that this is not absence — the misreading this exists to stop");

        // The re-probing is bounded by the BUDGET, not by a probe count: the original read, the
        // immediate re-probe, and then paced probes until the 3 s budget elapsed. Fewer than
        // three total would mean the budget was discarded again (the #1701 defect: both probes
        // burned within milliseconds, 14 s of a 15 s budget unused); the pacing keeps the count
        // small (~½ s apart), so this can never be a hot loop.
        Volatile.Read(ref _reads).Should().BeGreaterThanOrEqualTo(3,
            "a persistent recycler is probed throughout the budget — immediately once, then paced");
    }

    [Fact(Timeout = 120_000)]
    public async Task ShuttingDownNack_RecyclingCompletesWithinBudget_YieldsTheNode()
    {
        // The MeshWeaver#1701 shape: the owner is mid-recycle when the read starts and answers
        // ShuttingDown for a while (the wedged-dispose window), then reactivates and serves the
        // node. The read must RIDE THE RECYCLE OUT and emit the node — the previous
        // one-immediate-re-probe cap settled this exact sequence as a terminal error, which is
        // how every NodeType compile reading its package root during an install-recycle went
        // CompilationStatus=Error and the satellite compile gates failed nondeterministically.
        var address = new Address("recycling", "recovers");
        var node = new MeshNode(
            MeshNode.FromPath(address.Path).Id, MeshNode.FromPath(address.Path).Namespace)
        {
            Name = "Recovered",
            State = MeshNodeState.Active
        };
        var recycling = Mesh.GetHostedHub(
            address,
            c => c.WithHandler<GetDataRequest>((hub, delivery) =>
            {
                var read = Interlocked.Increment(ref _reads);
                if (read <= 3)
                {
                    hub.Post(
                        new DeliveryFailure(delivery)
                        {
                            ErrorType = ErrorType.ShuttingDown,
                            Message = $"Hub {address} is shutting down — cannot read. "
                                      + "The address may reactivate (recycle / restart); retry to get "
                                      + "the authoritative answer."
                        },
                        o => o.ResponseFor(delivery));
                }
                else
                {
                    hub.Post(new GetDataResponse(node, 1), o => o.ResponseFor(delivery));
                }
                return delivery.Processed();
            }));
        recycling.Should().NotBeNull();

        var result = await Mesh
            .GetMeshNode(address.ToString(), TimeSpan.FromSeconds(10))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        result.Should().NotBeNull(
            "the recycle completed well inside the budget, so the read must deliver the node "
            + "instead of surfacing a transient that had already healed");
        result!.Name.Should().Be("Recovered");

        // Deterministic probe ledger: the original read (NACK), the immediate re-probe (NACK),
        // one paced probe (NACK), one more paced probe (the answer). Exactly four — the pacing
        // is a schedule, not a storm.
        Volatile.Read(ref _reads).Should().Be(4,
            "three NACKs then the answer: original + immediate re-probe + two paced probes");
    }

    [Fact(Timeout = 120_000)]
    public async Task ShuttingDownNack_PersistingPastTheBudget_EmitNullCaller_DegradesOpen()
    {
        // EmitNull callers opted into "indeterminate ⇒ treat as absent" (a cosmetic fallback, an
        // idempotent existence probe, the cell-surface single-home gate's fail-OPEN owner read).
        // A recycler that outlasts the whole budget is exactly a timeout-shaped indeterminate for
        // them: the read emits null instead of erroring — which is what lets
        // ValidateCellSurfaceSingleHome keep its documented "a transient mesh blip can never turn
        // into a hard compile failure here" contract even for a pathological recycle. Callers for
        // which absent ≠ unavailable matters read GetMeshNodeOutcome, where this arrives as
        // Unavailable (carrying the AddressRecyclingException), never as Absent.
        var address = new Address("recycling", "emitnull");
        var recycling = Mesh.GetHostedHub(
            address,
            c => c.WithHandler<GetDataRequest>((hub, delivery) =>
            {
                Interlocked.Increment(ref _reads);
                hub.Post(
                    new DeliveryFailure(delivery)
                    {
                        ErrorType = ErrorType.ShuttingDown,
                        Message = $"Hub {address} is shutting down — cannot read."
                    },
                    o => o.ResponseFor(delivery));
                return delivery.Processed();
            }));
        recycling.Should().NotBeNull();

        var outcome = await Mesh
            .GetMeshNodeOutcome(address.ToString(), TimeSpan.FromSeconds(2),
                ReadTimeoutBehavior.EmitNull)
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(NodeReadStatus.Unavailable,
            "a full-budget recycle is indeterminate — NEVER Absent (recycling ≠ gone), and for an "
            + "EmitNull caller never an error either");
        outcome.Failure.Should().BeOfType<AddressRecyclingException>(
            "the outcome still says WHY, so a caller that asked for the distinction learns the "
            + "address was recycling");
    }
}
