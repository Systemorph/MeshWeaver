using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the ONE notion of "which platform build is newer" (#3542) — the sealed-publication lineage,
/// not the version string.
///
/// <para>🚨 <b>The failure this prevents rolled production backwards.</b> A version LINE is a label a
/// human maintains in <c>Directory.Build.props</c>; the <c>ci.&lt;run&gt;</c> suffix is the GitHub
/// Actions run number of the workflow that published the image. On 2026-09-05 the label was briefly
/// wrong — ten sets published as <c>3.1.0-ci.7832…7841</c> and were withdrawn — and by SemVer those
/// outrank every later, sealed <c>3.0.0-ci.79xx</c> set for ever. On 2026-09-07 both AKS portals
/// rolled themselves onto <c>3.1.0-ci.7841</c>, three days behind, and could not leave: nothing is
/// ever newer than the highest-sorting tag.</para>
///
/// <para>It lives in <c>MeshWeaver.Plugin.Packaging</c>, beside <see cref="NuGetVersionComparer"/>,
/// because the same wrong assumption has a SECOND call site — see
/// <see cref="ThePreReleaseLabelIsTextToSemVer_WhichIsWhyTheFloorNeedsItsOwnPredicate"/>.</para>
/// </summary>
public class PlatformReleaseOrderTest
{
    // ───────── the lineage key ─────────

    /// <summary>Every shape the pipeline publishes, and the one shape that carries no lineage at
    /// all. Both separators are required by <c>Directory.Build.props</c> in as many words: a clean
    /// line starts the pre-release with <c>-ci.N</c>, a labelled one appends <c>.ci.N</c>.</summary>
    [Theory]
    [InlineData("3.0.0-ci.7977", 7977L)]
    [InlineData("3.0.0-rc9.ci.7824", 7824L)]
    [InlineData("3.0.0-edge.7977", 7977L)]
    [InlineData("3.0.0-ci.7977+build.638", 7977L)]
    [InlineData("3.0.0-ci.0", 0L)]
    [InlineData("3.1.0-dev", 0L)]       // the SemVer minter's source-build stamp
    [InlineData("3.1.0-DEV", 0L)]
    [InlineData("3.1.0-dev.1", null)]   // only the bare label is a source build
    // A PROMOTION — a clean release is a retag of one sealed continuous set, so its lineage lives on
    // the sibling tag it was cut from and is not readable from the tag.
    [InlineData("3.0.0", null)]
    [InlineData("3.0.0-rc8", null)]
    [InlineData("unknown", null)]
    [InlineData("main", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void BuildOrdinal_IsTheRunNumberThatPublishedIt(string? version, long? expected)
        => Assert.Equal(expected, PlatformReleaseOrder.BuildOrdinal(version));

    // ───────── the incident ─────────

    /// <summary>
    /// 🚨 The whole of #3542, in one assertion pair: a build published LATER wins, whatever line it
    /// is labelled with, and the reverse must stay false or the two trade places on every check.
    /// </summary>
    [Fact]
    public void ALaterRunNumberWins_WhateverLineItIsLabelledWith()
    {
        Assert.True(PlatformReleaseOrder.IsNewer("3.0.0-ci.7977", "3.1.0-ci.7841"));
        Assert.False(PlatformReleaseOrder.IsNewer("3.1.0-ci.7841", "3.0.0-ci.7977"));
    }

    /// <summary>
    /// 🚨 The trap one layer down, and the reason "just stop bumping the line" is not the fix: SemVer
    /// §11.4 compares pre-release identifiers as TEXT, so <c>ci</c> &lt; <c>rc9</c> and the retired rc
    /// images outranked every clean-line build. With the 3.1.0 tags removed the selector simply moved
    /// to the next mislabelled tag — a 2026-09-04 build.
    /// </summary>
    [Fact]
    public void ARetiredChannelLabel_DoesNotOutrankALaterRun()
    {
        Assert.False(PlatformReleaseOrder.IsNewer("3.0.0-rc9.ci.7824", "3.0.0-ci.7977"));
        Assert.True(PlatformReleaseOrder.IsNewer("3.0.0-ci.7977", "3.0.0-rc9.ci.7824"));
    }

    // ───────── the promotion, which has no lineage of its own ─────────

    /// <summary>
    /// An official release carries no run number, so the version string is the only key it shares
    /// with anything — and for a deliberately cut release it is a trustworthy one. This is what keeps
    /// a Stable install running a continuous build able to reach the clean release it waits for.
    /// </summary>
    [Fact]
    public void APromotionIsComparedByVersion_BecauseItHasNoRunNumber()
    {
        Assert.True(PlatformReleaseOrder.IsNewer("3.1.0", "3.0.0-ci.7977"));
        Assert.True(PlatformReleaseOrder.IsNewer("3.0.0", "3.0.0-ci.7977"));
        Assert.False(PlatformReleaseOrder.IsNewer("3.0.0-ci.7977", "3.0.0"));
    }

    /// <summary>
    /// 🚨 <b>The boundary, pinned deliberately — and the reason the module platform floor cannot
    /// simply call <see cref="PlatformReleaseOrder.IsNewer"/>.</b>
    ///
    /// <para>The line above is the RIGHT answer for an updater: a Stable install must be able to take
    /// the clean <c>3.0.0</c>, so <c>3.0.0</c> outranks <c>3.0.0-ci.N</c>. Read as a FLOOR it is the
    /// wrong answer: measured against the real comparer, every <c>3.0.0-ci.N</c> — N = 1, 7981, 7989,
    /// 999999999 — is refused by a floor of <c>3.0.0</c> AND by every <c>3.0.0-rc*</c> floor, so a
    /// module declaring one is permanently un-landable on the clean continuous line
    /// (<c>ModulePlatformFloor.DeclineReason</c>, 2026-09-07: <i>"the module requires platform
    /// 3.0.0-rc8 or newer but this deployment runs 3.0.0-ci.7989"</i>).</para>
    ///
    /// <para>Same two strings, opposite required answers — so "newer than" and "satisfies the floor
    /// of" are DIFFERENT predicates over the same key, and forcing one to serve both would break the
    /// update path to fix the floor. The shareable part is
    /// <see cref="PlatformReleaseOrder.BuildOrdinal"/>; the floor's own predicate is a separate
    /// decision about what <c>minMeshVersion</c> means, which belongs with whoever owns the floors.
    /// This test exists so that decision is taken knowingly rather than by reusing this one.</para>
    /// </summary>
    [Fact]
    public void ThePreReleaseLabelIsTextToSemVer_WhichIsWhyTheFloorNeedsItsOwnPredicate()
    {
        foreach (var floor in new[] { "3.0.0-rc4", "3.0.0-rc8", "3.0.0-rc9", "3.0.0" })
            foreach (var running in new[] { "3.0.0-ci.1", "3.0.0-ci.7989", "3.0.0-ci.999999999" })
                Assert.True(
                    NuGetVersionComparer.Instance.Compare(running, floor) < 0,
                    $"measured 2026-09-07: SemVer puts {running} below {floor}, so a module declaring "
                    + $"that floor can never land on the clean continuous line. If this assertion "
                    + "ever fails, the floor predicate has changed and ModulePlatformFloor's "
                    + "behaviour changed with it — read that change, do not delete this test.");
    }

    // ───────── the third state ─────────

    /// <summary>
    /// 🚨 A comparison that had to READ something must not report a failed read as a real answer.
    /// <c>"unknown"</c> is what an unstamped build reports, and <see cref="NuGetVersionComparer"/>
    /// alone answers <c>1</c> for <c>Compare("3.0.0", "unknown")</c> — an unparseable core reads as
    /// zero — which would make every registry tag look newer for ever. The nullable return is what
    /// stops a caller receiving "older" or "newer" for a question nobody could answer.
    /// </summary>
    [Fact]
    public void AnUncomparableVersion_AnswersNull_NeverAnOrder()
    {
        Assert.Null(PlatformReleaseOrder.Compare("3.0.0", "unknown"));
        Assert.Null(PlatformReleaseOrder.Compare("main", "3.0.0"));
        Assert.Null(PlatformReleaseOrder.Compare("3.0.0", null));

        // …and the update decision reads that as "do not roll", never as "roll".
        Assert.False(PlatformReleaseOrder.IsNewer("3.0.0", "unknown"));
        Assert.False(PlatformReleaseOrder.IsNewer("unknown", "3.0.0"));

        // The four-part assembly-version fallback IS a version and stays comparable — an unstamped
        // build falls back to it, and refusing it would stop that install updating at all.
        Assert.Equal(1, PlatformReleaseOrder.Compare("3.1.0.0", "3.0.0.0"));
    }

    /// <summary>Build metadata is not part of ordering, per SemVer — and the running
    /// <c>InformationalVersion</c> carries it while a registry tag never does.</summary>
    [Fact]
    public void BuildMetadataIsIgnored()
    {
        Assert.Equal(0, PlatformReleaseOrder.Compare("3.0.0-ci.51", "3.0.0-ci.51+build.638123456789"));
        Assert.True(PlatformReleaseOrder.IsNewer("3.0.0-ci.52", "3.0.0-ci.51+build.638123456789"));
    }

    // ───────── the total order every RANKING caller shares ─────────

    /// <summary>
    /// 🚨 <b><see cref="PlatformReleaseOrder.Newest"/> over the incident's own set.</b> Four
    /// callers rank platform builds — the self-updater's tag listing, the release-marker index, the
    /// bundle-adoption sweep and the retention plan — and every one of them reads a version string
    /// a publication was LABELLED with. Ordered descending, the two mislabelled members must both
    /// sink below the later sealed runs, and the promotion (no run number of its own) sits in the
    /// band behind them.
    /// </summary>
    [Fact]
    public void TheTotalOrder_RanksTheRunNumberFirst_AndTheLabelNever()
    {
        string[] published =
        [
            "3.0.0",              // a promotion — no run number of its own
            "3.1.0-ci.7841",      // the withdrawn 2026-09-05 slip line
            "3.0.0-ci.8130",      // the newest sealed set
            "3.0.0-rc9.ci.7824",  // the retired rc line, 2026-09-04
            "3.0.0-ci.8059",
        ];

        Assert.Equal(
            ["3.0.0-ci.8130", "3.0.0-ci.8059", "3.1.0-ci.7841", "3.0.0-rc9.ci.7824", "3.0.0"],
            published.OrderByDescending(v => v, PlatformReleaseOrder.Newest));
    }

    /// <summary>
    /// 🚨 <b>Why the ordering is not <see cref="PlatformReleaseOrder.Compare"/>.</b> Pairwise, the
    /// three members form a CYCLE — slip beats promotion beats sealed beats slip — and a sort handed
    /// a cycle answers arbitrarily, which is how a "fixed" ordering keeps picking the wrong build.
    /// Banding by <see cref="PlatformReleaseOrder.BuildOrdinal"/> is what removes it, and this test
    /// exercises the cycle in every input order so a stable-sort accident cannot hide a regression.
    /// </summary>
    [Fact]
    public void TheTotalOrder_IsTransitive_WhereThePairwiseComparisonIsNot()
    {
        // The cycle, measured on the pairwise predicate.
        Assert.True(PlatformReleaseOrder.IsNewer("3.1.0-ci.7841", "3.0.0"));
        Assert.True(PlatformReleaseOrder.IsNewer("3.0.0", "3.0.0-ci.8130"));
        Assert.True(PlatformReleaseOrder.IsNewer("3.0.0-ci.8130", "3.1.0-ci.7841"));

        // The order over the same three is one answer, whatever order they arrive in.
        string[] cycle = ["3.1.0-ci.7841", "3.0.0", "3.0.0-ci.8130"];
        foreach (var permutation in Permutations(cycle))
            Assert.Equal(
                ["3.0.0-ci.8130", "3.1.0-ci.7841", "3.0.0"],
                permutation.OrderByDescending(v => v, PlatformReleaseOrder.Newest));
    }

    /// <summary>
    /// The one key a promotion band shares is the version string, and a deliberately cut release
    /// makes it a trustworthy one — so promotions order among themselves by SemVer, and an
    /// uncomparable string (an unstamped build's <c>unknown</c>) sinks to the bottom rather than
    /// throwing or being read as newest.
    /// </summary>
    [Fact]
    public void TheTotalOrder_OrdersPromotionsBySemVer_AndSinksWhatItCannotRead()
    {
        Assert.Equal(
            ["3.0.1", "3.0.0", "3.0.0-rc8", "unknown"],
            new[] { "3.0.0-rc8", "unknown", "3.0.1", "3.0.0" }
                .OrderByDescending(v => v, PlatformReleaseOrder.Newest));
    }

    // ───────── the SemVer notation (policy `platform-semver-versioning`) ─────────

    /// <summary>
    /// From <see cref="PlatformReleaseOrder.SemVerEraStart"/> on, a continuous build is the plain
    /// SemVer <c>&lt;major&gt;.&lt;minor&gt;.&lt;run&gt;</c> and its run number is the PATCH. Below
    /// the boundary nothing changes meaning: a clean <c>3.0.x</c> is still a promotion, and the
    /// withdrawn slip <c>3.1.0-ci.7841</c> is still read by its <c>ci</c> label.
    /// </summary>
    [Theory]
    [InlineData("3.1.10050", 10050L, true)]
    [InlineData("3.1.10050+sha.abc1234", 10050L, true)]
    [InlineData("3.1.10050-edge", 10050L, true)]
    [InlineData("3.1.10050-edge.311", 311L, false)]    // edge-images' fallback: its OWN run number decides, as before
    [InlineData("3.2.10400", 10400L, true)]
    [InlineData("4.0.20000", 20000L, true)]
    [InlineData("3.1.0", null, false)]                  // a zero patch is a floor or a release, never a run
    [InlineData("4.0.0", null, false)]
    [InlineData("3.1.0-ci.0", 0L, false)]               // the retired local source-build stamp
    [InlineData("3.1.0-dev", 0L, false)]                // the source-build stamp the minter writes
    [InlineData("3.1.0-ci.7841", 7841L, false)]         // the slip keeps its ci number
    [InlineData("3.0.0", null, false)]                  // a promotion below the boundary
    [InlineData("3.0.5", null, false)]
    [InlineData("2.9.12345", null, false)]
    [InlineData("3.1.10050-rc1", null, false)]          // an unknown label is not a build of the notation
    [InlineData("3.1.10050.1", null, false)]            // four parts is not the notation
    public void BuildOrdinal_ReadsTheSemVerNotationsPatch_AboveTheBoundaryOnly(
        string version, long? expected, bool isSemVerBuild)
    {
        Assert.Equal(expected, PlatformReleaseOrder.BuildOrdinal(version));
        Assert.Equal(isSemVerBuild, PlatformReleaseOrder.IsSemVerBuild(version));
    }

    /// <summary>
    /// 🚨 <b>The cut-over is monotonic — the claim the whole migration rests on.</b> The old and the
    /// new notation share ONE lineage (the CD run number), so the last <c>3.0.0-ci.&lt;n&gt;</c> and
    /// the first <c>3.1.&lt;n+1&gt;</c> are ordered by publication, whichever notation each wears.
    ///
    /// <para>Negative control, built into the data: before the SemVer reading existed,
    /// <c>3.1.10000</c> had NO run number, so the band rule ranked EVERY <c>3.0.0-ci.*</c> above it
    /// and a Continuous install would never have left the old notation. Reverting
    /// <c>SemVerBuildPatch</c> to <c>return null</c> fails this test.</para>
    /// </summary>
    [Fact]
    public void TheOldAndTheNewNotation_InterleaveByPublicationOrder()
    {
        Assert.True(PlatformReleaseOrder.IsNewer("3.1.10000", "3.0.0-ci.9999"));
        Assert.False(PlatformReleaseOrder.IsNewer("3.0.0-ci.9999", "3.1.10000"));
        // A LATER old-notation set (a reconcile run that minted under 3.0.0 after the flip) is still
        // newer than an earlier new-notation one: the run number decides, never the notation.
        Assert.True(PlatformReleaseOrder.IsNewer("3.0.0-ci.10002", "3.1.10001"));
        // The withdrawn slip never outranks the new notation.
        Assert.True(PlatformReleaseOrder.IsNewer("3.1.10000", "3.1.0-ci.7841"));
        // A minor bump is deliberate and later, so SemVer and the lineage agree across it.
        Assert.True(PlatformReleaseOrder.IsNewer("3.2.10400", "3.1.10399"));

        string[] mixed =
        [
            "3.0.0-ci.9998", "3.1.10001", "3.0.0", "3.1.0-ci.7841", "3.0.0-ci.9999",
            "3.2.10400", "3.1.10000", "3.0.0-rc9.ci.7824",
        ];
        Assert.Equal(
            ["3.2.10400", "3.1.10001", "3.1.10000", "3.0.0-ci.9999", "3.0.0-ci.9998",
             "3.1.0-ci.7841", "3.0.0-rc9.ci.7824", "3.0.0"],
            mixed.OrderByDescending(v => v, PlatformReleaseOrder.Newest));
    }

    /// <summary>The total order stays total across the two notations: every permutation of a mixed
    /// set sorts to the same answer.</summary>
    [Fact]
    public void TheTotalOrder_IsTransitive_AcrossBothNotations()
    {
        string[] expected = ["3.1.10001", "3.0.0-ci.10000", "3.1.0-ci.7841", "3.0.0"];
        foreach (var permutation in Permutations(["3.0.0", "3.1.10001", "3.1.0-ci.7841", "3.0.0-ci.10000"]))
            Assert.Equal(expected, permutation.OrderByDescending(v => v, PlatformReleaseOrder.Newest));
    }

    /// <summary>
    /// A declared floor keeps meaning what it says across the cut-over: a <c>3.0.0</c> floor is met
    /// by every new-notation build, an old-notation set floor is ordered by run number against a
    /// new-notation build, a minor floor (<c>3.2.0</c>, zero patch) holds until that minor ships, and
    /// the local stamp <c>3.1.0-ci.0</c> is never ordered against a publication.
    /// </summary>
    [Fact]
    public void AFloor_IsOrderedAcrossTheCutOver()
    {
        Assert.Equal(PlatformFloorKind.Satisfied, PlatformFloor.Evaluate("3.0.0", "3.1.10050").Kind);
        Assert.Equal(PlatformFloorKind.Satisfied, PlatformFloor.Evaluate("3.0.0-ci.9000", "3.1.10050").Kind);
        Assert.Equal(PlatformFloorKind.Held, PlatformFloor.Evaluate("3.1.10060", "3.0.0-ci.9999").Kind);
        Assert.Equal(PlatformFloorKind.Held, PlatformFloor.Evaluate("3.2.10400", "3.1.10050").Kind);
        Assert.Equal(PlatformFloorKind.Held, PlatformFloor.Evaluate("3.2.0", "3.1.10050").Kind);
        Assert.Equal(PlatformFloorKind.Satisfied, PlatformFloor.Evaluate("3.1.0", "3.1.10050").Kind);
        Assert.Equal(PlatformFloorKind.Held, PlatformFloor.Evaluate("3.1.0", "3.0.0-ci.9999").Kind);
        Assert.Equal(PlatformFloorKind.Advisory, PlatformFloor.Evaluate("3.1.10050", "3.1.0-ci.0").Kind);
        Assert.Equal(PlatformFloorKind.Advisory, PlatformFloor.Evaluate("3.1.10050", "3.1.0-dev").Kind);
        Assert.Equal(PlatformFloorKind.Advisory, PlatformFloor.Evaluate("3.1.0-dev", "3.1.10050").Kind);
    }

    /// <summary>
    /// 🚨 <b>The actual boundary the minter flip creates</b> (policy <c>platform-semver-versioning</c>):
    /// the newest <c>3.0.0-ci.&lt;n&gt;</c> in the registries when the minter changed, and the first
    /// build after it. Every question the fleet asks of the pair — newer, ordered, floor, source build —
    /// answers the same way SemVer does, so readers that compare either way agree.
    /// </summary>
    [Fact]
    public void TheFirstSemVerBuild_OutranksEveryOldNotationSet()
    {
        const string LastOld = "3.0.0-ci.10330";
        const string FirstNew = "3.1.10331";

        Assert.True(PlatformReleaseOrder.IsNewer(FirstNew, LastOld));
        Assert.False(PlatformReleaseOrder.IsNewer(LastOld, FirstNew));
        Assert.True(NuGetVersionComparer.Instance.Compare(FirstNew, LastOld) > 0, "SemVer agrees with the lineage");
        Assert.Equal(FirstNew, new[] { LastOld, "3.0.0-ci.10317", FirstNew, "3.0.0" }
            .OrderByDescending(v => v, PlatformReleaseOrder.Newest).First());

        // Floors stamped before the flip are met by the new builds; a new-notation floor holds a
        // portal still on the old notation.
        Assert.Equal(PlatformFloorKind.Satisfied, PlatformFloor.Evaluate("3.0.0-ci.10317", FirstNew).Kind);
        Assert.Equal(PlatformFloorKind.Satisfied, PlatformFloor.Evaluate(FirstNew, FirstNew).Kind);
        Assert.Equal(PlatformFloorKind.Held, PlatformFloor.Evaluate(FirstNew, LastOld).Kind);

        Assert.False(PlatformReleaseOrder.IsSourceBuild(FirstNew));
        Assert.True(PlatformReleaseOrder.IsSourceBuild("3.1.0-dev"));
        Assert.True(PlatformReleaseOrder.IsSourceBuild("3.0.0-ci.0"));
    }

    private static IEnumerable<string[]> Permutations(string[] items) =>
        items.Length <= 1
            ? [items]
            : items.SelectMany(
                (head, i) => Permutations([.. items.Take(i), .. items.Skip(i + 1)])
                    .Select(rest => (string[])[head, .. rest]));
}
