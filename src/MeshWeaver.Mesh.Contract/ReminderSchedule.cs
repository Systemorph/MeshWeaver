namespace MeshWeaver.Mesh;

/// <summary>
/// The PURE decisions behind reminders — is it due, may this instance claim it, what does it look
/// like after firing, and when does a recurring one run next. No hub, no clock of its own: every
/// answer is a function of the reminder plus the <c>now</c> the caller passes in, so the awkward
/// cases (a crashed claim, a reminder that slept through several intervals, a paused recurrence)
/// are unit-tested rather than reasoned about in production.
/// </summary>
public static class ReminderSchedule
{
    /// <summary>
    /// How long a firing claim is honoured before another instance may take it. Longer than any
    /// sane activity, short enough that a pod crash does not strand the reminder for an hour.
    /// </summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(10);

    /// <summary>Four times a day — the stats-refresh cadence, as an interval.</summary>
    public const int FourTimesDailySeconds = 6 * 60 * 60;

    /// <summary>Whether this reminder is RECURRING (reschedules itself) rather than one-shot.</summary>
    public static bool IsRecurring(Reminder reminder) =>
        reminder.IntervalSeconds is > 0;

    /// <summary>
    /// Whether the reminder wants to run at <paramref name="now"/>: Pending (or a Firing whose
    /// claim has expired) and past its due time. Paused, Completed and Failed never run — a
    /// failure is deliberately terminal so a broken reminder cannot hammer an external API every
    /// tick forever.
    /// </summary>
    public static bool IsDue(Reminder reminder, DateTime now) =>
        reminder.State switch
        {
            ReminderState.Pending => reminder.DueAt <= now,
            ReminderState.Firing => ClaimExpired(reminder, now),
            _ => false,
        };

    /// <summary>
    /// Whether a Firing reminder's claim is stale — the owning instance died mid-run. Only then may
    /// another instance take it over; without this a crash strands the reminder in Firing forever.
    /// An absent <see cref="Reminder.ClaimedAt"/> counts as expired: a Firing state with no claim
    /// is already corrupt, and refusing to reclaim it would strand it just the same.
    /// </summary>
    public static bool ClaimExpired(Reminder reminder, DateTime now) =>
        reminder.ClaimedAt is not { } claimedAt || claimedAt + ClaimLease <= now;

    /// <summary>
    /// The reminder as CLAIMED by <paramref name="instanceId"/>. The caller must write this and
    /// verify the write won (an optimistic-concurrency conflict means another instance claimed it
    /// first — stand down, do NOT run the work). Returns the reminder unchanged when it is not due,
    /// so a caller that skipped the <see cref="IsDue"/> check cannot accidentally fire it.
    /// </summary>
    public static Reminder Claim(Reminder reminder, string instanceId, DateTime now) =>
        IsDue(reminder, now)
            ? reminder with
            {
                State = ReminderState.Firing,
                ClaimedBy = instanceId,
                ClaimedAt = now,
            }
            : reminder;

    /// <summary>
    /// The reminder after a SUCCESSFUL run: a one-shot is Completed, a recurring one goes back to
    /// Pending at its next occurrence. The claim is released either way and
    /// <see cref="Reminder.LastError"/> is cleared — a success must not leave a stale failure
    /// showing on the node.
    /// </summary>
    public static Reminder Succeeded(Reminder reminder, DateTime now) =>
        reminder with
        {
            State = IsRecurring(reminder) ? ReminderState.Pending : ReminderState.Completed,
            DueAt = IsRecurring(reminder) ? NextOccurrence(reminder, now) : reminder.DueAt,
            LastFiredAt = now,
            FireCount = reminder.FireCount + 1,
            LastError = null,
            ClaimedBy = null,
            ClaimedAt = null,
        };

    /// <summary>
    /// The reminder after a FAILED run. A recurring reminder keeps its schedule and tries again at
    /// its next occurrence — a transient outage must not kill the stats refresh forever. A one-shot
    /// goes to <see cref="ReminderState.Failed"/> and stops: retrying an unattended publish on a
    /// loop is how you end up posting six times.
    /// </summary>
    public static Reminder Failed(Reminder reminder, string error, DateTime now) =>
        reminder with
        {
            State = IsRecurring(reminder) ? ReminderState.Pending : ReminderState.Failed,
            DueAt = IsRecurring(reminder) ? NextOccurrence(reminder, now) : reminder.DueAt,
            LastFiredAt = now,
            FireCount = reminder.FireCount + 1,
            LastError = string.IsNullOrWhiteSpace(error) ? "unknown error" : error.Trim(),
            ClaimedBy = null,
            ClaimedAt = null,
        };

    /// <summary>
    /// When a recurring reminder runs next: strictly AFTER <paramref name="now"/>, on the original
    /// cadence. Stepping the interval forward (rather than adding it to <c>now</c>) keeps a 4×/day
    /// refresh on its slots instead of drifting later on every run.
    ///
    /// <para>A reminder that slept through several intervals — the pod was down for a day — fires
    /// ONCE and resumes on cadence; it does not replay every missed slot. For a stats refresh the
    /// missed runs are worthless (the next one reads the same current numbers) and replaying them
    /// would burst an external API immediately after an outage, which is exactly when it is least
    /// welcome.</para>
    ///
    /// <para>Returns <see cref="Reminder.DueAt"/> unchanged for a one-shot: it has no next.</para>
    /// </summary>
    public static DateTime NextOccurrence(Reminder reminder, DateTime now)
    {
        if (!IsRecurring(reminder))
            return reminder.DueAt;

        var interval = TimeSpan.FromSeconds(reminder.IntervalSeconds!.Value);
        var next = reminder.DueAt;
        if (next > now)
            return next;

        // Step whole intervals past `now` in one arithmetic jump — a loop here would spin for
        // millions of iterations on a long-dormant reminder with a short interval.
        var skipped = (now - next).Ticks / interval.Ticks + 1;
        return next + TimeSpan.FromTicks(interval.Ticks * skipped);
    }

    /// <summary>
    /// A one-shot reminder for <paramref name="targetPath"/> at <paramref name="dueAt"/> — what
    /// "schedule this post" registers at the moment of the click.
    /// </summary>
    public static Reminder OneShot(string targetPath, string kind, DateTime dueAt, string? argument = null) =>
        new()
        {
            TargetPath = targetPath,
            Kind = kind,
            Argument = argument,
            DueAt = dueAt,
            IntervalSeconds = null,
            State = ReminderState.Pending,
        };

    /// <summary>
    /// A recurring reminder every <paramref name="intervalSeconds"/>, first firing at
    /// <paramref name="firstDueAt"/> — what the 4×/day stats refresh registers.
    /// </summary>
    public static Reminder Recurring(
        string targetPath, string kind, DateTime firstDueAt, int intervalSeconds, string? argument = null) =>
        new()
        {
            TargetPath = targetPath,
            Kind = kind,
            Argument = argument,
            DueAt = firstDueAt,
            IntervalSeconds = intervalSeconds,
            State = ReminderState.Pending,
        };
}
