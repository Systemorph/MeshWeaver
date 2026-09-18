using System;
using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A holder's build ENDING — the terminal transition, and the one that decides whether a
/// fingerprint ever gets its GO (#4708).
///
/// <para><b>The defect.</b> <c>CompleteBuild</c> / <c>FailBuild</c> went through
/// <c>UpdateBuildAsHolder</c>, which asks <c>ClaimedBy == me</c> and returns the node UNCHANGED
/// when the answer is no. A holder does not own the Build node, so that question is put to a copy
/// it does not own — this hub's mirror, or the locally computed state its own predecessor on the
/// stream cache's per-path write queue handed forward
/// (<c>MeshNodeStreamHandle.PatchBaseSource</c>). When that copy is stale the lambda returns the
/// node unchanged, <c>IsRecordNoOp</c> fires, <b>nothing is posted, and the caller is completed as
/// a SUCCESS</b> — no exception, nothing logged above <c>Debug</c>, nothing to grep. The build ends
/// on nothing: no GO on the history, <c>ObserveBuildGo</c> never emits, and every silo's readiness
/// probe stays down.</para>
///
/// <para><b>Why it fired where it did.</b> The predecessor of a completion is very often ANOTHER
/// completion, and a completion's locally computed state carries <c>ClaimedBy = null</c> — precisely
/// the value that makes the next holder's guard fail. That asymmetry is why all six measured
/// failures of <c>BuildCoordinationTest.ClaimQueue_Go_And_HolderGuard</c> (five branches, including
/// <c>main</c>) were the same ~15 s timeout on the wait for the SECOND holder's GO, and never on
/// the first.</para>
///
/// <para><b>The fix, and what these tests assert.</b> The holder STATES how its build ended under
/// its own key (<c>BuildNodeType.RecordOutcome</c> → <see cref="BuildState.ReportedOutcomes"/>) and
/// the node's OWN hub — serialised against state that never regresses — draws the conclusion
/// (<see cref="BuildNodeType.FoldReportedOutcomes"/>). The superseded-builder property the old
/// guard existed for is PRESERVED, not dropped: it moves to the one place that can tell a
/// superseded builder from a stale copy.</para>
///
/// <para>🚨 Every assertion here is on the EFFECT — the GO on the history, the claim freed, the
/// report consumed — never on the write REPORTING success, which is what the defect already did.
/// Everything is a decision over constructed state with no wall-clock and no concurrency, in the
/// same family as <c>BuildGrantPublicationTest</c>: the interleaving is BUILT, never raced for.</para>
/// </summary>
public class AHoldersCompletionLandsOnTheOwnerTest
{
    private static readonly JsonSerializerOptions Options = new();
    private static readonly DateTime T0 = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    private const string HolderA = "holder-a";
    private const string HolderB = "holder-b";

    private static readonly BuildGo GoA = new("fp-a", T0);
    private static readonly BuildGo GoB = new("fp-b", T0.AddMinutes(1));

    private static MeshNode Node(BuildState state) =>
        new("Build", "Admin") { NodeType = BuildNodeType.NodeType, Content = state };

    private static BuildState StateOf(MeshNode node) => node.ContentAs<BuildState>(Options)!;

    /// <summary>The history after the FIRST holder's build — one GO, nobody holding.</summary>
    private static ImmutableDictionary<string, BuildGo> HistoryWithA =>
        ImmutableDictionary<string, BuildGo>.Empty.Add("fp-a", GoA);

    /// <summary>
    /// What the second holder's write queue hands its lambda: the state its PREDECESSOR — the
    /// first holder's completion — computed locally. The build is free here and nobody holds it,
    /// because that is what completing does.
    /// </summary>
    private static BuildState TheWritersStaleCopy => new()
    {
        Status = BuildStatus.Ready,
        Ready = HistoryWithA,
        ClaimedBy = null,
    };

