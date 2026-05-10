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
            new MeshThread());

        InboxTool.FormatToolResult(drain).Should().Be("(no new messages)");
    }

    [Fact]
    public void FormatToolResult_SingleMessage_HumanReadablePrefix()
    {
        var drain = new InboxDrainResult(
            ImmutableList.Create("Can you also include the unit tests?"),
            ImmutableList.Create("u2"),
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
            new MeshThread());

        var result = InboxTool.FormatToolResult(drain);

        result.Should().StartWith("User sent 3 follow-up messages:");
        result.Should().Contain("1. first follow-up");
        result.Should().Contain("2. second follow-up");
        result.Should().Contain("3. third follow-up");
    }
}
