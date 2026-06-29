#pragma warning disable CS1591

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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

    // â”€â”€â”€ PlanNextRound â”€â”€â”€

    [Fact]
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

    [Fact]
    public void PlanNextRound_IdleWithOneQueued_ReturnsSingleItemDispatch()
    {
        // The round's selection comes from the pending ThreadMessage being drained —
        // each message carries its own agent/model/harness (no thread-level Pending* mirror).
        var u1 = new ThreadMessage { Role = "user", Text = "hello", AgentName = "Executor", ModelName = "gpt-4" };
        var thread = new MeshThread
        {
            UserMessageIds = ImmutableList.Create("u1"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty.Add("u1", u1)
        };

        var result = ThreadSubmission.PlanNextRound(thread);

        result.Should().NotBeNull();
        result!.UserMessageIds.Should().ContainInOrder("u1");
        result.ResponseMessageId.Should().NotBeNullOrEmpty();
        result.AgentName.Should().Be("Executor");
        result.ModelName.Should().Be("gpt-4");
    }

    [Fact]
    public void PlanNextRound_SelectionComesFromLastDrainedMessage()
    {
        // Multiple queued messages with different selections: the round runs under the
        // LAST drained message's selection (its Text is also the current turn's input).
        var u1 = new ThreadMessage { Role = "user", Text = "one", AgentName = "Coder", ModelName = "gpt-4" };
        var u2 = new ThreadMessage { Role = "user", Text = "two", AgentName = "Worker", ModelName = "claude-opus-4-6", Harness = "MeshWeaver" };
        var thread = new MeshThread
        {
            UserMessageIds = ImmutableList.Create("u1", "u2"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                .Add("u1", u1).Add("u2", u2)
        };

        var result = ThreadSubmission.PlanNextRound(thread);

        result.Should().NotBeNull();
        result!.UserMessageIds.Should().ContainInOrder("u1", "u2");
        result.AgentName.Should().Be("Worker");
        result.ModelName.Should().Be("claude-opus-4-6");
        result.Harness.Should().Be("MeshWeaver");
    }

    [Fact]
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
    public void PlanNextRound_SelectionStaysQueuedWhileExecuting_AppliesWhenIdle()
    {
        // The agent/model/harness selection rides on the QUEUED ThreadMessage and is only
        // ACCEPTED (drives a round) when the thread is Idle. While a round is Executing the
        // selection stays queued — PlanNextRound returns null, so the running round is never
        // re-targeted to the newly-picked agent/model/harness. The moment the thread goes
        // Idle the queued message's selection drives the next round.
        var queued = new ThreadMessage
        {
            Role = "user",
            Text = "switch me",
            AgentName = "Worker",
            ModelName = "claude-opus-4-6",
            Harness = "MeshWeaver"
        };

        // While Executing: the queued selection is NOT accepted — nothing dispatches.
        var executing = new MeshThread
        {
            UserMessageIds = ImmutableList.Create("u1"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty.Add("u1", queued),
            Status = ThreadExecutionStatus.Executing
        };
        ThreadSubmission.PlanNextRound(executing).Should().BeNull(
            "an agent/model/harness selection on a queued message stays queued while the thread executes");

        // Same thread, now Idle: the queued message's selection drives the round.
        var idle = executing with { Status = ThreadExecutionStatus.Idle };
        var result = ThreadSubmission.PlanNextRound(idle);
        result.Should().NotBeNull("a queued selection is accepted once the thread is idle");
        result!.AgentName.Should().Be("Worker");
        result.ModelName.Should().Be("claude-opus-4-6");
        result.Harness.Should().Be("MeshWeaver");
    }

    [Fact]
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
