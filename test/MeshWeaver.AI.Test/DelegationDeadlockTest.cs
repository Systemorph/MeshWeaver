#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI.Plugins;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Repros the Orleans deadlock in DelegationTool and pins the fix contract.
///
/// Deadlock mechanism: the old <c>async IAsyncEnumerable&lt;string&gt;</c> shape meant
/// FunctionInvokingChatClient drove the sub-thread's enumeration via <c>await foreach</c>
/// on the grain's message-handler stack. Each <c>MoveNextAsync</c> continuation captured
/// the grain's SynchronizationContext. Any sub-thread continuation that needed to post
/// back through that same scheduler wedged it — the grain was stuck inside the awaiting
/// tool call.
///
/// Fix contract: the sub-thread drain must run on ThreadPool with
/// <c>ConfigureAwait(false)</c>, and the tool must return a Task resolved from that
/// ThreadPool drain (TCS-backed). The parent grain still awaits completion, but its
/// await is on a TCS set from a non-hub thread, which never captures the grain scheduler.
/// </summary>
public class DelegationDeadlockTest
{
    private static readonly AgentConfiguration AgentA = new() { Id = "AgentA" };
    private static readonly AgentConfiguration AgentB = new() { Id = "AgentB", Description = "target" };

    private static AIFunction CreateTool(
        Func<string, string, string?, CancellationToken, IAsyncEnumerable<string>> execute) =>
        (AIFunction)DelegationTool.CreateUnifiedDelegationTool(
            AgentA, [AgentA, AgentB], execute);

    private static AIFunctionArguments Args() => new(new Dictionary<string, object?>
    {
        ["agentName"] = "AgentB",
        ["task"] = "do work"
    });

    /// <summary>
    /// REPRO — sub-thread drain must not capture the caller's SynchronizationContext.
    ///
    /// We invoke the tool on a single-threaded pump that models the Orleans grain
    /// scheduler. Inside the sub-thread's enumeration body, we record whether the
    /// current thread is a ThreadPool thread.
    ///
    /// Today (buggy): the `async IAsyncEnumerable` shape runs enumeration
    /// continuations on the caller's pump → <c>IsThreadPoolThread</c> is false. Under
    /// Orleans, that's the deadlock.
    ///
    /// After fix (Task.Run + ConfigureAwait(false)): the drain runs on ThreadPool →
    /// <c>IsThreadPoolThread</c> is true, and the grain scheduler stays free.
    /// </summary>
    [Fact]
    public async Task DelegationTool_SubthreadDrain_MustRunOnThreadPool_NotCallerContext()
    {
        var ct = TestContext.Current.CancellationToken;
        using var pump = new SingleThreadSyncContext();

        var enumerationOnThreadPool = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<string> Execute(
            string agentName, string task, string? context,
            [EnumeratorCancellation] CancellationToken innerCt)
        {
            // Record where the enumeration is actually running. This is the code path
            // that, under Orleans, would post UpdateThreadMessageContent back through
            // the parent hub — if it runs on the grain scheduler, we deadlock.
            enumerationOnThreadPool.TrySetResult(
                System.Threading.Thread.CurrentThread.IsThreadPoolThread);
            yield return "done";
            await Task.CompletedTask;
        }

        var tool = CreateTool(Execute);

        // Invoke the tool on the pump — this is how FunctionInvokingChatClient
        // would invoke it from a grain message handler.
        await pump.RunAsync(() => tool.InvokeAsync(Args(), ct).AsTask())
            .WaitAsync(10.Seconds(), ct);

        var onThreadPool = await enumerationOnThreadPool.Task.WaitAsync(5.Seconds(), ct);
        onThreadPool.Should().BeTrue(
            "sub-thread drain must run on ThreadPool, not the caller's " +
            "SynchronizationContext. With the old `async IAsyncEnumerable` shape, " +
            "FunctionInvokingChatClient captured the grain scheduler on every " +
            "iteration, wedging it when sub-thread continuations posted back " +
            "through the same scheduler. The fix uses Task.Run + ConfigureAwait(false) " +
            "so the drain never touches the caller's scheduler.");
    }

    /// <summary>
    /// Sanity: the full sub-thread text must still arrive as the tool result.
    /// This is what the parent LLM's follow-up turn consumes.
    /// </summary>
    [Fact]
    public async Task DelegationTool_ToolResult_AggregatesAllSubthreadChunks()
    {
        var ct = TestContext.Current.CancellationToken;

        async IAsyncEnumerable<string> Execute(
            string agentName, string task, string? context,
            [EnumeratorCancellation] CancellationToken innerCt)
        {
            yield return "Hello, ";
            await Task.Yield();
            yield return "world!";
        }

        var tool = CreateTool(Execute);

        var result = await tool.InvokeAsync(Args(), ct).AsTask().WaitAsync(10.Seconds(), ct);
        result?.ToString().Should().Contain("Hello, ").And.Contain("world!",
            "the tool must return the aggregated sub-thread text so the parent LLM " +
            "can reason over the delegation result in its next turn.");
    }

    /// <summary>
    /// Single-threaded synchronization context that serializes all continuations
    /// onto one thread — models an Orleans grain scheduler / message hub pump.
    /// </summary>
    private sealed class SingleThreadSyncContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback cb, object? state)> queue = new();
        private readonly System.Threading.Thread pumpThread;

        public SingleThreadSyncContext()
        {
            pumpThread = new System.Threading.Thread(Loop) { IsBackground = true, Name = "DelegationTest.Pump" };
            pumpThread.Start();
        }

        private void Loop()
        {
            SetSynchronizationContext(this);
            foreach (var item in queue.GetConsumingEnumerable())
            {
                try { item.cb(item.state); }
                catch { /* swallow — callbacks carry their own error plumbing via TCS */ }
            }
        }

        public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));

        public Task RunAsync(Func<Task> work)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { await work(); tcs.TrySetResult(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }, null);
            return tcs.Task;
        }

        public void Dispose() => queue.CompleteAdding();
    }
}
