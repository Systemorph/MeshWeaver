#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI.Plugins;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// 🚨 A <c>delegate_to_agent</c> call MUST observe the cancellation token it is invoked with.
///
/// <para><b>The defect this pins (#1863).</b> The tool bridged sub-thread completion back to the
/// parent agent loop through a <see cref="TaskCompletionSource{TResult}"/> that <b>nothing ever
/// cancelled</b>. Its <c>cancellationToken</c> parameter was forwarded to the sub-thread launch and
/// consulted in one error branch, but no path completed the returned <c>Task</c> when the token
/// fired. So the only ways out were a sub-thread reaching a terminal state, a
/// <c>Dispatched</c>/<c>Failed</c> delegation event, or
/// <see cref="DelegationTool.WaitForDelegationResult"/>'s <b>10-minute</b> backstop — and the
/// legacy (no delegation-events) path had no backstop at all.</para>
///
/// <para><b>Why that is a teardown defect, not a slow tool call.</b> The whole agent round runs as
/// a leaf on the bounded <c>IoPoolNames.Ai</c> pool, holding one gate permit for its entire
/// duration. <c>IoPool.Drain()</c> — the join every teardown orchestrator performs before it
/// disposes the service scope and unloads collectible node ALCs — cancels the pool token and then
/// re-acquires every permit. #1879 made the round itself observe that token, but a round parked
/// inside an uncancellable tool call never reaches the code that would notice: the parked
/// continuation holds the permit, <c>Drain</c> sits out its full 30&#160;s <c>DrainTimeout</c>, and
/// teardown proceeds over live code — the use-after-unload SIGSEGV precondition.</para>
///
/// <para>Observed as <c>DelegationSubThreadUsageTest</c> failing in teardown with
/// <c>teardown DIRTY — 1 pooled I/O leaf(s) still running</c> after a ~32&#160;006&#160;ms dispose,
/// with every assertion in the test body having passed (CI run 32271833370, shard 5). The test
/// waits on the SUB-thread's usage satellite, which <c>RecordUsage</c> writes as an independent
/// side effect that is deliberately NOT chained before the round's terminal write — so the test can
/// legitimately finish while the PARENT round is still parked in its delegation await.</para>
///
/// <para>The same defect makes the Stop button a lie: a user cancelling a thread that is mid
/// delegation fires the round's <c>executionCts</c>, which reaches the tool as this same token.</para>
/// </summary>
public class DelegationCancellationTest
{
    private static readonly AgentConfiguration AgentA = new() { Id = "AgentA" };
    private static readonly AgentConfiguration AgentB = new() { Id = "AgentB", Description = "target" };

    private static AIFunction CreateTool(
        Func<string, string, string?, CancellationToken, IObservable<string>> execute) =>
        (AIFunction)DelegationTool.CreateUnifiedDelegationTool(
            AgentA, [AgentA, AgentB], execute);

    private static AIFunctionArguments Args() => new(new Dictionary<string, object?>
    {
        ["agentName"] = "AgentB",
        ["task"] = "do work"
    });

    /// <summary>
    /// The sub-thread never produces anything and never terminates — the shape of a delegation that
    /// is still in flight when the round is cancelled (Stop button, hub disposal, or
    /// <c>IoPool.Drain</c> during teardown). Cancelling the invocation token must complete the tool
    /// call promptly.
    ///
    /// <para>Non-vacuity: against the unfixed <c>DelegationTool</c> this never completes, so the
    /// bounded wait below fails with a <see cref="TimeoutException"/> instead of the
    /// <see cref="OperationCanceledException"/> asserted here.</para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ParkedDelegation_UnwindsWhenTheRoundsTokenIsCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var round = new CancellationTokenSource();

        // Never emits, never completes: the sub-thread is running and has said nothing yet.
        var tool = CreateTool((_, _, _, _) => Observable.Never<string>());

        var call = tool.InvokeAsync(Args(), round.Token).AsTask();

        // It must genuinely be waiting — otherwise a "prompt" completion below would prove nothing.
        var settledEarly = await Task.WhenAny(call, Task.Delay(250, ct));
        settledEarly.Should().NotBeSameAs(call,
            "the delegation is still in flight — the tool call must not resolve before either the "
            + "sub-thread finishes or the round is cancelled");

        round.Cancel();

        var act = async () => await call.WaitAsync(TimeSpan.FromSeconds(10), ct);
        // WaitAsync's own failure is a TimeoutException, which is NOT an
        // OperationCanceledException — so this assertion cannot be satisfied by the wait giving up.
        await act.Should().ThrowAsync<OperationCanceledException>(
            "a cancelled round must unwind its parked tool call — the round holds an Ai-pool gate "
            + "permit for its whole duration, and IoPool.Drain() cannot join a leaf that is parked "
            + "inside a Task nothing cancels");
    }

    /// <summary>
    /// The happy path is untouched: a delegation that completes normally still resolves with the
    /// aggregated sub-thread text, and the cancellation wiring does not swallow it.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task CompletedDelegation_StillReturnsItsResult()
    {
        var ct = TestContext.Current.CancellationToken;
        using var round = new CancellationTokenSource();

        var tool = CreateTool((_, _, _, _) => Observable.Return("Hello, ").Concat(Observable.Return("world!")));

        var result = await tool.InvokeAsync(Args(), round.Token).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), ct);

        result?.ToString().Should().Contain("Hello, ").And.Contain("world!");
    }
}