    /// <summary>
    /// What the OWNER actually holds at the same instant: it granted the second holder and, writing
    /// the node it owns, never regressed past that.
    /// </summary>
    private static BuildState TheOwnersState => new()
    {
        Status = BuildStatus.Planning,
        Ready = HistoryWithA,
        ClaimedBy = HolderB,
        ClaimedAt = T0,
        HeartbeatAt = T0,
        FrameworkVersion = "fp-b",
    };

    // ── THE CONTROL ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE CONTROL. The second holder completes while its own copy says the build is unclaimed —
    /// the exact state its queue predecessor's completion produced — and the GO still reaches the
    /// authoritative record.
    ///
    /// <para>It fails on a tree where the holder decides its own holdership: there the write is a
    /// no-op, the report never exists, and <c>Ready</c> never gains <c>fp-b</c>. Note what is NOT
    /// asserted — that the write reported success. It always did; that is the defect.</para>
    /// </summary>
    [Fact]
    public void ACompletionReportedFromAStaleCopy_StillLandsItsGo()
    {
        // The holder's half, on the copy it was handed. Unconditional by construction.
        var reported = BuildNodeType.RecordOutcome(
            Node(TheWritersStaleCopy), TheWritersStaleCopy, HolderB,
            BuildOutcome.Completed(T0.AddMinutes(1), GoB));

        StateOf(reported).ReportedOutcomes.Should().ContainKey(
            HolderB,
            "the report is a FACT the holder states, so it is in the patch whatever its copy showed");

        // …and the owner's half, against the state the owner really holds.
        var folded = BuildNodeType.FoldReportedOutcomes(
            Node(TheOwnersState with { ReportedOutcomes = StateOf(reported).ReportedOutcomes }),
            Options);

        var after = StateOf(folded);
        after.Ready.Should().ContainKey("fp-b", "THE effect: this fingerprint now has its GO");
        after.Ready.Should().ContainKey("fp-a", "a newer build never revokes an older GO");
        after.Status.Should().Be(BuildStatus.Ready);
        after.ClaimedBy.Should().BeNull("completing frees the build for the next candidate");
        after.ClaimedByIdentity.Should().BeNull();
        after.ClaimedAt.Should().BeNull();
        after.HeartbeatAt.Should().BeNull();
        after.ReportedOutcomes.Should().BeNull("a report is CONSUMED, never accumulated");
    }

    /// <summary>
    /// …and the whole pass end to end: the fold runs BEFORE the election, so the build the second
    /// holder just finished with is free in time for the next candidate to be granted in the SAME
    /// serialised write — not on a later tick.
    /// </summary>
    [Fact]
    public void TheOwnerFoldsTheOutcome_AndElectsTheNextCandidateInTheSamePass()
    {
        var reported = TheOwnersState with
        {
            ReportedOutcomes = ImmutableDictionary<string, BuildOutcome>.Empty
                .Add(HolderB, BuildOutcome.Completed(T0.AddMinutes(1), GoB)),
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("next-image", new BuildClaimRequest("fp-next", T0)),
        };

        var after = StateOf(BuildNodeType.ArbitrateOnMirror(
            Node(reported), Options, T0.Add(BuildNodeType.GrantSettleWindow).AddMinutes(2)));

        after.Ready.Should().ContainKey("fp-b");
        after.ClaimedBy.Should().Be("next-image");
        after.FrameworkVersion.Should().Be("fp-next");
        after.ReportedOutcomes.Should().BeNull();
    }

    // ── the property the old guard existed for, kept ────────────────────────────────────────────

