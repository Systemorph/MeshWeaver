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
    // â”€â”€â”€ FindUnprocessedUserMessages â”€â”€â”€

    [Fact(Timeout = 30_000)]
    public void FindUnprocessedUserMessages_NoUsers_ReturnsEmpty()
    {
        var thread = new MeshThread();
        var result = ThreadSubmission.FindUnprocessedUserMessages(thread);
        result.Should().BeEmpty();
    }

    [Fact(Timeout = 30_000)]
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

    [Fact(Timeout = 30_000)]
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

    [Fact(Timeout = 30_000)]
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

    [Fact(Timeout = 30_000)]
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

    // â”€â”€â”€ PlanNextRound â”€â”€â”€

    [Fact(Timeout = 30_000)]
    public void PlanNextRound_Busy_ReturnsNull()
    {
        var thread = new MeshThread
        {
            Messages = ImmutableList.Create("u1"),
            UserMessageIds = ImmutableList.Create("u1"),
            IngestedMessageIds = ImmutableList<string>.Empty,
            Status = ThreadExecutionStatus.Executing
        };

        var result = ThreadSubmission.PlanNextRound(thread);

        result.Should().BeNull();
    }

    [Fact(Timeout = 30_000)]
    public void PlanNextRound_IdleWithOneQueued_ReturnsSingleItemDispatch()
    {
        var u1 = new ThreadMessage { Role = "user", Text = "hello" };
        var thread = new MeshThread
        {
            UserMessageIds = ImmutableList.Create("u1"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty.Add("u1", u1),
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

    [Fact(Timeout = 30_000)]
    public void PlanNextRound_IdleWithThreeQueued_DrainsAllIntoOneRound()
    {
        // Inbox semantics: PlanNextRound drains the entire queue at once.
        // All three messages share a single response cell for the round.
        var u1 = new ThreadMessage { Role = "user", Text = "one" };
        var u2 = new ThreadMessage { Role = "user", Text = "two" };
        var u3 = new ThreadMessage { Role = "user", Text = "three" };
        var thread = new MeshThread
        {
            UserMessageIds = ImmutableList.Create("u1", "u2", "u3"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                .Add("u1", u1).Add("u2", u2).Add("u3", u3)
        };

        var result = ThreadSubmission.PlanNextRound(thread);

        result.Should().NotBeNull();
        result!.UserMessageIds.Should().ContainInOrder("u1", "u2", "u3");
        result.ResponseMessageId.Should().NotBeNullOrEmpty();
    }

    [Fact(Timeout = 30_000)]
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

    [Fact(Timeout = 30_000)]
    public void PlanNextRound_AfterInterruptedRound_DrainsAllPending()
    {
        // Scenario: round 1 completed (u1 ingested, r1 in Messages). u2 and u3
        // arrived while idle (or during round 1) and sit in PendingUserMessages.
        // Inbox semantics: PlanNextRound drains ALL pending into one round.
        var u2 = new ThreadMessage { Role = "user", Text = "two" };
        var u3 = new ThreadMessage { Role = "user", Text = "three" };
        var thread = new MeshThread
        {
            Messages = ImmutableList.Create("u1", "r1"),
            UserMessageIds = ImmutableList.Create("u1", "u2", "u3"),
            IngestedMessageIds = ImmutableList.Create("u1"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                .Add("u2", u2).Add("u3", u3),
            Status = ThreadExecutionStatus.Idle
        };

        var result = ThreadSubmission.PlanNextRound(thread);

        result.Should().NotBeNull();
        result!.UserMessageIds.Should().ContainInOrder("u2", "u3");
    }
}
