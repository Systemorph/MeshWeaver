using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A re-attempt spawned by a "the patch NEVER applied" NACK must not conclude "already applied"
/// from this hub's mirror — issue #3477.
///
/// <para><b>The defect, end to end.</b> On the owner the ECHO PRECEDES THE DURABLE FLUSH:
/// <c>PATCH_MERGE_STAMPED → PATCH_ECHO_SEEN → IPostCommitFlush.Flush</c>
/// (<c>MeshWeaver.Data/DataExtensions.cs</c>). A teardown fault raised on that flush leg is
/// classified <see cref="MeshNodeErrorCode.OwnerDisposing"/> by <c>ClassifyPatchException</c> —
/// deliberately, since #3499, because the alternative (<c>Unknown</c>) is terminal and loses the
/// write outright. So the population of writes taking the OwnerDisposing re-enqueue arm is exactly
/// the population whose merge has ALREADY been echoed into the writing hub's mirror while storage
/// was left untouched.</para>
///
/// <para><c>UpdateRemote</c>'s re-enqueue then passed <c>refusedBaseVersion = 0</c>, which reduces
/// <c>RebaseSource</c> to <c>mirror.Take(1)</c>. The re-attempt read the mirror, found its own value
/// already there, computed an EMPTY merge patch, posted NOTHING, and completed the caller as a
/// SUCCESS carrying the value it had never persisted. Every line on that path is
/// <c>LogDebug</c>.</para>
///
/// <para><b>That is the measurement in #3477</b>: after
/// <c>LATE_NACK_REENQUEUE … code=OwnerDisposing attempt=1</c>, nothing landed and nothing was logged
/// for 45 s, and the test failed on its DURABLE-STORAGE poll rather than on the caller's verdict.
/// Enumerating every terminal the re-attempt can reach, that is the only one that is BOTH silent and
/// leaves storage unchanged: the base-state wait faults at 30 s (<c>LATE_NACK_REENQUEUE failed</c>,
/// Warning), an empty base faults at once (same Warning), a second NACK logs
/// <c>OWNER_NACK_REENQUEUE</c> / <c>LATE_NACK_REENQUEUE</c> (Warning), silence after the post logs
/// <c>VERDICT_TIMEOUT</c> at 31 s (Warning) — all inside the 45 s the test waited — and every
/// arm that ACKS lands the write.</para>
///
/// <para><b>The rule asserted here.</b> The mirror cannot tell a phantom (merge echoed, flush died)
/// apart from a merge that committed AND flushed, because both look like "my value is already
/// there". Only the OWNER can. So when the owner has just said the write never applied, a mirror
/// base that already carries it is discarded for an authoritative owner read. The ordinary write —
/// first attempt, CONFLICT re-attempt, or a "never applied" re-attempt whose mirror yields a REAL
/// diff — is untouched and pays no extra read.</para>
///
/// <para>Deterministic by construction: both bases are seams, the "already carries the write" test
/// is the caller's own lambda, and nothing here needs a hub, a cluster, a scheduler or a wall
/// clock.</para>
/// </summary>
public class ReattemptBaseIsAuthoritativeTest
{
    private const string Path = "rbuergi/TestData/late-nack-node";

    /// <summary>The write under test: the same shape as <c>LateNackReenqueueTest</c>'s.</summary>
    private const string Marker = "post-nack-4b7556aa5983";

    private static MeshNode Update(MeshNode node) => node with { Name = Marker };

    /// <summary>
    /// 🚨 These cases hand <see cref="MeshNodeStreamHandle.ReattemptBaseSource"/> the caller's
    /// LAMBDA, never a predicate — the seam computes the no-write decision itself
    /// (<see cref="MeshNodeStreamHandle.PostsNothing"/>), so there is no way for this test to
    /// supply one rule while the production call site uses another. An earlier version of this file
    /// injected the predicate and spelled the record comparison out locally, which is exactly how a
    /// guard covering only ONE of the write path's two no-write exits stayed green (#3477).
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>A typed content payload — what a caller's lambda produces, against a mirror that
    /// holds the same value as untyped JSON.</summary>
    private sealed record Payload(string Text);

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