    /// <summary>
    /// 🚨 A SUPERSEDED builder's report lands on nothing — the property <c>UpdateBuildAsHolder</c>'s
    /// guard was there to provide, now decided where the state is current. A builder whose claim was
    /// taken over while it worked must not publish a GO over its successor's build.
    ///
    /// <para>This is the assertion that makes the fix a MOVE rather than a removal. The difference
    /// from the defect is not the verdict but WHO reaches it: refusing here is sound because the
    /// owner's state never regresses, and refusing at the writer was not because its copy can.</para>
    /// </summary>
    [Fact]
    public void ASupersededBuildersReport_LandsOnNothing_AndIsConsumed()
    {
        var takenOver = TheOwnersState with
        {
            ClaimedBy = "the-successor",
            FrameworkVersion = "fp-next",
            ReportedOutcomes = ImmutableDictionary<string, BuildOutcome>.Empty
                .Add(HolderB, BuildOutcome.Completed(T0.AddMinutes(1), GoB)),
        };

        var after = StateOf(BuildNodeType.FoldReportedOutcomes(Node(takenOver), Options));

        after.Ready.Should().NotContainKey(
            "fp-b", "a builder that lost its claim must not certify a build it no longer owns");
        after.ClaimedBy.Should().Be(
            "the-successor", "…and it must not free its successor's claim either");
        after.Status.Should().Be(BuildStatus.Planning, "the successor is still building");
        after.ReportedOutcomes.Should().BeNull(
            "the refused report is CONSUMED, so it is never re-judged against a later claim");
    }

    /// <summary>
    /// A failure reports the same way and fails CLOSED: the fingerprint gets no GO, so readiness for
    /// that image stays refused, which is what the probe contract requires.
    /// </summary>
    [Fact]
    public void AFailureReport_RecordsTheError_PublishesNoGo_AndFreesTheClaim()
    {
        var reported = TheOwnersState with
        {
            ReportedOutcomes = ImmutableDictionary<string, BuildOutcome>.Empty
                .Add(HolderB, BuildOutcome.Failed(T0.AddMinutes(1), "2 regression(s) on this image")),
        };

        var after = StateOf(BuildNodeType.FoldReportedOutcomes(Node(reported), Options));

        after.Status.Should().Be(BuildStatus.Failed);
        after.Error.Should().Be("2 regression(s) on this image");
        after.Ready.Should().NotContainKey("fp-b", "a failed build publishes no GO");
        after.Ready.Should().ContainKey("fp-a", "…and revokes nobody else's");
        after.ClaimedBy.Should().BeNull("a failed build must not stay locked to a holder that stopped");
        after.ReportedOutcomes.Should().BeNull();
    }

    /// <summary>
    /// A CHUNK close-out is the same transition one level down: it publishes no GO — the
    /// per-fingerprint history is root-only — and reports the release paths its compiles minted.
    /// </summary>
    [Fact]
    public void AChunkCloseOut_RecordsItsReleasePaths_AndFreesTheChunk()
    {
        var written = ImmutableList.Create("Space/Type/Release/1", "Space/Other/Release/3");
        var chunk = new BuildState
        {
            Status = BuildStatus.Building,
            ClaimedBy = HolderA,
            ClaimedAt = T0,
            ReportedOutcomes = ImmutableDictionary<string, BuildOutcome>.Empty
                .Add(HolderA, BuildOutcome.Completed(T0.AddMinutes(1), go: null, writtenPaths: written)),
        };

        var after = StateOf(BuildNodeType.FoldReportedOutcomes(
            new MeshNode("chunk-1", BuildNodeType.RootPath)
            {
                NodeType = BuildNodeType.NodeType,
                Content = chunk,
            },
            Options));

        after.Status.Should().Be(BuildStatus.Ready);
        after.WrittenPaths.Should().Equal(written);
        after.Ready.Should().BeNull("a chunk publishes no GO");
        after.ClaimedBy.Should().BeNull();
        after.ReportedOutcomes.Should().BeNull();
    }

    /// <summary>
    /// At most ONE report can apply per pass — applying one clears <c>ClaimedBy</c>, so every other
    /// is refused against the same state. That is what makes the result independent of the order an
    /// <c>ImmutableDictionary</c> happens to enumerate in, which nothing specifies.
    /// </summary>
    [Fact]
    public void TwoReportsInOnePass_ApplyTheHOLDERS_AndRefuseTheOther()
    {
        var both = TheOwnersState with
        {
            ReportedOutcomes = ImmutableDictionary<string, BuildOutcome>.Empty
                .Add(HolderA, BuildOutcome.Completed(T0, new BuildGo("fp-stale", T0)))
                .Add(HolderB, BuildOutcome.Completed(T0.AddMinutes(1), GoB)),
        };

        var after = StateOf(BuildNodeType.FoldReportedOutcomes(Node(both), Options));

        after.Ready.Should().ContainKey("fp-b", "holder-b is the one this node names");
        after.Ready.Should().NotContainKey("fp-stale");
        after.ClaimedBy.Should().BeNull();
        after.ReportedOutcomes.Should().BeNull("both are consumed, applied or not");
    }

