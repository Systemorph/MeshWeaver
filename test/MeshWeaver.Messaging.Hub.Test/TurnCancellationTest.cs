using System;
using System.Threading;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Issue #6958 — <c>MessageService.RunHandler</c> threw
/// <c>ObjectDisposedException: The CancellationTokenSource has been disposed.</c> on a
/// <c>content-type-registration</c> hub torn down during its own initialization.
///
/// <para>The turn read a <c>volatile CancellationTokenSource</c> field and then called
/// <c>.Token</c> on it, while <c>CancelExecution</c> — invoked OFF the action block by the
/// disposal of a hub that never finished starting — swapped a fresh source in and cancelled and
/// disposed the old one. A turn whose read landed before the swap dereferenced a disposed source.
/// The interleave is two instructions wide, so these tests pin it deterministically by splitting it
/// at the seam: the reader's SNAPSHOT is taken, the roll runs, and only then is the token read.</para>
/// </summary>
public class TurnCancellationTest
{
    /// <summary>
    /// The negative control — the mechanism the old shape relied on. A reader holding the SOURCE
    /// across a cancel-and-dispose cannot read its token; this is the exception in the incident.
    /// If this ever stops throwing, the test below has stopped proving anything.
    /// </summary>
    [Fact]
    public void A_reader_holding_the_source_across_a_dispose_cannot_read_its_token()
    {
        var snapshot = new CancellationTokenSource();
        snapshot.Cancel();
        snapshot.Dispose();

        Action read = () => _ = snapshot.Token;

        read.Should().Throw<ObjectDisposedException>(
            "this is the incident's exception: .Token on a disposed CancellationTokenSource throws");
    }

    [Fact]
    public void A_reader_that_snapshotted_before_a_roll_reads_the_cancelled_token_without_throwing()
    {
        var cancellation = new TurnCancellation();
        var snapshot = cancellation.Current; // the turn's read of the field, before the swap

        cancellation.CancelAndRoll().Should().BeTrue("the generation was live when it was retired");

        Action read = () => _ = snapshot.Token;
        read.Should().NotThrow("the token was captured while its source was alive (#6958)");
        snapshot.Token.IsCancellationRequested.Should().BeTrue(
            "a turn that started under the retired generation is exactly the turn the roll cancels");
        Action register = () => snapshot.Token.Register(() => { }).Dispose();
        register.Should().NotThrow("Register on an already-cancelled token never touches the disposed source");
    }

    [Fact]
    public void A_roll_hands_later_turns_a_fresh_live_token()
    {
        var cancellation = new TurnCancellation();
        var before = cancellation.Token;

        cancellation.CancelAndRoll();

        before.IsCancellationRequested.Should().BeTrue();
        cancellation.Token.IsCancellationRequested.Should().BeFalse(
            "subsequent turns must not inherit the cancellation meant for the wedged one");
        cancellation.Token.Should().NotBe(before);
    }

    [Fact]
    public void Two_rolls_each_retire_their_own_generation_exactly_once()
    {
        var cancellation = new TurnCancellation();
        var first = cancellation.Current;

        cancellation.CancelAndRoll().Should().BeTrue();
        var second = cancellation.Current;
        cancellation.CancelAndRoll().Should().BeTrue(
            "the second roll claims the SECOND generation — it never re-cancels or re-disposes the first");

        first.Token.IsCancellationRequested.Should().BeTrue();
        second.Token.IsCancellationRequested.Should().BeTrue();
        cancellation.Token.IsCancellationRequested.Should().BeFalse();
    }
}
