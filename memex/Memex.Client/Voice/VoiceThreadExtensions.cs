using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshThread = MeshWeaver.AI.Thread;

namespace Memex.Client.Voice;

/// <summary>
/// Voice ↔ thread bridge, built ONLY on the canonical static thread surface
/// (<see cref="HubThreadExtensions"/>): the first utterance opens a thread
/// (<c>StartThread</c>); each later utterance feeds the thread's inbox
/// (<c>SubmitMessage</c> → <see cref="MeshThread.PendingUserMessages"/>, drained by the
/// submission watcher). Replies are observed reactively off the thread node's stream.
/// </summary>
public static class VoiceThreadExtensions
{
    /// <summary>
    /// First utterance ⇒ open a new thread; later utterances ⇒ feed its inbox. The trigger.
    /// </summary>
    /// <param name="threadPath">Null/empty ⇒ start a new thread; otherwise submit into it.</param>
    /// <param name="onThreadOpened">Fires once with the new thread's path (subscribe to replies then).</param>
    public static void SpeakToThread(
        this IMessageHub hub,
        string? threadPath,
        string namespacePath,
        string text,
        string? agentName,
        Action<string> onThreadOpened,
        Action<string> onError)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (string.IsNullOrEmpty(threadPath))
            hub.StartThread(namespacePath, text, agentName: agentName,
                onCreated: node => onThreadOpened(node.Path), onError: onError);
        else
            hub.SubmitMessage(threadPath, text, agentName: agentName, onError: onError);
    }

    /// <summary>
    /// Reactive stream of the agent's latest assistant reply text — one per completed round.
    /// Reads the last message cell once the round leaves <c>Executing</c>. Long-lived; the caller
    /// disposes the subscription.
    /// </summary>
    public static IObservable<string> ObserveAssistantReplies(this IMessageHub hub, string threadPath)
    {
        var workspace = hub.GetWorkspace();
        return workspace.GetMeshNodeStream(threadPath)
            .Select(node => node.Content as MeshThread)
            .Where(t => t is { IsExecuting: false } && t.Messages.Count > 0)
            .Select(t => t!.Messages[^1])
            .DistinctUntilChanged()
            .SelectMany(lastId => workspace.GetMeshNodeStream($"{threadPath}/{lastId}")
                .Select(cell => cell.Content as ThreadMessage)
                .Where(m => m is { Role: "assistant" } && !string.IsNullOrWhiteSpace(m.Text))
                .Select(m => m!.Text!)
                .Take(1));
    }
}
