using System;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// The reminder decisions, pinned. These are the cases that decide whether a scheduled publish
/// happens once, twice, or never — so they are tested as pure functions rather than discovered in
/// production with two replicas and a pod restart.
/// </summary>
public class ReminderScheduleTest
{
    private static readonly DateTime Now = new(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc);

    private static Reminder OneShotAt(DateTime due) =>
        ReminderSchedule.OneShot("Profiles/RolandLinkedIn", "PublishPost", due, "Posts/Hello");

    private static Reminder FourTimesDaily(DateTime first) =>
        ReminderSchedule.Recurring(
            "Profiles/RolandLinkedIn", "RefreshStats", first, ReminderSchedule.FourTimesDailySeconds);

    [Fact]
    public void A_reminder_is_due_only_once_its_time_has_come()
    {
        ReminderSchedule.IsDue(OneShotAt(Now.AddMinutes(1)), Now).Should().BeFalse("it is not due yet");
        ReminderSchedule.IsDue(OneShotAt(Now), Now).Should().BeTrue("due exactly now counts");
        ReminderSchedule.IsDue(OneShotAt(Now.AddMinutes(-1)), Now).Should().BeTrue("overdue still fires");
    }

    [Fact]
    public void Terminal_and_paused_reminders_never_fire()
    {
        var due = OneShotAt(Now.AddMinutes(-1));
        foreach (var state in new[] { ReminderState.Completed, ReminderState.Failed, ReminderState.Paused })
            ReminderSchedule.IsDue(due with { State = state }, Now).Should()
                .BeFalse($"a {state} reminder must never run again");
    }

    [Fact]
    public void A_live_claim_blocks_a_second_instance_but_a_dead_one_is_reclaimed()
    {
        var claimed = ReminderSchedule.Claim(OneShotAt(Now.AddMinutes(-1)), "pod-a", Now);
        claimed.State.Should().Be(ReminderState.Firing);
        claimed.ClaimedBy.Should().Be("pod-a");

        // Pod B sees the same reminder a second later: the claim is live, so it must stand down —
        // this is what stops both replicas publishing the same post.
        ReminderSchedule.IsDue(claimed, Now.AddSeconds(1)).Should().BeFalse("pod-a holds a live claim");

        // Pod A dies mid-run. Once the lease expires the work must not be stranded forever.
        var afterLease = Now + ReminderSchedule.ClaimLease;
        ReminderSchedule.IsDue(claimed, afterLease).Should().BeTrue("an expired claim is reclaimable");

        // A Firing state with no claim at all is corrupt — reclaim it rather than strand it.
        ReminderSchedule.ClaimExpired(claimed with { ClaimedAt = null }, Now).Should().BeTrue();
    }

    [Fact]
    public void Claiming_something_that_is_not_due_changes_nothing()
    {
        var notYet = OneShotAt(Now.AddHours(1));
        ReminderSchedule.Claim(notYet, "pod-a", Now).Should().Be(notYet,
            "the claim helper must not let a caller who skipped the due check fire early");
    }

    [Fact]
    public void A_one_shot_completes_and_a_recurring_one_reschedules()
    {
        var once = ReminderSchedule.Succeeded(OneShotAt(Now), Now);
        once.State.Should().Be(ReminderState.Completed);
        once.FireCount.Should().Be(1);
        once.ClaimedBy.Should().BeNull("the claim is released");

        var repeated = ReminderSchedule.Succeeded(FourTimesDaily(Now), Now);
        repeated.State.Should().Be(ReminderState.Pending, "a recurring reminder is never Completed");
        repeated.DueAt.Should().Be(Now.AddHours(6), "next of four daily slots");
    }

    [Fact]
    public void Success_clears_a_previous_error()
    {
        var recovered = ReminderSchedule.Succeeded(
            FourTimesDaily(Now) with { LastError = "429 from LinkedIn" }, Now);
        recovered.LastError.Should().BeNull("a stale failure must not keep showing after a success");
    }

    [Fact]
    public void A_failed_one_shot_stops_but_a_failed_recurring_one_keeps_its_cadence()
    {
        var once = ReminderSchedule.Failed(OneShotAt(Now), "boom", Now);
        once.State.Should().Be(ReminderState.Failed,
            "retrying an unattended publish on a loop is how you post six times");
        once.LastError.Should().Be("boom");

        var repeated = ReminderSchedule.Failed(FourTimesDaily(Now), "429", Now);
        repeated.State.Should().Be(ReminderState.Pending,
            "a transient outage must not kill the stats refresh forever");
        repeated.DueAt.Should().Be(Now.AddHours(6));
    }

    [Fact]
    public void A_reminder_that_slept_through_many_intervals_fires_once_and_resumes_on_cadence()
    {
        // The pod was down for two days. A 4×/day reminder must NOT replay eight missed runs the
        // moment it comes back — that bursts the API exactly when it is least welcome.
        var dormant = FourTimesDaily(Now);
        var wokeUp = Now.AddDays(2).AddMinutes(5);

        var next = ReminderSchedule.NextOccurrence(dormant, wokeUp);
        next.Should().BeAfter(wokeUp, "the next slot is strictly in the future");
        next.Should().BeBefore(wokeUp.AddHours(6), "and it is the very next slot, not one per missed run");

        // Still on the ORIGINAL 6-hour grid — no drift from the outage.
        (next - dormant.DueAt).TotalHours.Should().Be(Math.Round((next - dormant.DueAt).TotalHours / 6) * 6);
    }

    [Fact]
    public void Recurring_slots_do_not_drift_when_a_run_finishes_late()
    {
        // Firing takes 4 minutes; the next slot must still be on the grid, not 6h after completion.
        var reminder = FourTimesDaily(Now);
        var finishedLate = Now.AddMinutes(4);
        ReminderSchedule.Succeeded(reminder, finishedLate).DueAt.Should().Be(Now.AddHours(6),
            "stepping the interval keeps the cadence; adding to 'now' would drift later every run");
    }

    [Fact]
    public void A_one_shot_has_no_next_occurrence()
    {
        var once = OneShotAt(Now);
        ReminderSchedule.NextOccurrence(once, Now.AddYears(1)).Should().Be(once.DueAt);
        ReminderSchedule.IsRecurring(once).Should().BeFalse();
        ReminderSchedule.IsRecurring(once with { IntervalSeconds = 0 }).Should()
            .BeFalse("a zero interval is not a recurrence — it would be an infinite loop");
    }

    [Fact]
    public void Four_times_daily_is_six_hours()
        => ReminderSchedule.FourTimesDailySeconds.Should().Be(21600);
}
