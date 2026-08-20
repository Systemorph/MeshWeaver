using System.Collections.Immutable;
using MeshWeaver.Layout;

namespace MeshWeaver.AI;

/// <summary>
/// What a finished agent round is allowed to CLAIM about itself.
/// </summary>
/// <remarks>
/// <para>🚨 The invariant this type exists to enforce: <b>a round may report
/// <see cref="ThreadMessageStatus.Completed"/> only if it actually produced what
/// Completed asserts</b> — every dispatched tool call returned, and the model wrote a
/// closing answer. Anything else must terminate in a state that NAMES what happened.</para>
///
/// <para>Before this existed, the round asserted success <i>by default</i> rather than
/// <i>from evidence</i>, in three places that all defaulted the same way:
/// <see cref="ToolCallEntry.Status"/> defaults to <see cref="ToolCallStatus.Success"/>,
/// <see cref="ToolCallEntry.IsSuccess"/> defaults to <c>true</c>, and
/// <c>ThreadMessage.Status</c> defaults to <see cref="ThreadMessageStatus.Completed"/>.
/// So "no evidence of failure" was persisted as a positive success claim — which is how
/// #1689 (a round that lost its tool execution and reported fabricated success) and #1715
/// (a silent zero-output closing turn that ended <c>Completed</c> with no final answer)
/// are the SAME defect seen from two sides.</para>
/// </remarks>
public enum RoundVerdict
{
    /// <summary>Every dispatched tool call returned, and the model wrote a closing answer.
    /// The only verdict that may persist as <see cref="ThreadMessageStatus.Completed"/>.</summary>
    Answered,

    /// <summary>At least one tool call was dispatched and never returned a result: the
    /// stream ended while the call was still pending. The round's text may narrate work
    /// that never happened (#1689).</summary>
    ToolCallUnfinished,

    /// <summary>The round produced no content at all — no text, no tool calls. The model
    /// stream completed with zero tokens.</summary>
    NoOutput,

    /// <summary>Tool calls ran, but the CLOSING model turn — the one that receives the last
    /// tool result and should write the answer — produced nothing. Whatever text is
    /// persisted is the abandoned mid-round fragment, not an answer (#1715).</summary>
    NoFinalAnswer
}

/// <summary>
/// The evidence-derived conclusion of one agent round: what it may claim, the tool-call log
/// with unfinished calls stamped honestly, and a one-line diagnosis when it may not claim
/// success.
/// </summary>
/// <param name="Verdict">What the round actually produced.</param>
/// <param name="ToolCalls">The round's tool calls, with any call that never returned
/// re-stamped <see cref="ToolCallStatus.Failed"/> / <c>IsSuccess = false</c> instead of the
/// record's default <see cref="ToolCallStatus.Success"/>. <see cref="ToolCallEntry.Result"/> is
/// deliberately left untouched — see the remark in <see cref="RoundOutcome.Classify"/>.</param>
/// <param name="Diagnosis">Null when <see cref="Verdict"/> is
/// <see cref="RoundVerdict.Answered"/>; otherwise the sentence that names what happened,
/// used for both the response cell's text and the thread Summary.</param>
public sealed record RoundConclusion(
    RoundVerdict Verdict,
    ImmutableList<ToolCallEntry> ToolCalls,
    string? Diagnosis)
{
    /// <summary>True only for <see cref="RoundVerdict.Answered"/> — the single case in
    /// which the round genuinely did what a completed round asserts. Also what a delegating
    /// parent is told, so a parent never records a sub-round's silence as a success.</summary>
    public bool IsHonestCompletion => Verdict == RoundVerdict.Answered;

    /// <summary>The cell status this conclusion permits:
    /// <see cref="ThreadMessageStatus.Completed"/> for <see cref="RoundVerdict.Answered"/>,
    /// <see cref="ThreadMessageStatus.Error"/> for everything else.</summary>
    public ThreadMessageStatus Status =>
        IsHonestCompletion ? ThreadMessageStatus.Completed : ThreadMessageStatus.Error;
}