    /// <summary>The state the dying activation echoed but never flushed — the phantom.</summary>
    private static MeshNode Phantom => new(Path) { Name = Marker, Version = 5 };

    /// <summary>What the OWNER actually holds: the pre-write state, loaded from storage.</summary>
    private static MeshNode OwnerState => new(Path) { Name = "initial", Version = 4 };

    /// <summary>
    /// 🚨 THE REGRESSION. The owner NACKed <c>OwnerDisposing</c> — "this patch never applied" — and
    /// the mirror nevertheless carries the write, because the merge was echoed before the flush that
    /// died. The re-attempt must NOT diff against that.
    ///
    /// <para>RED before the fix: the base was <c>mirror.Take(1)</c>, so this emitted the phantom,
    /// <c>update</c> was a no-op against it, no <c>PatchDataRequest</c> was posted, and the caller
    /// was completed as a success for a write that was in no store anywhere.</para>
    /// </summary>
    [Fact]
    public void NeverAppliedReattempt_WhoseMirrorCarriesThePhantom_RebasesOnTheOwner()
    {
        var phantomNoted = 0;

        var observed = Watch(MeshNodeStreamHandle.ReattemptBaseSource(
            ownerSaidNeverApplied: true,
            mirrorBase: Observable.Return(Phantom),
            authoritativeBase: Observable.Return<MeshNode?>(OwnerState),
            update: Update,
            jsonOptions: JsonOptions,
            path: Path,
            onPhantomBase: () => phantomNoted++));

        observed.Error.Should().BeNull();
        observed.Values.Should().ContainSingle(
            "a write diffs against exactly one base")
            .Which.Name.Should().Be("initial",
                "the owner has just stated it does not hold this write, so a mirror that shows it "
                + "is an echo of a merge whose durable flush died with the activation. Diffing "
                + "against that phantom produces an EMPTY patch, posts nothing, and reports the "
                + "write as saved when it is in no store at all — #3477's silent loss.");
        phantomNoted.Should().Be(1, "the swap must be nameable in the log, not invisible");
    }

