using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the chat tool-calls window logic: the visible set is filled running → pending →
/// completed-backfill, capped at 5 active, with a 5-second linger that keeps a just-finished
/// call on screen even when the cap is full. The collapsed remainder + per-bucket counts drive
/// the "show all" control.
/// </summary>
public class ToolCallVisibilityTest
{
    private static readonly DateTime Now = new(2026, 6, 29, 12, 0, 0, DateTimeKind.Utc);

    private static ToolCallEntry Running(string name = "delegate_to_agent") =>
        new() { Name = name, Status = ToolCallStatus.Streaming, Result = "…live…", Timestamp = Now };

    private static ToolCallEntry Pending(string name = "Search") =>
        // Status defaults to Success via the record initializer; a null Result is the pending tell.
        new() { Name = name, Result = null, Timestamp = Now };

    private static ToolCallEntry Completed(string name, TimeSpan ago) =>
        new() { Name = name, Status = ToolCallStatus.Success, Result = "done", Timestamp = Now - ago };

    [Fact]
    public void Empty_or_null_yields_empty_view()
    {
        ToolCallVisibility.Partition(null, Now).Visible.Should().BeEmpty();
        var view = ToolCallVisibility.Partition([], Now);
        view.Visible.Should().BeEmpty();
        view.Hidden.Should().BeEmpty();
        view.HasHidden.Should().BeFalse();
    }

    [Fact]
    public void Classifies_running_pending_completed()
    {
        ToolCallVisibility.IsRunning(Running()).Should().BeTrue();
        ToolCallVisibility.IsPending(Pending()).Should().BeTrue();
        ToolCallVisibility.IsCompleted(Completed("Get", TimeSpan.Zero)).Should().BeTrue();

        // A terminal failure with no result still counts as completed, not pending.
        var failed = new ToolCallEntry { Name = "Update", Status = ToolCallStatus.Failed, Result = null, Timestamp = Now };
        ToolCallVisibility.IsCompleted(failed).Should().BeTrue();
        ToolCallVisibility.IsPending(failed).Should().BeFalse();
    }

    [Fact]
    public void Counts_reflect_whole_list_regardless_of_window()
    {
        var calls = new List<ToolCallEntry>
        {
            Running(), Running(),
            Pending(), Pending(), Pending(),
            Completed("a", TimeSpan.FromMinutes(1)), Completed("b", TimeSpan.FromMinutes(2)),
        };

        var view = ToolCallVisibility.Partition(calls, Now);

        view.RunningCount.Should().Be(2);
        view.PendingCount.Should().Be(3);
        view.CompletedCount.Should().Be(2);
    }

    [Fact]
    public void Running_sit_above_pending_in_the_window()
    {
        var r1 = Running("delegate_to_X");
        var p1 = Pending("Search");
        var r2 = Running("delegate_to_Y");
        // Interleaved input order — the window must still group running before pending.
        var view = ToolCallVisibility.Partition([p1, r1, r2], Now);

        view.Visible.Take(2).Should().OnlyContain(c => ToolCallVisibility.IsRunning(c));
        view.Visible[2].Should().BeSameAs(p1);
    }

    [Fact]
    public void Caps_active_window_at_five_and_hides_the_rest()
    {
        // Seven pending — only five fit the window, two collapse.
        var calls = Enumerable.Range(0, 7).Select(i => Pending($"t{i}")).ToList();

        var view = ToolCallVisibility.Partition(calls, Now);

        view.Visible.Should().HaveCount(5);
        view.Hidden.Should().HaveCount(2);
        view.HasHidden.Should().BeTrue();
    }

    [Fact]
    public void Backfills_empty_slots_with_newest_completed()
    {
        var calls = new List<ToolCallEntry>
        {
            Running(),                                   // 1 active
            Completed("old", TimeSpan.FromMinutes(10)),
            Completed("mid", TimeSpan.FromMinutes(5)),
            Completed("new", TimeSpan.FromMinutes(1)),
            Completed("newest", TimeSpan.FromSeconds(30)),
            Completed("ancient", TimeSpan.FromHours(1)),
        };

        var view = ToolCallVisibility.Partition(calls, Now);

        // 1 active + backfill to fill the 5-slot window with the newest completed.
        view.Visible.Should().HaveCount(5);
        var completedVisible = view.Visible.Where(ToolCallVisibility.IsCompleted).Select(c => c.Name).ToList();
        completedVisible.Should().ContainInOrder("newest", "new", "mid", "old");
        view.Hidden.Should().ContainSingle().Which.Name.Should().Be("ancient");
    }

    [Fact]
    public void Freshly_completed_lingers_in_window_even_when_cap_is_full()
    {
        var calls = new List<ToolCallEntry>
        {
            Running(), Running(), Running(), Running(), Running(),  // cap full with active
            Completed("justFinished", TimeSpan.FromSeconds(2)),     // < 5s linger
            Completed("longDone", TimeSpan.FromMinutes(3)),         // stale
        };

        var view = ToolCallVisibility.Partition(calls, Now);

        // Window holds the 5 running PLUS the fresh completion (it briefly exceeds the cap)…
        view.Visible.Should().HaveCount(6);
        view.Visible.Should().Contain(c => c.Name == "justFinished");
        // …but the stale completion stays collapsed.
        view.Hidden.Should().ContainSingle().Which.Name.Should().Be("longDone");
    }

    [Fact]
    public void Stale_completion_collapses_once_linger_passes()
    {
        var calls = new List<ToolCallEntry>
        {
            Running(), Running(), Running(), Running(), Running(),
            Completed("done", TimeSpan.FromSeconds(6)),  // just past the 5s window
        };

        var view = ToolCallVisibility.Partition(calls, Now);

        view.Visible.Should().HaveCount(5).And.OnlyContain(c => ToolCallVisibility.IsRunning(c));
        view.Hidden.Should().ContainSingle().Which.Name.Should().Be("done");
    }
}