    // ── the guard that keeps this fix from becoming a worse bug ─────────────────────────────────

    /// <summary>
    /// 🚨 A grant must NOT be published over a report nobody has folded yet.
    ///
    /// <para>The fold refuses a report from a holder this node does not name — correct, and the
    /// whole superseded-builder property. But a grant published between a holder's report and the
    /// fold would make the node name someone else, and the fold would then refuse a GO that was
    /// genuinely earned: the exact stall this change exists to end, re-entering through the
    /// arbiter's own publication.</para>
    ///
    /// <para>It is a real window, and only on the DURABLE path: <c>ArbitrateDurably</c> spans two
    /// storage round-trips between reading the candidate set and publishing, while
    /// <c>GrantOnMirror</c> composes fold-and-elect into one lambda where nothing can come between
    /// them. <c>ApplyGrant</c> runs on the node's own serialised write path, so it is the last point
    /// that can still see the report — the same place, and the same reasoning, as the refusal
    /// #1193 added for a stand-down.</para>
    /// </summary>
    [Fact]
    public void AGrantIsNotPublishedOverAnUnfoldedReport()
    {
        // 🚨 The winner IS still a registered candidate and has NOT stood down, so the two
        // pre-existing refusals in ApplyGrant cannot fire. Without that this case would pass on a
        // tree with no report guard at all — refused one line earlier, for a different reason, and
        // checking nothing. <see cref="OnceTheReportIsFolded_TheSameGrantPublishes"/> is the other
        // half of the discriminator: the same grant, the same candidate, report consumed, publishes.
        var pendingReport = Node(TheOwnersState with
        {
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("next-image", new BuildClaimRequest("fp-next", T0)),
            ReportedOutcomes = ImmutableDictionary<string, BuildOutcome>.Empty
                .Add(HolderB, BuildOutcome.Completed(T0.AddMinutes(1), GoB)),
        });
        var wouldGrant = new BuildState
        {
            ClaimedBy = "next-image",
            ClaimedAt = T0.AddMinutes(2),
            HeartbeatAt = T0.AddMinutes(2),
            FrameworkVersion = "fp-next",
            Status = BuildStatus.Planning,
        };

        BuildNodeType.ApplyGrant(pendingReport, wouldGrant, Options).Should().BeSameAs(
            pendingReport,
            "publishing here would make the fold refuse holder-b's GO as a superseded builder's");
    }

    /// <summary>
    /// …and the refusal is not a permanent hold: once the fold has consumed the report, the very
    /// same grant publishes. A guard that could not be satisfied would trade one stall for another,
    /// and the fold consumes every report it sees — applied or refused — so this always converges.
    /// </summary>
    [Fact]
    public void OnceTheReportIsFolded_TheSameGrantPublishes()
    {
        var folded = BuildNodeType.FoldReportedOutcomes(
            Node(TheOwnersState with
            {
                ReportedOutcomes = ImmutableDictionary<string, BuildOutcome>.Empty
                    .Add(HolderB, BuildOutcome.Completed(T0.AddMinutes(1), GoB)),
            }),
            Options);
        var withACandidate = StateOf(folded) with
        {
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("next-image", new BuildClaimRequest("fp-next", T0)),
        };
        var wouldGrant = new BuildState
        {
            ClaimedBy = "next-image",
            ClaimedAt = T0.AddMinutes(2),
            HeartbeatAt = T0.AddMinutes(2),
            FrameworkVersion = "fp-next",
            Status = BuildStatus.Planning,
        };

        var after = StateOf(BuildNodeType.ApplyGrant(Node(withACandidate), wouldGrant, Options));

        after.ClaimedBy.Should().Be("next-image");
        after.Ready.Should().ContainKey("fp-b", "the GO folded a moment ago is still on the history");
    }

