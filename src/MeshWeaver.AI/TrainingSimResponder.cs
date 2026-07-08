using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

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
            // 🚨 DEGRADE GRACEFULLY when no language model is configured. Submitting a doomed round
            // would surface the raw factory error ("ApiKey is missing for model 'glm-5.2'. Configure
            // a ModelProvider node …") inline in the training cell — hostile on a course page whose
            // point is to DEMONSTRATE the one-prompt-in / one-cell-out shape, not to debug config.
            // The credential resolver already answers "is any model available?" reactively from its
            // warm catalog snapshot (ResolveDefaultModelId → null when none resolves); when there is
            // none, emit the calm notice instead of starting a thread. NOT a fabricated model / hard-
            // coded key — the cell just says "configure a model to run this" and stays inert.
            if (!IsAnyModelConfigured(hub))
            {
                observer.OnNext(NoModelNotice());
                observer.OnCompleted();
                return Disposable.Empty;
            }

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
                                // A round that RAN but whose reply IS the model-config error (the
                                // factory failure surfaced as the assistant's text, not as an
                                // OnError) must also degrade to the calm notice rather than echo the
                                // raw "ApiKey is missing" into the cell.
                                observer.OnNext(reply.Message is null || LooksLikeMissingModel(reply.Message.Text)
                                    ? NoModelNotice()
                                    : Project(reply.Message));
                                observer.OnCompleted();
                            },
                            error =>
                            {
                                // A round that ERRORED with the model-config failure degrades too;
                                // any other error still surfaces to the cell's exchange pane.
                                if (LooksLikeMissingModel(error.Message))
                                {
                                    observer.OnNext(NoModelNotice());
                                    observer.OnCompleted();
                                }
                                else
                                    observer.OnError(error);
                            }),
                onError: error => observer.OnError(new InvalidOperationException(error)));
            return replySubscription;
        });
    }

    /// <summary>
    /// The calm inline notice a training cell shows when no usable language model is configured —
    /// empty code pane, a friendly explanation in the output pane. Keeps the one-prompt-in /
    /// one-result-out SHAPE (a code pane + an output pane) so the cell still reads as a demo,
    /// without a fabricated model or a hard-coded key.
    /// </summary>
    public static PromptCellResponse NoModelNotice() =>
        new(string.Empty, Controls.Markdown(
            "▶ **Live agent demo** — configure a language model to run this. "
            + "_(This cell shows how an agent turns your prompt into one code cell.)_"));

    /// <summary>
    /// Reactive "is any model available?" check for the LIVE responder, off the same warm catalog
    /// snapshot the credential resolver maintains: <c>ResolveDefaultModelId()</c> returns the lowest-
    /// Order LanguageModel whose credentials actually resolve, or <c>null</c> when none does. When the
    /// resolver isn't registered (a hub without the AI stack), assume a model MIGHT exist and let the
    /// round proceed — the reply-text / OnError classification below still catches a config failure.
    /// </summary>
    private static bool IsAnyModelConfigured(IMessageHub hub)
    {
        var resolver = hub.ServiceProvider.GetService<ChatClientCredentialResolver>();
        return resolver is null || resolver.ResolveDefaultModelId() is not null;
    }

    /// <summary>
    /// True when <paramref name="text"/> carries the signature of a missing-model / model-creation
    /// failure — the factory's "ApiKey is missing …" / "Configure a ModelProvider …" error, or the
    /// agent client's "No AI model is available …" banner. Used to convert a raw config error (whether
    /// it arrived as the reply text or as an OnError) into the calm <see cref="NoModelNotice"/> instead
    /// of echoing it into a course cell. Deliberately narrow: only the model-config signatures, so a
    /// genuine agent answer or a real runtime error is never masked.
    /// </summary>
    internal static bool LooksLikeMissingModel(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("ApiKey is missing", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Configure a ModelProvider", StringComparison.OrdinalIgnoreCase)
               || text.Contains("No AI model is available", StringComparison.OrdinalIgnoreCase)
               || text.Contains("creating it failed via factory", StringComparison.OrdinalIgnoreCase);
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
