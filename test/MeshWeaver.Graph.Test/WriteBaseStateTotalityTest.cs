using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A cross-hub write must ALWAYS reach a terminal — issue #3001.
///
/// <para><b>The defect.</b> <c>MeshNodeStreamHandle.UpdateRemote</c> reads the node's current state
/// off this hub's mirror, runs the caller's lambda against it, diffs, and posts the patch. It
/// subscribes that base read with <c>Subscribe(onNext, onError)</c> — two of Rx's three
/// terminations — and EVERY verdict the caller can ever receive is raised from inside one of those
/// two callbacks: the no-op short-circuits, the owner's ack, every NACK, the delivery failure, and
/// the outer <c>VERDICT_TIMEOUT</c> deadline (which is armed inside the response wait, so a write
/// that never posts never arms it either). A source that COMPLETES WITHOUT A VALUE therefore
/// settles nothing at all: no OnNext, no OnError, no OnCompleted on the caller's observer, no
/// patch, and no deadline. The writer waits for the life of the process, and nothing is logged —
/// from the message layer's point of view the request that started it succeeded long ago.</para>
///
/// <para><b>That completion is not hypothetical.</b> <c>Workspace.AcquireRemoteStreamUnchecked</c>
/// deliberately hands back a stream that was already dead when it resolved — its own comment says
/// "hand it back with an empty lease and let the caller's subscribe collect the terminal" — and a
/// disposed synchronization stream replays exactly one thing: <c>OnCompleted</c>. The same shape
/// arises when the last lease on an evicted mirror is released mid-write (<c>ReclaimIfUnheld</c>
/// disposes it there and then) and when the write path's own
/// <c>Where(change =&gt; change.Value is not null)</c> filter drops every emission the mirror had.
/// With N writers sharing one leased mirror, one of them collecting that bare completion reads
/// exactly as "N writes started, N-1 finished" — which is the trace on #3001.</para>
///
/// <para><b>What is asserted</b> is the TERMINAL, not the value: a base read that ends without
/// state must raise, because no base means no diff, which means no <c>PatchDataRequest</c> was ever
/// posted and the write PROVABLY did not land. Completing silently is the hang; completing
/// SUCCESSFULLY would be the fail-open #2661 closed twice — reporting "saved" for a write nobody
/// attempted.</para>
///
/// <para>Deterministic by construction: <see cref="Observable.Empty{TResult}()"/> IS a disposed
/// mirror's replay, and it terminates synchronously on Subscribe. No mesh, no cluster, no wall
/// clock, no scheduler.</para>
/// </summary>
public class WriteBaseStateTotalityTest
{
    private const string Path = "charlie/_UserActivity/charlie_doc";

    /// <summary>What a subscriber actually observed — all three Rx terminations, separately.</summary>
    private sealed record Observed(IReadOnlyList<MeshNode> Values, Exception? Error, bool Completed);

    private static Observed Watch(IObservable<MeshNode> source)
    {
        var values = new List<MeshNode>();
        Exception? error = null;
        var completed = false;
        using var subscription = source.Subscribe(values.Add, ex => error = ex, () => completed = true);
        return new Observed(values, error, completed);
    }

    /// <summary>
    /// 🚨 THE REGRESSION. The ORDINARY write — a first attempt, <c>refusedBaseVersion == 0</c>,
    /// which is every caller write in the mesh — whose mirror ends without carrying the node.
    ///
    /// <para>RED before the fix: the branch was a bare <c>mirror.Take(1)</c>, so this completed
    /// with no value and no error, and <c>UpdateRemote</c>'s two-callback subscribe dropped it on
    /// the floor. The totality guard that makes this GREEN already existed — on the CONFLICT
    /// re-attempt branch only, i.e. the rare one — with the rule spelled out beside it: "An EMPTY
    /// completion must not reach the caller … A source that cannot answer must SAY so."</para>
    /// </summary>
    [Fact]
    public void FirstAttempt_WhoseMirrorEndsWithoutState_RaisesATerminal()
    {
        var observed = Watch(MeshNodeStreamHandle.RebaseSource(
            Observable.Empty<MeshNode>(), refusedBaseVersion: 0, onStaleMirror: _ => { }));

        observed.Values.Should().BeEmpty("there was no state to diff against");
        observed.Completed.Should().BeFalse(
            "a silent completion is the hang itself: UpdateRemote subscribes this source with "
            + "onNext and onError only, so a bare OnCompleted settles NOTHING — the caller's "
            + "observer is never called, no patch is posted, and not even the outer verdict "
            + "deadline is armed. The writer waits forever with nothing logged.");
        observed.Error.Should().NotBeNull(
            "no base means no diff and no PatchDataRequest, so the write PROVABLY did not land — "
            + "the caller must be told, exactly as it is told for every other unlanded write");
        observed.Error!.Message.Should().Contain("did NOT land",
            "the message must say the write did not happen, so a caller can re-issue it");
    }

    /// <summary>
    /// The CONFLICT re-attempt keeps its OWN, more specific diagnostic. The guard is now applied at
    /// one seam for both branches, and that must not flatten the two messages into one: "the mirror
    /// never carried the node" and "the owner refused this at version N and the mirror never moved
    /// past it" send an operator to different places.
    /// </summary>
    [Fact]
    public void ConflictReattempt_WhoseMirrorNeverAdvances_KeepsItsOwnDiagnostic()
    {
        var observed = Watch(MeshNodeStreamHandle.RebaseSource(
            Observable.Empty<MeshNode>(), refusedBaseVersion: 7, onStaleMirror: _ => { }));

        observed.Completed.Should().BeFalse();
        observed.Error.Should().NotBeNull();
        observed.Error!.Message.Should().Contain("7",
            "the re-attempt's diagnostic names the version the owner refused");
        observed.Error.Message.Should().Contain("re-apply against",
            "…and says what is missing: state newer than the refused version");
    }

    /// <summary>
    /// The guard must be invisible to every write that HAS a base — which is all of them, normally.
    /// A totality guard that changed the ordinary path would be a far worse bug than the one it
    /// closes, so pin that the value passes through untouched and the source still completes.
    /// </summary>
    [Fact]
    public void AMirrorThatCarriesTheNode_PassesItThroughUnchanged()
    {
        var live = new MeshNode(Path) { Version = 4 };

        var observed = Watch(MeshNodeStreamHandle.RebaseSource(
            Observable.Return(live), refusedBaseVersion: 0, onStaleMirror: _ => { }));

        observed.Error.Should().BeNull();
        observed.Values.Should().ContainSingle().Which.Should().BeSameAs(live,
            "the ordinary write path gains no filter and no substitution — the guard only adds a "
            + "terminal to the case that had none");
        observed.Completed.Should().BeTrue("a source that answered still completes normally");
    }
}