    // ── the trigger: a decision the arbiter is never woken to take is not a fix ──────────────────

    /// <summary>
    /// 🚨 A report lands on a node with <c>RequestedClaims</c> EMPTY — the holder's own grant
    /// consumed its registration on the way in — so a trigger that asked only "is anyone queued?"
    /// would filter the emission away and the GO would wait for the two-minute stale tick. The
    /// same hole #1193 had to close for the stand-down mark.
    /// </summary>
    [Fact]
    public void AReportedOutcome_WakesTheArbiter_WithNobodyQueued()
    {
        var reported = TheOwnersState with
        {
            ReportedOutcomes = ImmutableDictionary<string, BuildOutcome>.Empty
                .Add(HolderB, BuildOutcome.Completed(T0.AddMinutes(1), GoB)),
        };

        BuildNodeType.ArbitrationTrigger(reported).Should().NotBeNull(
            "nothing else is going to wake a pass — the registration is long consumed");
    }

    /// <summary>
    /// …and the report belongs to the KEY, not merely to the filter. It arrives on a node whose
    /// other trigger fields did not move, so a key that omitted it would be swallowed by
    /// <c>DistinctUntilChanged</c>: the filter would pass and still nothing would fire.
    /// </summary>
    [Fact]
    public void AReportedOutcome_ChangesTheTriggerKey()
    {
        var queuedBehindTheHolder = TheOwnersState with
        {
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("next-image", new BuildClaimRequest("fp-next", T0)),
        };
        var reported = queuedBehindTheHolder with
        {
            ReportedOutcomes = ImmutableDictionary<string, BuildOutcome>.Empty
                .Add(HolderB, BuildOutcome.Completed(T0.AddMinutes(1), GoB)),
        };

        BuildNodeType.ArbitrationTrigger(reported).Should().NotBe(
            BuildNodeType.ArbitrationTrigger(queuedBehindTheHolder),
            "the two differ only in the report, and they need different arbitration");
    }

    /// <summary>
    /// The negative control: a node with nothing reported, nobody queued and nothing to release
    /// still wakes NOTHING. A key that fired on every emission would be a poll wearing a filter's
    /// clothes.
    /// </summary>
    [Fact]
    public void ABuildNodeWithNothingReported_WakesNoArbitrationPass()
    {
        BuildNodeType.ArbitrationTrigger(TheOwnersState).Should().BeNull();
        BuildNodeType.ArbitrationTrigger(TheWritersStaleCopy).Should().BeNull();
    }

    // ── nothing to do, and content this build cannot read ───────────────────────────────────────

    /// <summary>
    /// Pure and free: with no report the fold returns the SAME node, so the arbiter may run it on
    /// every pass and a quiet build node still writes nothing.
    /// </summary>
    [Fact]
    public void WithNothingReported_TheFoldReturnsTheNodeUnchanged()
    {
        var quiet = Node(TheOwnersState);

        BuildNodeType.FoldReportedOutcomes(quiet, Options).Should().BeSameAs(quiet);
    }

    /// <summary>
    /// 🚨 A node whose content is PRESENT and cannot be materialised as <c>BuildState</c> is left
    /// exactly as it is — the same refusal the three sibling deciders on this node make (#3623).
    /// Folding over a default-valued state would drop the live claim, every registration and every
    /// stand-down mark, and this pass would COMMIT that.
    /// </summary>
    [Fact]
    public void AnUnreadableMirror_IsLeftUntouched()
    {
        var unreadable = new MeshNode("Build", "Admin")
        {
            NodeType = BuildNodeType.NodeType,
            Content = JsonSerializer.Deserialize<JsonElement>("""{"status":"not-a-build-state"}"""),
        };

        BuildNodeType.FoldReportedOutcomes(unreadable, Options).Should().BeSameAs(unreadable);
    }
}
