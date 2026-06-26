#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.AI.Plugins;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the wedges-to-zero contract for delegation (<c>delegate_to_agent</c>) tool
/// calls — the 2026-06-26 atioz wedge.
///
/// <para>A sub-thread that ERRORS (its node stream faults — a stuck-init gate failure
/// surfaces as a <c>DeliveryFailure</c> ~30s in), TIMES OUT, or ends
/// <see cref="ThreadExecutionStatus.Cancelled"/> without a summary MUST resolve the
/// parent tool call as an <c>"Error: …"</c> string — NEVER an empty success.
/// <c>ThreadExecution.ExtractToolResult</c> keys <c>IsSuccess</c> off the
/// <c>"Error"</c> prefix, so the failure lands as a failed tool-call entry on the
/// parent thread's output cell.</para>
///
/// <para>Before the fix, <see cref="DelegationTool.WaitForDelegationResult"/>'s
/// failure branches did <c>tcs.TrySetResult(sb.ToString())</c> — an EMPTY success.
/// The parent agent was told "the delegation produced nothing", the failure never
/// reached the thread output, AND the wedged sub-hub kept storming path-resolution
/// until the portal GC-thrashed and liveness wedged (502 + pod restart).</para>
/// </summary>
public class DelegationFailureSurfacingTest
{
    private const string Agent = "AgentB";
    private const string SubPath = "Space/Statement/_Thread/parent/round/finde-und-liste-7fb9";

    private static MeshThread Thread(ThreadExecutionStatus status, string? summary = null)
        => new() { Status = status, Summary = summary };

    /// <summary>
    /// The exact production failure: the sub-thread's status stream faults because
    /// its hub's init gates never opened (the SubscribeRequest defers >30s →
    /// DeliveryFailure → the node stream errors). This must surface as a tool error,
    /// not the empty-success that left the parent blind and the sub-hub storming.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task SubThreadStreamFaults_SurfacesToolError_NotEmptySuccess()
    {
        var stream = Observable.Throw<MeshThread>(new InvalidOperationException(
            "Hub …/round/finde-und-liste-7fb9 deferred HeartbeatTick for >30s " +
            "without opening init gates [DataContextInit,MeshNodeInit]"));

        var result = await DelegationTool
            .WaitForDelegationResult(stream, Agent, SubPath, TimeSpan.FromSeconds(5))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        result.Should().StartWith("Error",
            "a sub-thread whose stream faults (stuck init gate) MUST surface as a failed " +
            "tool call — ExtractToolResult keys IsSuccess off the 'Error' prefix — not an " +
            "empty success that tells the parent agent the delegation produced nothing");
        result.Should().Contain(Agent);
    }

    /// <summary>
    /// The sub-thread starts (Executing) but never reaches a terminal state. The
    /// parent must NOT hang — it times out (10-min backstop in prod; 200ms here) and
    /// surfaces an error. A hung tool call is the wedge itself.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task SubThreadNeverCompletes_TimesOut_SurfacesToolError()
    {
        var stream = Observable.Return(Thread(ThreadExecutionStatus.Executing))
            .Concat(Observable.Never<MeshThread>());

        var result = await DelegationTool
            .WaitForDelegationResult(stream, Agent, SubPath, TimeSpan.FromMilliseconds(200))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        result.Should().StartWith("Error");
        result.Should().Contain("timed out",
            "a sub-thread that never reaches terminal must time out into a tool error, " +
            "never hang the parent tool call");
    }

    /// <summary>
    /// Heartbeat cancelled a stuck sub-thread (RequestedStatus=Cancelled) but it
    /// produced no summary. Returning "" as success is misleading — surface the
    /// cancellation as a tool error.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task SubThreadCancelledWithoutSummary_SurfacesToolError()
    {
        var stream = new[]
        {
            Thread(ThreadExecutionStatus.Executing),
            Thread(ThreadExecutionStatus.Cancelled, summary: null),
        }.ToObservable();

        var result = await DelegationTool
            .WaitForDelegationResult(stream, Agent, SubPath, TimeSpan.FromSeconds(5))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        result.Should().StartWith("Error");
        result.Should().Contain("cancelled");
    }

    /// <summary>
    /// A genuinely completed round returns its Summary verbatim — no "Error" prefix,
    /// so ExtractToolResult marks the tool call successful. This guards against the
    /// fix over-reaching and flagging healthy delegations as failures.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task SubThreadCompletesWithSummary_ReturnsSummaryAsSuccess()
    {
        var stream = new[]
        {
            Thread(ThreadExecutionStatus.Executing),
            Thread(ThreadExecutionStatus.Idle, summary: "Found 3 entries."),
        }.ToObservable();

        var result = await DelegationTool
            .WaitForDelegationResult(stream, Agent, SubPath, TimeSpan.FromSeconds(5))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        result.Should().Be("Found 3 entries.",
            "a completed sub-round returns its Summary verbatim (no 'Error' prefix) so " +
            "ExtractToolResult marks the tool call successful");
    }

    /// <summary>
    /// A fast sub-agent can coalesce Running→Idle into one emission; the non-empty
    /// Summary is the authoritative "round finished" signal (no Executing was ever
    /// observed). Must still resolve to the summary, not be treated as the initial
    /// creation-Idle and hang.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task FastSubThread_IdleWithSummaryInSingleEmission_ReturnsSummary()
    {
        var stream = Observable.Return(Thread(ThreadExecutionStatus.Idle, summary: "Quick answer."));

        var result = await DelegationTool
            .WaitForDelegationResult(stream, Agent, SubPath, TimeSpan.FromSeconds(5))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        result.Should().Be("Quick answer.");
    }
}