    /// <summary>
    /// 🚨 THE RESIDUAL #3633 LEFT OPEN — the same phantom, reached through the write path's OTHER
    /// no-write exit.
    ///
    /// <para><c>UpdateRemote</c> decides not to post a patch in TWO places: the record comparison
    /// (<c>IsRecordNoOp</c>), and — for a lambda whose output is not record-equal — the SERIALISED
    /// merge patch coming out empty (<c>NO-OP … diff empty after serialisation</c>). Both post
    /// nothing, both complete the caller as a SUCCESS, both log at Debug. #3633's phantom guard was
    /// written as the record comparison alone, so a phantom base that fails record equality but
    /// serialises identically walked past the guard and out through the second exit — into exactly
    /// the silent loss the guard exists to refuse.</para>
    ///
    /// <para><b>Not a contrived pair — it is the cache hub's ordinary shape.</b>
    /// <c>MeshNode.ContentEquals</c> compares two <c>JsonElement</c>s structurally, but a MIXED
    /// pair — one <c>JsonElement</c>, one typed — is <c>false</c> by construction, and says so:
    /// "no JsonSerializerOptions is available here to bridge representations". The mirror
    /// <c>UpdateRemote</c> reads lives on the cache hub, "whose hub does not know domain types",
    /// so its content IS a <c>JsonElement</c>; the caller's lambda produces a TYPED content. Every
    /// such write is not record-equal and serialises identically when the value has not changed —
    /// which is exactly what the write path's own comment predicts ("a rebuilt-but-identical
    /// content slips past the record-Equals check above") and why gate 2 exists at all.
    /// <c>MeshNode.SerializedEquals</c> documents the same three witnesses.</para>
    ///
    /// <para><b>Falsified.</b> Narrowing <c>PostsNothing</c> back to the record comparison alone
    /// (the pre-#3477 predicate) turns this case red on the BEHAVIOUR assertion, measured:
    /// <c>Expected value to be "initial" … but found "post-nack-4b7556aa5983"</c> — the re-attempt
    /// diffing against the phantom, which is the loss itself. The first assertion states why the old
    /// guard cannot see it; the last states why the write path would nevertheless post nothing.</para>
    /// </summary>
    [Fact]
    public void NeverAppliedReattempt_WhoseMirrorCarriesAPhantomThatIsNotRecordEqual_StillRebasesOnTheOwner()
    {
        // The cache hub's mirror carries UNTYPED content; the caller's lambda produces a TYPED
        // value. ContentEquals refuses that mixed pair outright, and the two serialise identically.
        // 🚨 The mirror's JSON is produced by the SAME serializer the diff uses, so the two sides
        // are byte-identical under WHATEVER options are supplied — hub options included, with their
        // naming policy and polymorphic discriminator. A hand-written literal here would pin only
        // the default options and could disagree with production (which is how the mirror gets its
        // value in the first place: the owner serialised it with the hub's own options).
        static JsonElement Mirrored() =>
            JsonSerializer.SerializeToElement<object>(new Payload("unchanged"), JsonOptions);
        static MeshNode UpdateRebuildingContent(MeshNode node) =>
            node with { Name = Marker, Content = new Payload("unchanged") };

        var phantom = new MeshNode(Path) { Name = Marker, Version = 5, Content = Mirrored() };
        var owned = new MeshNode(Path) { Name = "initial", Version = 4, Content = Mirrored() };

        // The two halves of the gap, stated before the behaviour: gate 1 does not see this base,
        // and the write path would nevertheless post nothing against it.
        MeshNodeStreamHandle.IsRecordNoOp(phantom, UpdateRebuildingContent(phantom)).Should().BeFalse(
            "the pre-#3477 guard was this comparison alone, and it does NOT see this phantom — an "
            + "untyped mirror value and a typed one are never record-equal, whatever they hold");

        var phantomNoted = 0;
        var observed = Watch(MeshNodeStreamHandle.ReattemptBaseSource(
            ownerSaidNeverApplied: true,
            mirrorBase: Observable.Return(phantom),
            authoritativeBase: Observable.Return<MeshNode?>(owned),
            update: UpdateRebuildingContent,
            jsonOptions: JsonOptions,
            path: Path,
            onPhantomBase: () => phantomNoted++));

        observed.Error.Should().BeNull();
        observed.Values.Should().ContainSingle().Which.Name.Should().Be("initial",
            "the owner has just stated it does not hold this write, and the mirror base would have "
            + "produced an empty patch — posting nothing and reporting success for a write that is "
            + "in no store at all. #3633 closed that exit for a record-equal no-op; this is the "
            + "same exit one gate later (#3477)");
        phantomNoted.Should().Be(1, "the swap must be nameable in the log, not invisible");
        MeshNodeStreamHandle.PostsNothing(phantom, UpdateRebuildingContent(phantom), JsonOptions)
            .Should().BeTrue(
                "…and this is WHY: the write path would post NOTHING against this base, because the "
                + "serialised merge patch is empty. That — not record equality — is the only "
                + "measure that decides whether a PatchDataRequest is sent");
    }

    /// <summary>
    /// The other half of the ambiguity, and why the fix cannot be "always fault on an empty diff":
    /// the merge may have committed AND flushed, with only the ACK lost to teardown. Then the write
    /// IS durable, the no-op is correct, and the caller's success is earned. The authoritative read
    /// is what tells the two apart — and here it says the owner has it.
    /// </summary>
    [Fact]
    public void NeverAppliedReattempt_WhoseOwnerAlreadyHasTheWrite_KeepsTheNoOp()
    {
        var observed = Watch(MeshNodeStreamHandle.ReattemptBaseSource(
            ownerSaidNeverApplied: true,
            mirrorBase: Observable.Return(Phantom),
            authoritativeBase: Observable.Return<MeshNode?>(new MeshNode(Path) { Name = Marker, Version = 5 }),
            update: Update,
            jsonOptions: JsonOptions,
            path: Path));

        observed.Error.Should().BeNull();
        observed.Values.Should().ContainSingle().Which.Name.Should().Be(Marker,
            "the owner itself reports the value, so the merge survived its flush and the re-diff "
            + "is a genuine no-op — faulting here would report failure for a durable write");
    }

