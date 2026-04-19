#pragma warning disable CS1591

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using FluentAssertions;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pure-logic unit tests for <see cref="ThreadSubmission"/>: no hub, no async.
/// Covers <see cref="ThreadSubmission.FindUnprocessedUserMessages"/> and
/// <see cref="ThreadSubmission.PlanNextRound"/>.
/// </summary>
public class ThreadSubmissionUnitTest
{
    // ─── FindUnprocessedUserMessages ───

    [Fact]
    public void FindUnprocessedUserMessages_NoUsers_ReturnsEmpty()
    {
        var thread = new MeshThread();
        var result = ThreadSubmission.FindUnprocessedUserMessages(thread);
        result.Should().BeEmpty();
    }

    [Fact]
    public void FindUnprocessedUserMessages_AllIngested_ReturnsEmpty()
    {
        var thread = new MeshThread
        {
            Messages = ImmutableList.Create("u1", "r1"),
            UserMessageIds = ImmutableList.Create("u1"),
            IngestedMessageIds = ImmutableList.Create("u1")
        };

        var result = ThreadSubmission.FindUnprocessedUserMessages(thread);

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindUnprocessedUserMessages_SingleQueued_ReturnsIt()
    {
        var thread = new MeshThread
        {
            Messages = ImmutableList.Create("u1", "r1", "u2"),
            UserMessageIds = ImmutableList.Create("u1", "u2"),
            IngestedMessageIds = ImmutableList.Create("u1")
        };

        var result = ThreadSubmission.FindUnprocessedUserMessages(thread);

        result.Should().ContainInOrder("u2");
    }

    [Fact]
    public void FindUnprocessedUserMessages_ThreeQueued_ReturnsAllInOrder()
    {
        var thread = new MeshThread
        {
            Messages = ImmutableList.Create("u1", "u2", "u3"),
            UserMessageIds = ImmutableList.Create("u1", "u2", "u3"),
            IngestedMessageIds = ImmutableList<string>.Empty
        };

        var result = ThreadSubmission.FindUnprocessedUserMessages(thread);

        result.Should().ContainInOrder("u1", "u2", "u3");
    }

    [Fact]
    public void FindUnprocessedUserMessages_Interleaved_ReturnsUnprocessedOnly()
    {
        var thread = new MeshThread
        {
            Messages = ImmutableList.Create("u1", "r1", "u2", "r2", "u3"),
            UserMessageIds = ImmutableList.Create("u1", "u2", "u3"),
            IngestedMessageIds = ImmutableList.Create("u1", "u2")
        };

        var result = ThreadSubmission.FindUnprocessedUserMessages(thread);

        result.Should().ContainInOrder("u3");
    }

    // ─── PlanNextRound ───

    [Fact]
    public void PlanNextRound_Busy_ReturnsNull()
    {
        var thread = new MeshThread
        {
            Messages = ImmutableList.Create("u1"),
            UserMessageIds = ImmutableList.Create("u1"),
            IngestedMessageIds = ImmutableList<string>.Empty,
            IsExecuting = true
        };

        var result = ThreadSubmission.PlanNextRound(thread);

        result.Should().BeNull();
    }

    [Fact]
    public void PlanNextRound_IdleWithOneQueued_ReturnsSingleItemDispatch()
    {
        var thread = new MeshThread
        {
            Messages = ImmutableList.Create("u1"),
            UserMessageIds = ImmutableList.Create("u1"),
            IngestedMessageIds = ImmutableList<string>.Empty,
            PendingAgentName = "Executor",
            PendingModelName = "gpt-4"
        };

        var result = ThreadSubmission.PlanNextRound(thread);

        result.Should().NotBeNull();
        result!.UserMessageIds.Should().ContainInOrder("u1");
        result.ResponseMessageId.Should().NotBeNullOrEmpty();
        result.AgentName.Should().Be("Executor");
        result.ModelName.Should().Be("gpt-4");
    }

    [Fact]
    public void PlanNextRound_IdleWithThreeQueued_ReturnsBatchedDispatch()
    {
        var thread = new MeshThread
        {
            Messages = ImmutableList.Create("u1", "u2", "u3"),
            UserMessageIds = ImmutableList.Create("u1", "u2", "u3"),
            IngestedMessageIds = ImmutableList<string>.Empty
        };

        var result = ThreadSubmission.PlanNextRound(thread);

        result.Should().NotBeNull();
        result!.UserMessageIds.Should().ContainInOrder("u1", "u2", "u3");
        result.ResponseMessageId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void PlanNextRound_IdleNothingQueued_ReturnsNull()
    {
        var thread = new MeshThread
        {
            Messages = ImmutableList.Create("u1", "r1"),
            UserMessageIds = ImmutableList.Create("u1"),
            IngestedMessageIds = ImmutableList.Create("u1")
        };

        var result = ThreadSubmission.PlanNextRound(thread);

        result.Should().BeNull();
    }

    [Fact]
    public void PlanNextRound_AfterInterruptedRound_ReturnsNewDispatchForQueuedInputs()
    {
        // Scenario: r1 is in Messages but IsExecuting was set back to false
        // (the agent loop finalized the round early after seeing queued inputs).
        var thread = new MeshThread
        {
            Messages = ImmutableList.Create("u1", "r1", "u2", "u3"),
            UserMessageIds = ImmutableList.Create("u1", "u2", "u3"),
            IngestedMessageIds = ImmutableList.Create("u1"),
            IsExecuting = false
        };

        var result = ThreadSubmission.PlanNextRound(thread);

        result.Should().NotBeNull();
        result!.UserMessageIds.Should().ContainInOrder("u2", "u3");
    }
}
