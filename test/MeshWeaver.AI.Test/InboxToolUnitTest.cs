#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using FluentAssertions;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pure-logic tests for <see cref="InboxTool.Drain"/> and
/// <see cref="InboxTool.FormatToolResult"/>. No hub, no async — exercises the
/// state-transition contract that the integration tests then verify
/// end-to-end.
/// </summary>
public class InboxToolUnitTest
{
    // ─── Drain ───

    [Fact]
    public void Drain_EmptyPending_ReturnsEmptyResult_LeavesThreadUnchanged()
    {
        var thread = new MeshThread
        {
            Messages = ImmutableList.Create("u1", "r1"),
            UserMessageIds = ImmutableList.Create("u1"),
            IngestedMessageIds = ImmutableList.Create("u1")
        };

        var result = InboxTool.Drain(thread);

        result.DrainedTexts.Should().BeEmpty();
        result.DrainedIds.Should().BeEmpty();
        result.UpdatedThread.Should().BeEquivalentTo(thread,
            "drain on empty inbox is a no-op");
    }

    [Fact]
    public void Drain_OnePending_ReturnsItAndMarksIngested()
    {
        var msg = new ThreadMessage { Role = "user", Text = "hello mid-stream" };
        var thread = new MeshThread
        {
            Messages = ImmutableList.Create("u1", "r1", "u2"),
            UserMessageIds = ImmutableList.Create("u1", "u2"),
            IngestedMessageIds = ImmutableList.Create("u1"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                .Add("u2", msg),
            IsExecuting = true
        };

        var result = InboxTool.Drain(thread);

        result.DrainedTexts.Should().ContainInOrder("hello mid-stream");
        result.DrainedIds.Should().ContainInOrder("u2");
        result.UpdatedThread.PendingUserMessages.Should().BeEmpty();
        result.UpdatedThread.IngestedMessageIds.Should().ContainInOrder("u1", "u2");
        result.UpdatedThread.IsExecuting.Should().BeTrue(
            "drain must not flip the executing flag — the agent is still running");
        result.UpdatedThread.Messages.Should().ContainInOrder(new[] { "u1", "r1", "u2" },
            "drain doesn't reorder Messages");
    }

    [Fact]
    public void Drain_ThreePendingInOrder_ReturnsAllInUserMessageIdsOrder()
    {
        var m1 = new ThreadMessage { Role = "user", Text = "one" };
        var m2 = new ThreadMessage { Role = "user", Text = "two" };
        var m3 = new ThreadMessage { Role = "user", Text = "three" };
        var thread = new MeshThread
        {
            UserMessageIds = ImmutableList.Create("u1", "u2", "u3"),
            // Intentionally insert in reverse order to prove the result follows
            // UserMessageIds order, not dictionary enumeration order.
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                .Add("u3", m3).Add("u1", m1).Add("u2", m2)
        };

        var result = InboxTool.Drain(thread);

        result.DrainedIds.Should().ContainInOrder("u1", "u2", "u3");
        result.DrainedTexts.Should().ContainInOrder("one", "two", "three");
    }

    [Fact]
    public void Drain_PendingNotInUserMessageIds_StillReturnedAtEnd()
    {
        // Defensive case — pending entry whose id was never registered. We must
        // not silently drop it, otherwise it would survive forever.
        var orphan = new ThreadMessage { Role = "user", Text = "orphan text" };
        var thread = new MeshThread
        {
            UserMessageIds = ImmutableList.Create("u1"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                .Add("orphan-id", orphan)
        };

        var result = InboxTool.Drain(thread);

        result.DrainedTexts.Should().ContainInOrder("orphan text");
        result.UpdatedThread.PendingUserMessages.Should().BeEmpty();
    }

    [Fact]
    public void Drain_DoesNotDoubleAddIngestedIds()
    {
        var msg = new ThreadMessage { Role = "user", Text = "x" };
        var thread = new MeshThread
        {
            UserMessageIds = ImmutableList.Create("u1"),
            // u1 is somehow already ingested (defensive — race tolerance) but
            // also pending. Drain should not produce duplicates.
            IngestedMessageIds = ImmutableList.Create("u1"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                .Add("u1", msg)
        };

        var result = InboxTool.Drain(thread);

        result.UpdatedThread.IngestedMessageIds.Should().ContainSingle().Which.Should().Be("u1");
    }

    [Fact]
    public void Drain_PreservesOtherThreadFields()
    {
        var msg = new ThreadMessage { Role = "user", Text = "x" };
        var thread = new MeshThread
        {
            UserMessageIds = ImmutableList.Create("u1"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty.Add("u1", msg),
            IsExecuting = true,
            ActiveMessageId = "r1",
            CreatedBy = "user@example.com",
            SelectedAgentName = "TestAgent",
            SelectedModelName = "TestModel",
            TokensUsed = 42
        };

        var updated = InboxTool.Drain(thread).UpdatedThread;

        updated.IsExecuting.Should().BeTrue();
        updated.ActiveMessageId.Should().Be("r1");
        updated.CreatedBy.Should().Be("user@example.com");
        updated.SelectedAgentName.Should().Be("TestAgent");
        updated.SelectedModelName.Should().Be("TestModel");
        updated.TokensUsed.Should().Be(42);
    }

    [Fact]
    public void Drain_NullThread_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => InboxTool.Drain(null!));
    }

    // ─── FormatToolResult ───

    [Fact]
    public void FormatToolResult_Empty_NoNewMessages()
    {
        var drain = new InboxDrainResult(
            ImmutableList<string>.Empty,
            ImmutableList<string>.Empty,
            ImmutableList<ThreadMessage>.Empty,
            new MeshThread());

        InboxTool.FormatToolResult(drain).Should().Be("(no new messages)");
    }

    [Fact]
    public void FormatToolResult_SingleMessage_HumanReadablePrefix()
    {
        var drain = new InboxDrainResult(
            ImmutableList.Create("Can you also include the unit tests?"),
            ImmutableList.Create("u2"),
            ImmutableList.Create(new ThreadMessage { Role = "user", Text = "Can you also include the unit tests?" }),
            new MeshThread());

        var result = InboxTool.FormatToolResult(drain);

        result.Should().StartWith("User sent a follow-up message:");
        result.Should().Contain("Can you also include the unit tests?");
    }

    [Fact]
    public void FormatToolResult_MultipleMessages_NumberedList()
    {
        var drain = new InboxDrainResult(
            ImmutableList.Create("first follow-up", "second follow-up", "third follow-up"),
            ImmutableList.Create("u2", "u3", "u4"),
            ImmutableList.Create(
                new ThreadMessage { Role = "user", Text = "first follow-up" },
                new ThreadMessage { Role = "user", Text = "second follow-up" },
                new ThreadMessage { Role = "user", Text = "third follow-up" }),
            new MeshThread());

        var result = InboxTool.FormatToolResult(drain);

        result.Should().StartWith("User sent 3 follow-up messages:");
        result.Should().Contain("1. first follow-up");
        result.Should().Contain("2. second follow-up");
        result.Should().Contain("3. third follow-up");
    }

    // ─── Drain semantics for the inbox design ───
    //
    // The inbox is the unified ingestion point. AppendUserInput writes to
    // PendingUserMessages + UserMessageIds only. Drain moves the queue into
    // Messages, marks IngestedMessageIds, and returns the satellite-cell
    // payloads so the caller can materialise them via CreateNode.

    [Fact]
    public void Drain_AppendsDrainedIdsToMessagesInSubmissionOrder()
    {
        var m1 = new ThreadMessage { Role = "user", Text = "one" };
        var m2 = new ThreadMessage { Role = "user", Text = "two" };
        var thread = new MeshThread
        {
            // Pre-existing prior round: u0 ingested, r0 responded.
            Messages = ImmutableList.Create("u0", "r0"),
            UserMessageIds = ImmutableList.Create("u0", "u1", "u2"),
            IngestedMessageIds = ImmutableList.Create("u0"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                .Add("u1", m1).Add("u2", m2)
        };

        var result = InboxTool.Drain(thread);

        result.UpdatedThread.Messages.Should().ContainInOrder("u0", "r0", "u1", "u2");
        // drained ids are appended to Messages in submission order
        result.UpdatedThread.PendingUserMessages.Should().BeEmpty();
        result.UpdatedThread.IngestedMessageIds.Should().ContainInOrder("u0", "u1", "u2");
    }

    [Fact]
    public void Drain_IdAlreadyInMessages_NotAppendedTwice()
    {
        var msg = new ThreadMessage { Role = "user", Text = "x" };
        var thread = new MeshThread
        {
            Messages = ImmutableList.Create("u1"), // already present from a prior ingest
            UserMessageIds = ImmutableList.Create("u1"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty.Add("u1", msg)
        };

        var result = InboxTool.Drain(thread);

        result.UpdatedThread.Messages.Should().ContainSingle().Which.Should().Be("u1",
            "idempotent: re-ingest of an id already in Messages is a no-op on Messages");
    }

    [Fact]
    public void Drain_ReturnsThreadMessages_ForSatelliteCellMaterialisation()
    {
        var m1 = new ThreadMessage
        {
            Role = "user",
            Text = "first",
            AgentName = "Foo",
            ModelName = "fake-model",
            ContextPath = "User/Alice"
        };
        var m2 = new ThreadMessage { Role = "user", Text = "second" };
        var thread = new MeshThread
        {
            UserMessageIds = ImmutableList.Create("u1", "u2"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                .Add("u1", m1).Add("u2", m2)
        };

        var result = InboxTool.Drain(thread);

        result.DrainedMessages.Should().HaveCount(2);
        result.DrainedMessages[0].Should().BeSameAs(m1,
            "the original ThreadMessage envelope is returned so the caller "
            + "can CreateNode with the exact payload (preserves AgentName, "
            + "ModelName, ContextPath, Attachments)");
        result.DrainedMessages[1].Should().BeSameAs(m2);
    }

    // ─── GUI-perspective ───
    //
    // The chat view binds to MeshThread.PendingUserMessages (queued / "not yet
    // picked up") and MeshThread.Messages (submitted / "picked up by inbox").
    // These tests pin the visible state transitions so a regression in
    // either AppendUserInput (writes pending) or Drain (moves pending→messages)
    // surfaces as a unit-test failure instead of a runtime visual glitch.

    [Fact]
    public void GuiPerspective_QueuedState_VisibleInPendingButNotInMessages()
    {
        // Setup mirrors what ThreadInput.AppendUserInput writes when a user
        // hits Send while the thread is idle: the entry sits in pending; no
        // satellite cell exists; Messages doesn't yet contain the id.
        var msg = new ThreadMessage
        {
            Role = "user",
            Text = "hello",
            Status = ThreadMessageStatus.Submitted
        };
        var thread = new MeshThread
        {
            UserMessageIds = ImmutableList.Create("u1"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty.Add("u1", msg),
            // No Messages entry yet — inbox hasn't picked it up.
        };

        thread.Messages.Should().BeEmpty("queued message must not be in Messages until the inbox picks it up");
        thread.PendingUserMessages.Should().ContainKey("u1",
            "queued message lives in PendingUserMessages — the GUI renders it from this dict");
        thread.PendingUserMessages["u1"].Text.Should().Be("hello");
    }

    [Fact]
    public void GuiPerspective_InboxPickup_MovesIdFromPendingToMessages()
    {
        // Setup: same as above (queued state).
        var msg = new ThreadMessage { Role = "user", Text = "hello" };
        var thread = new MeshThread
        {
            UserMessageIds = ImmutableList.Create("u1"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty.Add("u1", msg)
        };

        // The inbox picks it up (round-start ingestion path).
        var afterPickup = InboxTool.Drain(thread).UpdatedThread;

        afterPickup.PendingUserMessages.Should().NotContainKey("u1",
            "after pickup, the id leaves PendingUserMessages — GUI removes the queued visual");
        afterPickup.Messages.Should().Contain("u1",
            "after pickup, the id appears in Messages — GUI renders the materialised cell");
        afterPickup.IngestedMessageIds.Should().Contain("u1",
            "ingestion is tracked so a re-fire of the watcher doesn't double-dispatch");
    }

    [Fact]
    public void GuiPerspective_MultipleQueued_AllPickedUpInOneDrain()
    {
        // Three rapid submits while the thread is idle: all sit in pending in
        // submission order.
        var m1 = new ThreadMessage { Role = "user", Text = "first" };
        var m2 = new ThreadMessage { Role = "user", Text = "second" };
        var m3 = new ThreadMessage { Role = "user", Text = "third" };
        var thread = new MeshThread
        {
            UserMessageIds = ImmutableList.Create("u1", "u2", "u3"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                .Add("u1", m1).Add("u2", m2).Add("u3", m3)
        };

        thread.Messages.Should().BeEmpty();
        thread.PendingUserMessages.Should().HaveCount(3);

        // One drain ingests all three.
        var afterPickup = InboxTool.Drain(thread).UpdatedThread;

        afterPickup.PendingUserMessages.Should().BeEmpty(
            "pending is fully drained in one ingestion event");
        afterPickup.Messages.Should().ContainInOrder("u1", "u2", "u3");
        afterPickup.IngestedMessageIds.Should().ContainInOrder("u1", "u2", "u3");
    }

    [Fact]
    public void GuiPerspective_MidStreamSubmit_VisibleInPendingDuringExecution()
    {
        // Round 1 is running (IsExecuting=true, response cell r1 active). A new
        // user submission lands while streaming — it must show up in
        // PendingUserMessages as a queued cell, NOT in Messages yet (the
        // currently-streaming response cell continues uninterrupted).
        var threadBefore = new MeshThread
        {
            IsExecuting = true,
            ActiveMessageId = "r1",
            Messages = ImmutableList.Create("u1", "r1"),
            UserMessageIds = ImmutableList.Create("u1"),
            IngestedMessageIds = ImmutableList.Create("u1")
        };

        // Simulate AppendUserInput's atomic update for u2 (only Pending + UserMessageIds).
        var u2 = new ThreadMessage { Role = "user", Text = "follow-up" };
        var threadAfterSubmit = threadBefore with
        {
            UserMessageIds = threadBefore.UserMessageIds.Add("u2"),
            PendingUserMessages = threadBefore.PendingUserMessages.SetItem("u2", u2)
        };

        threadAfterSubmit.IsExecuting.Should().BeTrue("the current round keeps running");
        threadAfterSubmit.ActiveMessageId.Should().Be("r1", "current response cell still streaming");
        threadAfterSubmit.Messages.Should().NotContain("u2",
            "follow-up is NOT yet visible in Messages — GUI shows it as 'queued'");
        threadAfterSubmit.PendingUserMessages.Should().ContainKey("u2",
            "follow-up is visible in PendingUserMessages — GUI renders the queued cell");

        // The agent's check_inbox tool drains the new pending entry. The same
        // response cell r1 continues; u2 moves into Messages between u1/r1
        // (preserving order) so the GUI shows it as a submitted cell.
        var afterPickup = InboxTool.Drain(threadAfterSubmit).UpdatedThread;

        afterPickup.IsExecuting.Should().BeTrue("drain doesn't tear the round down");
        afterPickup.ActiveMessageId.Should().Be("r1", "still streaming to r1");
        afterPickup.Messages.Should().Contain("u2",
            "follow-up has moved into Messages — GUI flips it from 'queued' to 'submitted'");
        afterPickup.PendingUserMessages.Should().NotContainKey("u2",
            "follow-up is no longer queued");
        afterPickup.IngestedMessageIds.Should().Contain("u2",
            "follow-up is marked ingested so the watcher doesn't dispatch a new round for it");
    }
}
