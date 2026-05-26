using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI;

/// <summary>
/// Reactive read-side primitives for observing a thread chat flow from any
/// CLIENT — Blazor side panel, MCP clients, tests. Writes go through the
/// canonical <see cref="WorkspaceThreadExtensions"/> surface
/// (<c>workspace.SubmitMessage</c>, <c>workspace.StartThread</c>, etc.);
/// this class only exposes the matching read-side primitives:
/// <list type="bullet">
///   <item><c>workspace.GetMeshNodeStream(path)</c> — the cache-routed,
///         write-coherent thread stream</item>
///   <item><see cref="SubmitAndWait"/> — convenience composition of
///         <see cref="WorkspaceThreadExtensions.SubmitMessage"/> + a wait
///         for the round to land</item>
/// </list>
///
/// <para><b>All methods return <see cref="IObservable{T}"/>.</b> Per
/// <c>Doc/Architecture/AsynchronousCalls.md</c>: no <c>Task&lt;T&gt;</c> on the
/// public surface, no <c>async</c>/<c>await</c> in mesh-reachable code. Tests
/// bridge to <c>Task</c> at their edge via
/// <c>.FirstAsync().ToTask(ct)</c>.</para>
///
/// <para>Always invoked on the CLIENT workspace — the per-thread hub's own
/// workspace would block on the active submission handler.</para>
/// </summary>
public static class ThreadFlow
{
    /// <summary>
    /// Live observable of the <see cref="MeshThread"/> at
    /// <paramref name="threadPath"/>. Subscribes via the cache-routed
    /// <c>workspace.GetMeshNodeStream(path)</c> primitive (same handle every
    /// reader/writer shares — write-coherent with the GUI and other clients).
    /// Filters out empty / null-content emissions so subscribers only see
    /// real thread state.
    /// </summary>
    public static IObservable<MeshThread> ObserveThread(
        IMessageHub client, string threadPath) =>
        client.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t != null)
            .Select(t => t!);

    /// <summary>
    /// Live observable of <see cref="MeshThread.Messages"/> — the message
    /// id list as the thread evolves. Equivalent to the GUI's data-binding
    /// subscription. Stays subscribed.
    /// </summary>
    public static IObservable<IReadOnlyList<string>> ObserveMessages(
        IMessageHub client, string threadPath) =>
        ObserveThread(client, threadPath)
            .Select(t => (IReadOnlyList<string>)t.Messages);

    /// <summary>
    /// Live read of the <see cref="ThreadMessage"/> at
    /// <c>{threadPath}/{msgId}</c>. Subscribes to the message satellite's
    /// remote stream and waits until <paramref name="predicate"/> holds
    /// (default: <c>Text</c> non-empty) then completes.
    /// </summary>
    public static IObservable<ThreadMessage> ReadMessage(
        IMessageHub client, string threadPath, string msgId,
        Func<ThreadMessage, bool>? predicate = null,
        TimeSpan? timeout = null)
    {
        predicate ??= m => !string.IsNullOrEmpty(m.Text);
        timeout ??= TimeSpan.FromSeconds(15);
        return client.GetWorkspace().GetMeshNodeStream($"{threadPath}/{msgId}")
            .Select(n => n.Content as ThreadMessage)
            .Where(m => m != null && predicate(m))
            .Take(1)
            .Timeout(timeout.Value)
            .Select(m => m!);
    }

    /// <summary>
    /// Waits for the thread to satisfy <paramref name="predicate"/>, then
    /// completes with the matching thread snapshot. Same remote-stream
    /// primitive as <see cref="ObserveThread"/>.
    /// </summary>
    public static IObservable<MeshThread> ReadThread(
        IMessageHub client, string threadPath,
        Func<MeshThread, bool>? predicate = null,
        TimeSpan? timeout = null)
    {
        predicate ??= _ => true;
        timeout ??= TimeSpan.FromSeconds(30);
        return ObserveThread(client, threadPath)
            .Where(t => predicate(t))
            .Take(1)
            .Timeout(timeout.Value);
    }

    /// <summary>
    /// Submits via the GUI path then emits exactly once when the resulting
    /// round completes — the response message id (last entry of
    /// <c>Thread.Messages</c> after <c>IsExecuting</c> flips back to false
    /// AND <c>Messages.Count</c> grew past the pre-submit baseline).
    ///
    /// <para>Reactive end-to-end. Captures baseline from the thread stream
    /// once (<c>.Take(1)</c>), fires Submit inside <c>SelectMany</c>, then
    /// waits on the SAME stream for the next Idle frame whose count grew.
    /// The whole chain is one observable — no <c>Task</c>, no <c>await</c>,
    /// no scheduler bridge. Works for first-submit (baseline=0, count→2)
    /// AND subsequent submits on an existing thread (baseline=N, count→N+2).</para>
    /// </summary>
    public static IObservable<string> SubmitAndWait(
        IMessageHub client, string threadPath, string userText,
        string? contextPath = null, string? agentName = null, string? modelName = null,
        TimeSpan? timeout = null) => Observable.Defer(() =>
        {
            timeout ??= TimeSpan.FromSeconds(30);
            var thread = ObserveThread(client, threadPath);

            return thread
                .Select(t => t.Messages.Count)
                .Take(1)
                .Timeout(timeout.Value)
                .SelectMany(baseline =>
                {
                    client.SubmitMessage(
                        threadPath, userText,
                        agentName: agentName, modelName: modelName, contextPath: contextPath);
                    return thread
                        .Where(t => !t.IsExecuting && t.Messages.Count > baseline)
                        .Select(t => t.Messages[^1])
                        .Take(1)
                        .Timeout(timeout.Value);
                });
        });
}
