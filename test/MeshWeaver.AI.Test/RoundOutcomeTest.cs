#pragma warning disable CS1591

using System.Collections.Immutable;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The round-completion invariant as a PURE table: no hub, no mesh, no clock. Every row is a claim
/// about what a round is allowed to say about itself, so the rule cannot drift silently while the
/// integration test (<see cref="RoundCompletionHonestyTest"/>) sits behind a 40 s round.
/// </summary>
public class RoundOutcomeTest
{
    private static ToolCallEntry Dispatched(string name = "CreateEvent") =>
        new() { Name = name };   // the record's defaults: Status = Success, IsSuccess = true, Result = null

    private static ToolCallEntry Returned(string name = "CreateEvent", bool success = true) =>
        new()
        {
            Name = name,
            Result = success ? "ok" : "it failed",
            IsSuccess = success,
            Status = success ? ToolCallStatus.Success : ToolCallStatus.Failed
        };

    [Fact]
    public void ToolCallReturnedAndModelAnswered_IsTheOnlyHonestCompletion()
    {
        var conclusion = RoundOutcome.Classify(
            "All done.", [Returned()], producedClosingText: true);

        conclusion.Verdict.Should().Be(RoundVerdict.Answered);
        conclusion.Status.Should().Be(ThreadMessageStatus.Completed);
        conclusion.IsHonestCompletion.Should().BeTrue();
        conclusion.Diagnosis.Should().BeNull();
    }

    [Fact]
    public void PlainTextRoundWithNoTools_Completes()
    {
        // The overwhelmingly common shape — a chat answer with no tool use at all. The
        // NoFinalAnswer rule must never reach it (the caller seeds the watermark below zero).
        var conclusion = RoundOutcome.Classify(
            "Sure — here is the summary.", [], producedClosingText: true);

        conclusion.Verdict.Should().Be(RoundVerdict.Answered);
    }

    [Fact]
    public void DispatchedToolCallWithNoResult_IsNotCompleted_AndTheEntryStopsClaimingSuccess()
    {
        var conclusion = RoundOutcome.Classify(
            "Confirmed — the body is now saved correctly with all nine bullets.",
            [Dispatched()], producedClosingText: true);

        conclusion.Verdict.Should().Be(RoundVerdict.ToolCallUnfinished);
        conclusion.Status.Should().Be(ThreadMessageStatus.Error);
        conclusion.IsHonestCompletion.Should().BeFalse();
        conclusion.Diagnosis.Should().Contain("CreateEvent", "the diagnosis must be actionable");

        var entry = conclusion.ToolCalls.Should().ContainSingle().Subject;
        entry.Status.Should().Be(ToolCallStatus.Failed);
        entry.IsSuccess.Should().BeFalse();
        entry.Result.Should().BeNull(
            "Result is deliberately left untouched — the cell merge prefers whichever side carries "
            + "one, so a placeholder here would clobber a real terminal result that reached the "
            + "cell through the delegation stamp but not this log");
    }

    [Fact]
    public void UnfinishedCallOutranksAMissingAnswer()
    {
        // Rule ORDER is itself the diagnosis: a round that lost a tool call has no answer to miss,
        // and the lost call is the fact worth reporting.
        var conclusion = RoundOutcome.Classify(
            "partial", [Dispatched()], producedClosingText: false);

        conclusion.Verdict.Should().Be(RoundVerdict.ToolCallUnfinished);
    }

    [Fact]
    public void OnlyTheUnfinishedEntriesAreRestamped()
    {
        var conclusion = RoundOutcome.Classify(
            "text", [Returned("Search"), Dispatched("CreateEvent"), Returned("Get", success: false)],
            producedClosingText: true);

        conclusion.ToolCalls[0].Status.Should().Be(ToolCallStatus.Success, "it returned");
        conclusion.ToolCalls[1].Status.Should().Be(ToolCallStatus.Failed, "it never returned");
        conclusion.ToolCalls[2].Status.Should().Be(ToolCallStatus.Failed, "it returned a failure");
        conclusion.Diagnosis.Should().Contain("CreateEvent").And.NotContain("Search");
    }

    [Fact]
    public void MidFlightDelegationCarryingProgress_IsNotCountedUnfinished()
    {
        // A Streaming delegation carries a live progress projection in Result; it is neither
        // "pending" to the UI nor unfinished here. Reusing ToolCallVisibility.IsPending rather than
        // inventing a second notion of pending is what keeps these two views in agreement.
        var delegation = new ToolCallEntry
        {
            Name = "delegate_to_agent",
            Status = ToolCallStatus.Streaming,
            Result = "…sub-thread output so far…",
            DelegationPath = "x/_Thread/y"
        };

        RoundOutcome.IsUnfinished(delegation).Should().BeFalse();
        RoundOutcome.Classify("text", [delegation], producedClosingText: true)
            .Verdict.Should().Be(RoundVerdict.Answered);
    }

    [Fact]
    public void CancelledToolCall_IsAlreadyTerminal_AndNotReclassified()
    {
        var cancelled = new ToolCallEntry { Name = "Get", Status = ToolCallStatus.Cancelled };

        RoundOutcome.IsUnfinished(cancelled).Should().BeFalse();
        RoundOutcome.Classify("stopped", [cancelled], producedClosingText: true)
            .ToolCalls.Should().ContainSingle().Which.Status.Should().Be(ToolCallStatus.Cancelled);
    }

    [Fact]
    public void ZeroTextAndZeroToolCalls_IsNoOutput_NotCompleted()
    {
        var conclusion = RoundOutcome.Classify("", [], producedClosingText: true);

        conclusion.Verdict.Should().Be(RoundVerdict.NoOutput);
        conclusion.Status.Should().Be(ThreadMessageStatus.Error,
            "an agent that streamed zero tokens produced nothing — a placeholder sentence stamped "
            + "Completed told the user the round had succeeded");
        conclusion.Diagnosis.Should().Contain("zero tokens");
    }

    [Fact]
    public void ToolCallsRanButClosingTurnWasSilent_IsNoFinalAnswer()
    {
        var conclusion = RoundOutcome.Classify(
            "Creating the space now — `path` = id = `ClientE",
            [Returned("Create")], producedClosingText: false);

        conclusion.Verdict.Should().Be(RoundVerdict.NoFinalAnswer);
        conclusion.Status.Should().Be(ThreadMessageStatus.Error);
        conclusion.Diagnosis.Should().Contain("closing answer");
        conclusion.ToolCalls.Should().ContainSingle().Which.Status.Should().Be(ToolCallStatus.Success,
            "the tools DID return — only the final turn is missing");
    }

    [Fact]
    public void NullTextIsTreatedAsEmpty()
        => RoundOutcome.Classify(null, [], producedClosingText: true)
            .Verdict.Should().Be(RoundVerdict.NoOutput);

    [Fact]
    public void NullToolCallList_Throws_RatherThanSilentlyPassing()
        => Assert.Throws<ArgumentNullException>(
            () => RoundOutcome.Classify("text", null!, producedClosingText: true));
}
