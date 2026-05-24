using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI.Attributes;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Layout;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the <see cref="ToolTimeoutAttribute"/> contract enforced by
/// <see cref="AccessContextAIFunction"/>:
/// <list type="bullet">
///   <item>A tool method annotated with <c>[ToolTimeout(seconds)]</c> is
///         cancelled if it doesn't return within the budget; the wrapper
///         returns a synthetic <c>"Tool '...' timed out after Ns"</c>
///         string so the agent's tool-call loop never sees a hung promise.</item>
///   <item>Tools that respect their CancellationToken unwind promptly
///         on timeout (CTS-firing is observable in the inner method).</item>
///   <item>External cancellation (the agent abandoning the call) flows
///         out as <see cref="OperationCanceledException"/>; the wrapper
///         only masks its OWN timer as the synthetic string.</item>
/// </list>
/// </summary>
public class ToolTimeoutAttributeTest
{
    /// <summary>Tool ignores the linked CTS — wrapper must still return the synthetic timeout string and not pin the agent loop.</summary>
    [Fact(Timeout = 10_000)]
    public async Task ShortTimeout_ToolHangsIgnoringCancellation_ReturnsSyntheticTimeoutString()
    {
        var inner = AIFunctionFactory.Create(HangingShortBudget);
        var wrapper = new AccessContextAIFunction(inner, new TestAgentChat(), accessService: null!);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await wrapper.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
        sw.Stop();

        result.Should().NotBeNull();
        result!.ToString().Should().StartWith("Tool 'HangingShortBudget' timed out after 2s",
            "the wrapper must return a synthetic result so the agent loop never " +
            "sees a hung promise");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "wrapper returns once its own timer fires + a small slack — not the " +
            "tool's intrinsic 10s delay");
    }

    /// <summary>Tool honours the linked CTS — wrapper returns the synthetic timeout and the tool unwinds essentially immediately.</summary>
    [Fact(Timeout = 10_000)]
    public async Task ShortTimeout_ToolRespectsCancellation_ReturnsTimeoutAndUnwindsPromptly()
    {
        var inner = AIFunctionFactory.Create(HangingShortBudgetRespectsCancellation);
        var wrapper = new AccessContextAIFunction(inner, new TestAgentChat(), accessService: null!);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await wrapper.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
        sw.Stop();

        result!.ToString().Should().Contain("timed out after 2s");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3),
            "a cancellation-aware tool unwinds essentially immediately on CTS fire");
    }

    /// <summary>External cancellation propagates as OperationCanceledException — the wrapper only masks its OWN timer as the synthetic string.</summary>
    [Fact(Timeout = 10_000)]
    public async Task ExternalCancellation_PropagatesAsOperationCancelled_NotSyntheticTimeout()
    {
        var inner = AIFunctionFactory.Create(HangingShortBudgetRespectsCancellation);
        var wrapper = new AccessContextAIFunction(inner, new TestAgentChat(), accessService: null!);

        using var externalCts = new CancellationTokenSource(500.Milliseconds());

        Func<Task> act = () => wrapper.InvokeAsync(new AIFunctionArguments(), externalCts.Token).AsTask();
        await act.Should().ThrowAsync<OperationCanceledException>(
            "external cancellation must propagate — the wrapper's synthetic " +
            "timeout message is reserved for its OWN timer firing");
    }

    // ── Tool methods ─────────────────────────────────────────────────────

    [Description("Test tool: hangs 10s, 2s budget — does NOT honor cancellation token")]
    [ToolTimeout(2)]
    private static async Task<string> HangingShortBudget()
    {
        await Task.Delay(10_000);
        return "should never reach here";
    }

    [Description("Test tool: hangs 10s, 2s budget — DOES honor cancellation token")]
    [ToolTimeout(2)]
    private static async Task<string> HangingShortBudgetRespectsCancellation(CancellationToken ct)
    {
        await Task.Delay(10_000, ct);
        return "should never reach here";
    }

    /// <summary>
    /// Minimal IAgentChat stub — AccessContextAIFunction only reads
    /// <see cref="IAgentChat.ExecutionContext"/>; the rest get the
    /// interface's default no-op implementations.
    /// </summary>
    private sealed class TestAgentChat : IAgentChat
    {
        public void SetContext(AgentContext? applicationContext) { }
        public void SetSelectedAgent(string? agentName) { }
        public Task ResumeAsync(ChatConversation conversation) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentDisplayInfo>> GetOrderedAgentsAsync() =>
            Task.FromResult<IReadOnlyList<AgentDisplayInfo>>([]);
        public async IAsyncEnumerable<ChatMessage> GetResponseAsync(
            IReadOnlyCollection<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IReadOnlyCollection<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public void SetThreadId(string threadId) { }
        public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl) { }
        public AgentContext? Context => null;
    }
}
