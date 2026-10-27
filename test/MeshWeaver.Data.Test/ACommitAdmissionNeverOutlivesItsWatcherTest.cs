using System;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// The commit decision of a cross-hub patch is atomic against the end of its echo watcher
/// (Systemorph/MeshWeaver#7346 review): a turn whose watcher has ended commits NOTHING, and the watcher
/// never releases the admission of a turn that is still deciding — so no write commits without an
/// admission a deletion can wait on. Each interleaving is driven step by step on
/// <see cref="CommitAdmissionHandoff"/>; the admission is a probe that counts its releases.
/// </summary>
public class ACommitAdmissionNeverOutlivesItsWatcherTest
{
    private sealed class Probe : IDisposable
    {
        public int Releases { get; private set; }
        public void Dispose() => Releases++;
    }

    [Fact]
    public void AWatcherThatEndedBeforeTheTurnAbortsTheTurn()
    {
        var handoff = new CommitAdmissionHandoff();
        var admission = new Probe();

        handoff.OnWatcherEnded();

        handoff.TryBeginDecision(admission).Should().BeFalse(
            "the watcher is the only route to the flush — a turn after its end must commit nothing");
        admission.Releases.Should().Be(1, "an aborted turn holds nothing a deletion could wait on");
    }

    [Fact]
    public void AWatcherEndingWhileTheTurnDecidesLeavesTheAdmissionToTheTurnWhichAborts()
    {
        var handoff = new CommitAdmissionHandoff();
        var admission = new Probe();
        handoff.TryBeginDecision(admission).Should().BeTrue();

        handoff.OnWatcherEnded();
        admission.Releases.Should().Be(0,
            "the watcher must not release the admission of a turn that is still deciding");

        handoff.TryCommit().Should().BeFalse("the turn sees the watcher's end at its commit point");
        admission.Releases.Should().Be(1, "the aborting turn releases its own admission");
    }

    [Fact]
    public void ACommittedTurnHandsItsAdmissionToTheFlush()
    {
        var handoff = new CommitAdmissionHandoff();
        var admission = new Probe();
        handoff.TryBeginDecision(admission).Should().BeTrue();
        handoff.TryCommit().Should().BeTrue();

        var taken = handoff.TakeForFlush();
        taken.Should().BeSameAs(admission);
        handoff.OnWatcherEnded();
        admission.Releases.Should().Be(0, "the flush holds it until its own storage write answers");

        taken!.Dispose();
        admission.Releases.Should().Be(1);
    }

    [Fact]
    public void ACommittedTurnWhoseEchoNeverComesIsReleasedAtTheWatchersEnd()
    {
        var handoff = new CommitAdmissionHandoff();
        var admission = new Probe();
        handoff.TryBeginDecision(admission).Should().BeTrue();
        handoff.TryCommit().Should().BeTrue();

        handoff.OnWatcherEnded();

        admission.Releases.Should().Be(1, "no flush will follow, so nothing may wait for it");
        handoff.TakeForFlush().Should().BeNull();
    }

    [Fact]
    public void ATurnThatCommitsNothingReleasesAndAWatcherEndAfterwardsIsHarmless()
    {
        var handoff = new CommitAdmissionHandoff();
        var admission = new Probe();
        handoff.TryBeginDecision(admission).Should().BeTrue();

        handoff.Abandon();
        admission.Releases.Should().Be(1);

        handoff.OnWatcherEnded();
        admission.Releases.Should().Be(1, "a released admission is released exactly once");
    }

    [Fact]
    public void AnAbortDuringOwnerTeardownLeavesTheVerdictToTheDisposalNack()
    {
        // While the owner shuts down with a route armed for the ShutDown-phase disposal NACK, the echo
        // watcher stands aside for it — and so must the turn that aborts because the watcher ended:
        // claiming the gate would detach that NACK and send the writer's retry into the closing
        // activation (#7346 review).
        var answers = 0;
        DataExtensions.AnswerWatcherEndedAbort(() => true, (_, _) => answers++, "owner/path");
        answers.Should().Be(0, "the disposal NACK, not the aborting turn, claims the verdict");

        // Negative control: with no disposal NACK to stand aside for, the abort answers itself.
        MeshWeaver.Mesh.MeshNodeError? error = null;
        DataExtensions.AnswerWatcherEndedAbort(() => false, (success, e) =>
        {
            answers++;
            success.Should().BeFalse();
            error = e;
        }, "owner/path");
        answers.Should().Be(1);
        error!.Code.Should().Be(MeshWeaver.Mesh.MeshNodeErrorCode.OwnerNotReady);
    }
}
