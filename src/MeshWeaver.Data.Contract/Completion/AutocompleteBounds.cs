#nullable enable

namespace MeshWeaver.Data.Completion;

/// <summary>
/// The one place the <see cref="AutocompleteRequest"/> round-trip's time bounds are decided.
///
/// <para>🚨 <b>There is no "settle window" here, and there must never be one again.</b> The handler
/// used to answer when the merged provider snapshot had been QUIET for 150 ms. Quiet is not
/// settled: a provider whose rows arrive more than a beat after the fast in-memory ones — a
/// cross-partition query under load — missed the window, and the response went out truncated and
/// still labelled <see cref="AutocompleteResponse.IsComplete"/> = <c>true</c>, so no caller could
/// tell (#3094). The answer now comes from the providers' own lifecycle: the merged stream
/// COMPLETES when every <see cref="IAutocompleteProvider"/> has settled, which is authoritative and
/// carries no clock at all.</para>
///
/// <para>What is left is a HANG bound, which every one-shot request needs: a provider that never
/// completes must not hold the answer forever. That is <see cref="AnswerDeadline"/>, and an answer
/// forced by it is labelled <c>IsComplete = false</c> and logged with the offending provider's
/// name — never passed off as settled.</para>
/// </summary>
public static class AutocompleteBounds
{
    /// <summary>
    /// The answer deadline: how long the handler waits for every provider to settle before it
    /// answers with the best snapshot so far and marks the response
    /// <see cref="AutocompleteResponse.IsComplete"/> = <c>false</c>.
    ///
    /// <para>🚨 This is not a latency budget to tune. A contract-honouring provider set completes
    /// long before it and the answer goes out on completion; reaching this bound means some
    /// provider's <see cref="IAutocompleteProvider.GetItems"/> stream never completed, which is a
    /// defect in that provider, not a reason to move this number.</para>
    /// </summary>
    public static readonly TimeSpan AnswerDeadline = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The slack a CALLER adds on top of <see cref="AnswerDeadline"/> before giving up on an
    /// <see cref="AutocompleteRequest"/>.
    ///
    /// <para>🚨 <b>A caller's bound must strictly DOMINATE the producer's, never equal it.</b>
    /// Every caller in the tree used to wait exactly 2 s — the same value the handler answers at —
    /// so a legitimately late-but-in-contract answer raced its own caller's timeout and was
    /// dropped as often as it was kept. That was invisible while the handler answered at 150 ms;
    /// it becomes a coin toss the moment the handler is allowed to use its full deadline. Same
    /// rule, same reason as <c>LateResponseWatchBound</c> + <c>VerdictBoundGrace</c> on the write
    /// path: the outer bound exists to catch a WEDGE, so it must sit clear of the inner one.</para>
    /// </summary>
    public static readonly TimeSpan CallerGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long to wait for an <see cref="AutocompleteResponse"/> from another hub. Derived from
    /// <see cref="AnswerDeadline"/> + <see cref="CallerGrace"/> so the ordering holds by
    /// construction rather than by whoever writes the next caller remembering it.
    /// </summary>
    public static TimeSpan CallerBound => AnswerDeadline + CallerGrace;
}