    /// <summary>
    /// The ordinary re-attempt: the owner said "never applied" and the mirror agrees — it does NOT
    /// carry the write. Nothing changes, and above all no authoritative read is issued, so the hot
    /// path gains no round-trip and no new failure mode.
    /// </summary>
    [Fact]
    public void NeverAppliedReattempt_WhoseMirrorIsBehind_UsesTheMirrorAndReadsNothingElse()
    {
        var authoritativeSubscribed = 0;
        var authoritative = Observable.Defer(() =>
        {
            authoritativeSubscribed++;
            return Observable.Return<MeshNode?>(OwnerState);
        });

        var observed = Watch(MeshNodeStreamHandle.ReattemptBaseSource(
            ownerSaidNeverApplied: true,
            mirrorBase: Observable.Return(new MeshNode(Path) { Name = "initial", Version = 4 }),
            authoritativeBase: authoritative,
            update: Update,
            jsonOptions: JsonOptions,
            path: Path));

        observed.Values.Should().ContainSingle().Which.Name.Should().Be("initial");
        authoritativeSubscribed.Should().Be(0,
            "the mirror base yields a real diff, so the write proceeds exactly as before — the "
            + "extra read is paid only in the ambiguous case that was losing the write");
    }

    /// <summary>
    /// A FIRST attempt — and a CONFLICT re-attempt, which passes the same <c>false</c> — is not
    /// touched at all, even when its base happens to make the lambda a no-op (an identical upsert, a
    /// guard <c>return node</c>). Those are legitimate no-ops: no owner has said anything about this
    /// write, so there is nothing to contradict.
    /// </summary>
    [Fact]
    public void OrdinaryWrite_KeepsTheMirrorBase_EvenWhenTheLambdaIsANoOp()
    {
        var authoritativeSubscribed = 0;
        var authoritative = Observable.Defer(() =>
        {
            authoritativeSubscribed++;
            return Observable.Return<MeshNode?>(OwnerState);
        });

        var observed = Watch(MeshNodeStreamHandle.ReattemptBaseSource(
            ownerSaidNeverApplied: false,
            mirrorBase: Observable.Return(Phantom),
            authoritativeBase: authoritative,
            update: Update,
            jsonOptions: JsonOptions,
            path: Path));

        observed.Values.Should().ContainSingle().Which.Name.Should().Be(Marker);
        authoritativeSubscribed.Should().Be(0,
            "an ordinary write's no-op is legitimate — nothing has claimed the write did not land");
    }

    /// <summary>
    /// 🚨 Totality, the <c>RequireBaseState</c> doctrine applied to the new seam. The read called in
    /// to disambiguate has three ways to fail to answer — it raises, it emits <c>null</c> ("no node"),
    /// or it ENDS having emitted nothing — and all three must reach the caller as a terminal.
    /// Completing silently is the hang; completing SUCCESSFULLY, or resolving back onto the phantom,
    /// would be the very fail-open this change closes.
    /// </summary>
    [Theory]
    [InlineData("raises")]
    [InlineData("emits null")]
    [InlineData("ends empty")]
    public void PhantomBase_WhoseAuthoritativeReadCannotAnswer_RaisesRatherThanFallingBack(string shape)
    {
        var authoritative = shape switch
        {
            "raises" => Observable.Throw<MeshNode?>(
                new MeshNodeStreamException(new MeshNodeError(
                    MeshNodeErrorCode.OwnerUnreachable, Path, "the read failed"))),
            "emits null" => Observable.Return<MeshNode?>(null),
            _ => Observable.Empty<MeshNode?>(),
        };

        var observed = Watch(MeshNodeStreamHandle.ReattemptBaseSource(
            ownerSaidNeverApplied: true,
            mirrorBase: Observable.Return(Phantom),
            authoritativeBase: authoritative,
            update: Update,
            jsonOptions: JsonOptions,
            path: Path));

        observed.Values.Should().BeEmpty("there was no trustworthy state to diff against");
        observed.Completed.Should().BeFalse(
            "a silent completion settles nothing: UpdateRemote subscribes this source with onNext "
            + "and onError only, so a bare OnCompleted posts no patch and arms no deadline");
        observed.Error.Should().BeOfType<MeshNodeStreamException>(
            "the read that was supposed to disambiguate could not, so the write must be refused "
            + "loudly — never resolved back onto the phantom it was called in to reject");
    }
}
