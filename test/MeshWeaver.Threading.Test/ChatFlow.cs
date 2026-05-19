using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Test-side mirror of the Blazor chat flow. Every method goes through the
/// same handlers the GUI uses:
/// <list type="bullet">
///   <item><see cref="ThreadSubmission.Submit"/> + <see cref="ThreadSubmission.CreateThreadAndSubmit"/></item>
///   <item><c>client.GetWorkspace().GetMeshNodeStream(path)</c> — the remote-stream-cache-backed reactive handle (no <c>GetDataRequest</c> polling, no ad-hoc <c>GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c>)</item>
/// </list>
/// Always invoked on the CLIENT workspace — the per-thread hub's own workspace
/// would block on the active submission handler.
/// </summary>
public static class ChatFlow
{
    /// <summary>
    /// Submits a user message via <see cref="ThreadSubmission.Submit"/> (the GUI path)
    /// and waits for execution to complete. Returns the response message id —
    /// the last entry of <c>Thread.Messages</c> after <c>IsExecuting</c> flips back to false.
    /// </summary>
    public static async Task<string> SubmitAndWaitAsync(
        IMessageHub client, string threadPath, string userText,
        string? contextPath = null, string? agentName = null, string? modelName = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(30);
        var workspace = client.GetWorkspace();
        var threadStream = workspace.GetMeshNodeStream(threadPath);

        var baselineCount = await threadStream
            .Select(n => (n.Content as MeshThread)?.Messages.Count ?? 0)
            .Take(1)
            .Timeout(timeout.Value)
            .ToTask(ct);

        var executionComplete = threadStream
            .Select(n => n.Content as MeshThread)
            .Where(t => t is { IsExecuting: false } && t.Messages.Count > baselineCount)
            .Select(t => t!.Messages[^1])
            .Take(1)
            .Timeout(timeout.Value)
            .ToTask(ct);

        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client,
            ThreadPath = threadPath,
            UserText = userText,
            ContextPath = contextPath,
            AgentName = agentName,
            ModelName = modelName,
        });

        return await executionComplete;
    }

    /// <summary>
    /// Live read of the <see cref="ThreadMessage"/> at
    /// <c>{threadPath}/{msgId}</c> via
    /// <c>workspace.GetMeshNodeStream(path)</c>. Subscribes to the
    /// remote-stream-cache-backed handle and waits until <paramref name="predicate"/>
    /// holds (default: <c>Text</c> non-empty).
    /// </summary>
    public static Task<ThreadMessage> ReadMessageAsync(
        IMessageHub client, string threadPath, string msgId,
        Func<ThreadMessage, bool>? predicate = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        predicate ??= m => !string.IsNullOrEmpty(m.Text);
        timeout ??= TimeSpan.FromSeconds(15);
        return client.GetWorkspace().GetMeshNodeStream($"{threadPath}/{msgId}")
            .Select(n => n.Content as ThreadMessage)
            .Where(m => m != null && predicate(m))
            .Take(1)
            .Timeout(timeout.Value)
            .ToTask(ct)!;
    }

    /// <summary>
    /// Live read of the <see cref="MeshThread"/> at <paramref name="threadPath"/>
    /// via <c>workspace.GetMeshNodeStream(path)</c>. Same primitive the GUI uses
    /// for thread-level data binding.
    /// </summary>
    public static Task<MeshThread> ReadThreadAsync(
        IMessageHub client, string threadPath,
        Func<MeshThread, bool>? predicate = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        predicate ??= _ => true;
        timeout ??= TimeSpan.FromSeconds(10);
        return client.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t != null && predicate(t))
            .Take(1)
            .Timeout(timeout.Value)
            .ToTask(ct)!;
    }

    /// <summary>
    /// Live observable of <c>Thread.Messages</c> for stream-based assertions
    /// (e.g. <c>.Where(ids => ids.Count >= 2).FirstAsync()</c>). Equivalent to the
    /// GUI's data-binding subscription.
    /// </summary>
    public static IObservable<System.Collections.Generic.IReadOnlyList<string>> ObserveMessages(
        IMessageHub client, string threadPath) =>
        client.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => (System.Collections.Generic.IReadOnlyList<string>)
                ((n.Content as MeshThread)?.Messages ?? []));
}
