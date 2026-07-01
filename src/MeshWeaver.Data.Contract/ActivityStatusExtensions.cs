#nullable enable

using System.Collections.Generic;

namespace MeshWeaver.Data;

/// <summary>
/// The status-driven continuation + rendering law for activities (and any status-bearing content):
///
/// <list type="bullet">
///   <item><b>Continuations are triggered by a status change INTO a follow-up status.</b> Rather than
///         ad-hoc completion detection (message counts, bespoke events), a watcher does
///         <c>stream.Select(n => n.Status).Where(s => s.IsTerminal())</c> — the round/activity reached a
///         terminal state (cancelled, errored, or finished), so the continuation runs.</item>
///   <item><b>Only on success do we read the real typed content.</b> A consumer/layout area calls
///         <c>ContentAs&lt;T&gt;</c> ONLY when <see cref="IsSuccess"/> — never on a still-running or
///         failed activity, whose content may be absent, partial, or not-yet-type-resolvable (the
///         untyped-JsonElement storm that wedged the portal: typing un-ready content → null → reactive
///         waits time out → resubscribe storm). Gate the type read on success and that whole class is gone.</item>
///   <item><b>Error/cancel ⇒ emergency mode.</b> When <see cref="IsError"/>, the node is rendered as its
///         error, no matter what — every layout area short-circuits to the error instead of attempting a
///         normal (typed) render.</item>
/// </list>
/// </summary>
public static class ActivityStatusExtensions
{
    /// <summary>
    /// The follow-up statuses: a transition INTO one of these is what triggers a continuation — the
    /// activity has stopped (finished, errored, or was cancelled). Equivalent to "not
    /// <see cref="ActivityStatus.Running"/>".
    /// </summary>
    public static readonly IReadOnlySet<ActivityStatus> FollowupStatuses = new HashSet<ActivityStatus>
    {
        ActivityStatus.Succeeded,
        ActivityStatus.Warning,
        ActivityStatus.Failed,
        ActivityStatus.Cancelled,
    };

    /// <summary>
    /// A terminal status — the activity has stopped and a continuation should fire. Only
    /// <see cref="ActivityStatus.Running"/> is non-terminal.
    /// </summary>
    public static bool IsTerminal(this ActivityStatus status) => status != ActivityStatus.Running;

    /// <summary>
    /// The activity produced a usable result (<see cref="ActivityStatus.Succeeded"/> or
    /// <see cref="ActivityStatus.Warning"/>) — the ONLY case where the real typed content
    /// (<c>ContentAs&lt;T&gt;</c>) should be read. Never type a running or failed activity's content.
    /// </summary>
    public static bool IsSuccess(this ActivityStatus status)
        => status is ActivityStatus.Succeeded or ActivityStatus.Warning;

    /// <summary>
    /// An error-class terminal status (<see cref="ActivityStatus.Failed"/> or
    /// <see cref="ActivityStatus.Cancelled"/>) — triggers EMERGENCY MODE: the content renders as its
    /// error, every layout area short-circuits to the error, no typed render is attempted.
    /// </summary>
    public static bool IsError(this ActivityStatus status)
        => status is ActivityStatus.Failed or ActivityStatus.Cancelled;
}
