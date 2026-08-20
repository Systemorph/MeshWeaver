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
        conclusion.LocalizationKey.Should().BeNull("an answered round has nothing to diagnose");
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
        conclusion.LocalizationKey.Should().Be("chat.roundToolCallUnfinished");
        conclusion.UnfinishedToolNames.Should().Equal("CreateEvent");   // named, so the diagnosis is actionable
        conclusion.LocalizationArgs.Should().Equal(1, "CreateEvent");

        var entry = conclusion.ToolCalls.Should().ContainSingle().Subject;
        entry.Status.Should().Be(ToolCallStatus.Failed);
        entry.IsSuccess.Should().BeFalse();
        entry.Result.Should().BeNull(
            "Result is deliberately left untouched — the cell merge prefers whichever side carries "
            + "one, so a placeholder here would clobber a real terminal result that reached the "
            + "cell through the delegation stamp but not this log");
        conclusion.UnfinishedToolNames.Should().Equal("CreateEvent");
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
        // A returned call — successful OR failed — is finished; only the outstanding one is named.
        conclusion.UnfinishedToolNames.Should().Equal("CreateEvent");
    }

    [Fact]
    public void MidFlightDelegation_CountsAsUnfinished_ButIsNotRestamped()
    {
        // A delegation still Streaming when the stream ended has NOT terminated, so the round
        // cannot claim it finished — "unfinished" is the exact complement of the UI's IsCompleted.
        var delegation = new ToolCallEntry
        {
            Name = "delegate_to_agent",
            Status = ToolCallStatus.Streaming,
            Result = "…sub-thread output so far…",
            DelegationPath = "x/_Thread/y"
        };

        RoundOutcome.IsUnfinished(delegation).Should().BeTrue();

        var conclusion = RoundOutcome.Classify("text", [delegation], producedClosingText: true);
        conclusion.Verdict.Should().Be(RoundVerdict.ToolCallUnfinished);
        conclusion.UnfinishedToolNames.Should().Equal("delegate_to_agent");

        // …but the ENTRY is left exactly as it was. The cell merge keeps the cell's terminal
        // status precisely when the incoming one is Streaming, and prefers whichever side carries
        // a Result — converting it to Failed here, or overwriting the live progress projection,
        // would defeat one guard each and clobber a terminal stamp that reached the cell first.
        var entry = conclusion.ToolCalls.Should().ContainSingle().Subject;
        entry.Status.Should().Be(ToolCallStatus.Streaming);
        entry.Result.Should().Be("…sub-thread output so far…");
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
        conclusion.LocalizationKey.Should().Be("chat.roundNoOutput");
    }

    [Fact]
    public void ToolCallsRanButClosingTurnWasSilent_IsNoFinalAnswer()
    {
        var conclusion = RoundOutcome.Classify(
            "Creating the space now — `path` = id = `ClientE",
            [Returned("Create")], producedClosingText: false);

        conclusion.Verdict.Should().Be(RoundVerdict.NoFinalAnswer);
        conclusion.Status.Should().Be(ThreadMessageStatus.Error);
        conclusion.LocalizationKey.Should().Be("chat.roundNoFinalAnswer");
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
