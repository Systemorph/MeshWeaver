using System.ComponentModel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// The <c>submit_message</c> tool: send a message to ANOTHER thread — one an agent did not
/// dispatch itself. Sub-threads are reachable with <c>send_to_sub_thread</c>; this is the general
/// case, and it is how one conversation reaches another (a colleague's thread, a long-running
/// operation's thread, a thread a human left open).
///
/// <para><b>There is no new wire message and no bespoke request type</b>, deliberately: the
/// submission surface already exists as <see cref="HubThreadExtensions.SubmitMessage"/>, which
/// appends to the target thread's <c>PendingUserMessages</c> through the ONE mutation API
/// (<c>GetMeshNodeStream(threadPath).Update</c>, routed to the owning per-thread hub). The handler
/// on the thread side is likewise already there — the per-thread submission watcher reacts to that
/// state change, drains the queue into <c>Messages</c>, allocates a response cell and runs a round.
/// An idle thread therefore WAKES on delivery, and a running one picks the message up at its next
/// <c>check_inbox</c>. Adding a request/response pair here would duplicate that machinery and is
/// what AGENTS.md means by "delete the request type".</para>
///
/// <para><b>Identity and permission are not this tool's to decide.</b> The call runs under the
/// caller's <c>AccessContext</c> (the factory restores it around every tool invocation), and the
/// cross-hub write is access-checked by the owning thread's hub exactly as any other write is. An
/// agent can therefore reach precisely the threads its user can — no more.</para>
/// </summary>
public static class ThreadMessageTool
{
    /// <summary>Creates the <c>submit_message</c> AITool.</summary>
    /// <param name="hub">The hub the tool posts from.</param>
    /// <param name="chat">The chat whose execution context identifies the calling thread.</param>
    public static AITool Create(IMessageHub hub, IAgentChat chat)
    {
        string SubmitMessage(
            [Description("Path of the thread to send to, e.g. 'alice/_Thread/quarterly-review'. Must be an existing thread node you have write access to.")]
            string threadPath,
            [Description("The message text to deliver, written for a reader with none of your context: say what you need and why, and name the paths involved.")]
            string text)
        {
            if (string.IsNullOrWhiteSpace(threadPath))
                return "submit_message requires a threadPath — the path of an existing thread node.";
            if (string.IsNullOrWhiteSpace(text))
                return "submit_message requires non-empty text; nothing was sent.";

            // Normalise FIRST, then validate the NORMALISED value. `/`, `@/` and `///` all survive
            // the whitespace check above and normalise to empty — and MeshNode.FromPath("") throws
            // ArgumentException, which aborts the whole round instead of returning a tool result
            // the model can act on. A tool must answer, never explode.
            var target = threadPath.Trim().TrimStart('@').Trim('/');
            if (target.Length == 0)
                return "submit_message requires a real thread path such as "
                       + "'alice/_Thread/quarterly-review'; that value normalises to nothing.";

            // Structural guard with a speaking answer, BEFORE the self-send comparison: a path is
            // checked for being well-formed before it is compared to anything. SubmitMessage
            // refuses an ownerless top-level `_Thread/{id}` (no partition, no per-node hub — the
            // cross-hub write would NotFound-storm the router); saying so beats the agent retrying
            // the same bad path.
            if (ActivityNodeGuard.IsOwnerless(MeshNode.FromPath(target), out var reason))
                return $"'{target}' is not a valid thread path: {reason}. "
                       + "A thread lives at {owner}/_Thread/{id}.";

            // Refuse the self-send rather than let an agent queue work onto its own round: the
            // message would be drained by the watcher into the conversation the agent is already
            // in, which reads as a loop and is never what the caller meant.
            var own = chat.ExecutionContext?.ThreadPath?.Trim('/');
            if (!string.IsNullOrEmpty(own)
                && string.Equals(own, target, StringComparison.OrdinalIgnoreCase))
                return "submit_message targets ANOTHER thread — this is the thread you are already in. "
                       + "Just answer here, or use delegate_to_agent to open a sub-thread.";

            string? failure = null;
            hub.SubmitMessage(target, text, onError: e => failure = e);
            return failure is not null
                ? $"Message NOT delivered to {target}: {failure}"
                : $"Message queued to {target}. An idle thread starts a round; a running one picks "
                  + "it up at its next check_inbox. You are not notified of the reply — read the "
                  + "thread when you need it.";
        }

        return AIFunctionFactory.Create(
            SubmitMessage,
            name: "submit_message",
            description:
                "Send a message to ANOTHER thread (not the one you are in, and not one you "
                + "dispatched — use send_to_sub_thread for your own sub-threads). The message is "
                + "queued on that thread: if it is idle it starts a round, if it is running it "
                + "receives the message at its next inbox check. Delivery is fire-and-forget — you "
                + "get no reply here. Requires write access to the target thread.");
    }
}
