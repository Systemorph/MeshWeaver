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

    /// <summary>At least one tool call was still un-terminated when the stream ended: it was
    /// dispatched and never returned, or a delegation was still mid-flight. The round's text may
    /// narrate work that never happened (#1689).</summary>
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
/// with unfinished calls stamped honestly, and the ingredients for a localized diagnosis when it
/// may not claim success.
/// </summary>
/// <param name="Verdict">What the round actually produced.</param>
/// <param name="ToolCalls">The round's tool calls, with any call that was DISPATCHED AND NEVER
/// RETURNED re-stamped <see cref="ToolCallStatus.Failed"/> / <c>IsSuccess = false</c> instead of
/// the record's default <see cref="ToolCallStatus.Success"/>. See
/// <see cref="RoundOutcome.Classify"/> for what is deliberately NOT re-stamped.</param>
/// <param name="UnfinishedToolNames">Distinct names of the tool calls that had not terminated
/// when the stream ended — the argument the diagnosis is built from, so it is actionable.</param>
public sealed record RoundConclusion(
    RoundVerdict Verdict,
    ImmutableList<ToolCallEntry> ToolCalls,
    ImmutableList<string> UnfinishedToolNames)
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

    /// <summary>
    /// The string-catalog key for the user-facing diagnosis, or null when the round answered.
    /// </summary>
    /// <remarks>
    /// 🌍 The classifier stays LANGUAGE-NEUTRAL: it decides, and names the arguments; the caller
    /// renders the sentence off the round's own <c>AccessContext.Locale</c> — the same explicit-locale
    /// rule the other terminal-error paths follow (never ambient <c>CultureInfo</c>: a round hops
    /// schedulers). Hard-coding English here would have handed a German viewer an English error on
    /// exactly the paths this PR adds.
    /// </remarks>
    public string? LocalizationKey => Verdict switch
    {
        RoundVerdict.ToolCallUnfinished => "chat.roundToolCallUnfinished",
        RoundVerdict.NoOutput => "chat.roundNoOutput",
        RoundVerdict.NoFinalAnswer => "chat.roundNoFinalAnswer",
        _ => null
    };

    /// <summary>Arguments for <see cref="LocalizationKey"/>'s placeholders: the unfinished-call
    /// count and their comma-joined names.</summary>
    public object?[] LocalizationArgs =>
        Verdict == RoundVerdict.ToolCallUnfinished
            ? [UnfinishedToolNames.Count, string.Join(", ", UnfinishedToolNames)]
            : [];
}

/// <summary>
/// Classifies a finished round from its own evidence. Pure and deterministic — no hub, no
/// clock, no I/O — so the invariant is unit-testable without a mesh, and so the classification
/// cannot drift between the streaming path and whatever reads the persisted cell.
/// </summary>
public static class RoundOutcome
{
    /// <summary>
    /// A tool call that had NOT terminated when the stream ended — the exact complement of the
    /// live UI's <see cref="ToolCallVisibility.IsCompleted"/>, i.e. still <i>pending</i>
    /// (dispatched, no result) or still <i>running</i> (a delegation mid-flight).
    /// </summary>
    /// <remarks>
    /// Expressed as the complement of the framework's own predicate rather than as a fresh notion
    /// of "pending", so the round's verdict and the chat UI can never disagree about which calls
    /// are outstanding. A terminal <see cref="ToolCallStatus.Failed"/> or
    /// <see cref="ToolCallStatus.Cancelled"/> call is finished — it failed, which is a recorded
    /// outcome, not an outstanding one.
    /// </remarks>
    public static bool IsUnfinished(ToolCallEntry call) => !ToolCallVisibility.IsCompleted(call);

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
    /// <para>Rule order matters and is itself the diagnosis: an unfinished tool call is reported
    /// ahead of a missing answer, because a round that lost a tool call has no answer to miss and
    /// the tool call is the actionable fact.</para>
    ///
    /// <para>🚨 <b>What the re-stamp deliberately does NOT touch.</b> Only a
    /// <see cref="ToolCallVisibility.IsPending"/> entry (default status + null
    /// <see cref="ToolCallEntry.Result"/>) is re-stamped, and only its
    /// <see cref="ToolCallEntry.Status"/> / <see cref="ToolCallEntry.IsSuccess"/> — never its
    /// <c>Result</c>, and never a <see cref="ToolCallStatus.Streaming"/> entry. Both exclusions
    /// exist because the cell write MERGES this log with the cell's current <c>ToolCalls</c>
    /// (<c>ThreadExecution.MergeToolCallEntries</c>), and that merge protects a late terminal
    /// stamp two ways: it prefers whichever side carries a <c>Result</c>, and it keeps the cell's
    /// status when the incoming one is <c>Streaming</c>. Writing a placeholder <c>Result</c>, or
    /// converting <c>Streaming</c> to <c>Failed</c> here, would defeat one guard each and CLOBBER
    /// a real terminal result that reached the cell through the delegation stamp but not this
    /// in-memory log. The VERDICT still counts those calls as unfinished — the round genuinely
    /// does not know they finished — so the invariant holds without the destructive write.</para>
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
            var stamped = toolCalls
                .Select(c => ToolCallVisibility.IsPending(c)
                    ? c with { Status = ToolCallStatus.Failed, IsSuccess = false }
                    : c)
                .ToImmutableList();
            var names = unfinished.Select(c => c.Name).Distinct().ToImmutableList();
            return new RoundConclusion(RoundVerdict.ToolCallUnfinished, stamped, names);
        }

        if (string.IsNullOrEmpty(finalText) && toolCalls.IsEmpty)
            return new RoundConclusion(RoundVerdict.NoOutput, toolCalls, []);

        if (!producedClosingText)
            return new RoundConclusion(RoundVerdict.NoFinalAnswer, toolCalls, []);

        return new RoundConclusion(RoundVerdict.Answered, toolCalls, []);
    }
}