/// <summary>
/// Classifies a finished round from its own evidence. Pure and deterministic — no hub, no
/// clock, no I/O — so the invariant is unit-testable without a mesh, and so the classification
/// cannot drift between the streaming path and whatever reads the persisted cell.
/// </summary>
public static class RoundOutcome
{
    /// <summary>
    /// A tool call the round DISPATCHED but never got a result for.
    /// </summary>
    /// <remarks>
    /// Deliberately the same predicate the live UI already uses for "dispatched, result not
    /// back yet" (<see cref="ToolCallVisibility.IsPending"/>): default status + null result.
    /// Reusing it rather than inventing a second notion of "pending" is what keeps a
    /// mid-flight delegation (<see cref="ToolCallStatus.Streaming"/>, carrying a live progress
    /// projection in <see cref="ToolCallEntry.Result"/>) and an already-terminal
    /// <see cref="ToolCallStatus.Failed"/>/<see cref="ToolCallStatus.Cancelled"/> call out of
    /// the unfinished set.
    /// </remarks>
    public static bool IsUnfinished(ToolCallEntry call) => ToolCallVisibility.IsPending(call);

    /// <summary>
    /// Derives what the round may claim.
    /// </summary>
    /// <param name="finalText">The round's accumulated response text at stream end.</param>
    /// <param name="toolCalls">The round's tool-call log at stream end.</param>
    /// <param name="producedClosingText">Whether the model appended text AFTER its last tool
    /// result. With no tool results this is trivially true — the caller seeds the
    /// "text length at last tool result" watermark below zero — so the
    /// <see cref="RoundVerdict.NoFinalAnswer"/> rule only ever fires on a round that actually
    /// ran tools and then went silent.</param>
    /// <remarks>
    /// Rule order matters and is itself the diagnosis: an unfinished tool call is reported
    /// ahead of a missing answer, because a round that lost a tool call has no answer to
    /// miss and the tool call is the actionable fact.
    /// </remarks>
    public static RoundConclusion Classify(
        string? finalText,
        ImmutableList<ToolCallEntry> toolCalls,
        bool producedClosingText)
    {
        ArgumentNullException.ThrowIfNull(toolCalls);

        var unfinished = toolCalls.Where(IsUnfinished).ToList();
        if (unfinished.Count > 0)
        {
            // Stamp the unfinished calls Failed so the persisted record stops asserting a
            // success that never happened. Without this the entry keeps the type's default
            // Status = Success with a null Result — indistinguishable, to the UI and to any
            // monitoring reading the thread, from a tool call that ran fine.
            //
            // 🚨 Status and IsSuccess ONLY — never a synthetic Result. The cell write merges
            // this log with the cell's current ToolCalls, and that merge prefers whichever
            // side carries a Result (ThreadExecution.MergeToolCallEntries). A placeholder
            // string here would therefore CLOBBER a real terminal result that reached the
            // cell through the delegation stamp but not this in-memory log. Leaving Result
            // null keeps the merge's "cur wins" branch reachable, so the honest signal is
            // added without ever overwriting evidence. The WHY is carried by the round's
            // diagnosis line, which names the tools.
            var stamped = toolCalls
                .Select(c => IsUnfinished(c)
                    ? c with { Status = ToolCallStatus.Failed, IsSuccess = false }
                    : c)
                .ToImmutableList();
            var names = string.Join(", ", unfinished.Select(c => c.Name).Distinct());
            return new RoundConclusion(
                RoundVerdict.ToolCallUnfinished, stamped,
                $"Round ended with {unfinished.Count} tool call(s) still unfinished ({names}). "
                + "The response above may describe work that was never carried out — re-run it.");
        }

        if (string.IsNullOrEmpty(finalText) && toolCalls.IsEmpty)
            return new RoundConclusion(
                RoundVerdict.NoOutput, toolCalls,
                "The model returned no response — the stream completed with zero tokens.");

        if (!producedClosingText)
            return new RoundConclusion(
                RoundVerdict.NoFinalAnswer, toolCalls,
                "The model ran its tools but never wrote a closing answer — the final turn "
                + "produced no content. The text above is the unfinished mid-round fragment.");

        return new RoundConclusion(RoundVerdict.Answered, toolCalls, null);
    }
}
