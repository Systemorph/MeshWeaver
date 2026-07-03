using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// LIVE-mode adapter for <see cref="PromptCell"/>: wires the cell's
/// <c>Responder</c> contract to a REAL agent thread. The prompt is submitted
/// through the canonical <see cref="HubThreadExtensions.StartThread"/> surface
/// (agent <see cref="AgentName"/> — the dedicated in-course demo agent), the
/// reply is observed via <see cref="ThreadFlow.ObserveResponses"/> (the standard
/// mesh-node-stream observation — no polling), and projected into the cell's
/// <see cref="PromptCellResponse"/> shape (first fenced code block → code pane,
/// remainder → output pane).
/// <para>The Simulated mode needs none of this — <see cref="PromptCell.Simulated"/>
/// lives in MeshWeaver.Layout with no AI reference. This adapter is the thin
/// LIVE half, kept here because it needs the thread-submission surface.</para>
/// </summary>
public static class TrainingSimResponder
{
    /// <summary>The dedicated training agent (Data/Agent/TrainingSim.md).</summary>
    public const string AgentName = "TrainingSim";

    /// <summary>
    /// Upper bound on one live round. When it trips, the failure is SURFACED to
    /// the cell's output pane ("could not answer") — a graceful sink, never a
    /// silent spinner.
    /// </summary>
    public static readonly TimeSpan RoundTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Builds a LIVE responder: each submitted prompt starts a thread under
    /// <paramref name="namespacePath"/> with the training agent and completes
    /// with the projected reply. Errors (thread refused, round failed, timeout)
    /// propagate as OnError — <see cref="PromptCell"/> renders them in the
    /// exchange pane.
    /// </summary>
    /// <param name="hub">The hub used to submit and observe (client-side hub).</param>
    /// <param name="namespacePath">The exercise namespace the thread anchors under.</param>
    /// <param name="agentName">Override for the agent (default <see cref="AgentName"/>).</param>
    /// <param name="contextPath">Optional context node the agent reads.</param>
    public static Func<string, IObservable<PromptCellResponse>> Live(
        IMessageHub hub,
        string namespacePath,
        string? agentName = null,
        string? contextPath = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(namespacePath))
            throw new ArgumentException("namespacePath is required", nameof(namespacePath));
        var agent = string.IsNullOrEmpty(agentName) ? AgentName : agentName!;

        return prompt => Observable.Create<PromptCellResponse>(observer =>
        {
            var replySubscription = new SerialDisposable();
            hub.StartThread(
                namespacePath,
                prompt,
                agentName: agent,
                contextPath: contextPath,
                onCreated: node => replySubscription.Disposable =
                    ThreadFlow.ObserveResponses(hub, node.Path)
                        .Take(1)
                        .Timeout(RoundTimeout)
                        .Subscribe(
                            reply =>
                            {
                                observer.OnNext(Project(reply.Message));
                                observer.OnCompleted();
                            },
                            observer.OnError),
                onError: error => observer.OnError(new InvalidOperationException(error)));
            return replySubscription;
        });
    }

    /// <summary>
    /// Projects an assistant reply into the cell's three-pane shape: the FIRST
    /// fenced code block becomes the code pane; everything else renders as the
    /// output markdown. A reply without a fence yields an empty code pane.
    /// </summary>
    public static PromptCellResponse Project(ThreadMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var text = message.Text ?? "";
        var (code, remainder) = ExtractFirstCodeFence(text);
        var output = string.IsNullOrWhiteSpace(remainder)
            ? Controls.Markdown("_(no further output)_")
            : Controls.Markdown(remainder);
        return new PromptCellResponse(code, output);
    }

    /// <summary>
    /// Splits <paramref name="text"/> at its first triple-backtick fence:
    /// returns the fence BODY (without the fence lines / language tag) and the
    /// text with that fence removed. No fence → ("", text).
    /// </summary>
    internal static (string Code, string Remainder) ExtractFirstCodeFence(string text)
    {
        const string fence = "```";
        var open = text.IndexOf(fence, StringComparison.Ordinal);
        if (open < 0)
            return ("", text);
        var bodyStart = text.IndexOf('\n', open);
        if (bodyStart < 0)
            return ("", text);
        bodyStart++; // past the "```lang" line
        var close = text.IndexOf(fence, bodyStart, StringComparison.Ordinal);
        if (close < 0)
            return ("", text);
        var code = text[bodyStart..close].TrimEnd('\n', '\r');
        var closeEnd = close + fence.Length;
        var remainder = (text[..open] + text[closeEnd..]).Trim();
        return (code, remainder);
    }
}
