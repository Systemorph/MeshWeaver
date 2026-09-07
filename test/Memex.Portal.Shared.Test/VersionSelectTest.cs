using System.Text.Json;
using System.Text.Json.Serialization;
using Memex.Portal.Shared.SelfUpdate;
using Xunit;
using MeshWeaver.Hosting.SelfUpdate;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the self-update DECISION logic: which registry tag each policy rolls to, and when a target is
/// "newer" than the running version. This is the safety core of the feature (a wrong pick auto-rolls
/// the whole platform), so the truth table is fixed here. Also covers the policy-node content parse.
/// </summary>
public class VersionSelectTest
{
    // 3.1.0-ci.55 is the newest overall (run 55 is the latest publication); the only CLEAN release
    // is 3.0.0. 🚨 The build number was 5 here until #3542: a 3.1.0 LABEL over an EARLIER run than
    // 3.0.0-ci.51, which is the slip shape itself, and the assertion below then pinned the defect as
    // the contract. Real tags cannot look like that — the number is the CI run number and only ever
    // increases — so the data now says what the pipeline actually produces.
    private static readonly string[] Tags =
        ["3.0.0", "3.0.0-ci.40", "3.0.0-ci.51", "3.1.0-ci.55", "garbage", "latest", "main"];

    [Fact]
    public void Continuous_PicksNewestTag_IncludingBuildNumbered()
        => Assert.Equal("3.1.0-ci.55", VersionSelect.PickTarget(Tags, UpdatePolicyKind.Continuous));

    [Fact]
    public void Stable_PicksNewestCleanRelease_IgnoringBuildNumbered()
        => Assert.Equal("3.0.0", VersionSelect.PickTarget(Tags, UpdatePolicyKind.Stable));

    [Fact]
    public void None_PicksNothing()
        => Assert.Null(VersionSelect.PickTarget(Tags, UpdatePolicyKind.None));

    [Fact]
    public void PickTarget_NoParseableTags_ReturnsNull()
        => Assert.Null(VersionSelect.PickTarget(["latest", "main", "garbage"], UpdatePolicyKind.Continuous));

    [Fact]
    public void Continuous_IgnoresPerArchRidTags_PrefersManifestList()
        // The multi-arch CD pushes the manifest list (3.0.0-ci.43) alongside per-RID image tags whose
        // suffix parses as an extra pre-release identifier that sorts ABOVE it. The poller must roll to the
        // manifest list, never the x64-only image (which would be wrong-arch on an arm64 node).
        => Assert.Equal("3.0.0-ci.43", VersionSelect.PickTarget(
            ["3.0.0-ci.43", "3.0.0-ci.43-linux-x64", "3.0.0-ci.43-linux-arm64", "main", "main-linux-x64"],
            UpdatePolicyKind.Continuous));

    [Fact]
    public void Continuous_IgnoresBareGitShaTags_EvenAllDigitOnes()
        // The CD pushes a 7-char git-sha tag per version alongside the ci.N tag. An ALL-DIGIT sha like
        // "6943991" NuGet-parses as version 6943991.0.0 and outranks every real 3.x release — the prod bug
        // that froze every portal on ci.122 (whose sha, 6943991, is all digits) and reverted rolls to a
        // newer ci.N. Only a real MAJOR.MINOR.PATCH tag may be picked; git-shas (digit- or letter-) are out.
        => Assert.Equal("3.0.0-ci.133", VersionSelect.PickTarget(
            ["3.0.0-ci.122", "6943991", "3.0.0-ci.133", "4779b4e", "main"],
            UpdatePolicyKind.Continuous));

    // CI-green gate: a `-edge.N` build is UNVERIFIED. Default (RequireCiGreen=true) must skip it even
    // though it is the newest; opting into "any" (RequireCiGreen=false) rolls to it.
    [Fact]
    public void GreenOnly_Default_ExcludesEdgeBuilds_EvenWhenNewest()
        => Assert.Equal("3.0.0-ci.51",
            VersionSelect.PickTarget(["3.0.0-ci.51", "3.1.0-edge.7"], UpdatePolicyKind.Continuous));

    [Fact]
    public void Any_RequireCiGreenFalse_IncludesEdgeBuilds()
        // 🚨 edge.70, not edge.7: `edge-images.yml` rewrites the `ci` label to `edge` and keeps the same
        // run number, so an edge build that is genuinely newer carries a HIGHER one. Pinning
        // "3.1.0-edge.7 beats 3.0.0-ci.51" would be pinning the #3542 defect under another label.
        => Assert.Equal("3.1.0-edge.70",
            VersionSelect.PickTarget(["3.0.0-ci.51", "3.1.0-edge.70"], UpdatePolicyKind.Continuous, requireCiGreen: false));

    [Fact]
    public void UpdatePolicyContent_DefaultsToRequireCiGreen()
        => Assert.True(new UpdatePolicyContent().RequireCiGreen, "green-only must be the safe default");

    [Theory]
    [InlineData("3.1.0-ci.55", "3.0.0-ci.51", true)]  // later run on a higher base wins
    [InlineData("3.0.0-ci.51", "3.0.0-ci.40", true)]  // monotonic ci number
    [InlineData("3.0.0", "3.0.0-ci.51", true)]        // release beats its prerelease
    [InlineData("3.0.0-ci.40", "3.0.0-ci.51", false)] // older ci number
    [InlineData("3.0.0", "3.0.0", false)]             // equal
    [InlineData("3.0.0", "unknown", false)]           // unparseable current → never update
    public void IsNewer_TruthTable(string target, string current, bool expected)
        => Assert.Equal(expected, VersionSelect.IsNewer(target, current));

    [Theory]
    // The running InformationalVersion carries +build.<ticks> metadata; SemVer ignores it in comparison.
    [InlineData("3.0.0-ci.52", "3.0.0-ci.51+build.638123456789", true)]
    [InlineData("3.0.0-ci.51", "3.0.0-ci.51+build.638123456789", false)]
    public void IsNewer_IgnoresBuildMetadataOnCurrent(string target, string current, bool expected)
        => Assert.Equal(expected, VersionSelect.IsNewer(target, current));

    [Fact]
    public void ParseContent_TypedContent_RoundTrips()
        => Assert.Equal(UpdatePolicyKind.Stable,
            UpdatePolicyNodeType.ParseContent(
                new UpdatePolicyContent { Policy = UpdatePolicyKind.Stable }, Web).Policy);

    [Fact]
    public void ParseContent_Null_FailsClosedToNone()
        // 🚨 REVERSED by #3542, deliberately. This used to assert Continuous — i.e. content that is
        // absent or unreadable enabled unattended rolls. That is how memex-cloud rolled onto a
        // withdrawn 3.1.0-ci line "on a policy record that lost its own policy". A read that
        // produced nothing must not be the most permissive answer.
        => Assert.Equal(UpdatePolicyKind.None, UpdatePolicyNodeType.ParseContent(null, Web).Policy);

    [Fact]
    public void ParseContent_JsonElement_DeserializesEnumByName()
    {
        var element = JsonSerializer.SerializeToElement(
            new UpdatePolicyContent { Policy = UpdatePolicyKind.None }, Web);
        Assert.Equal(UpdatePolicyKind.None, UpdatePolicyNodeType.ParseContent(element, Web).Policy);
    }

    private static readonly JsonSerializerOptions Web =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // ───────── PickTargets: the ordered candidate list (the deadlock fix) ─────────

    /// <summary>
    /// The newest eligible tag is simply the first candidate — so the two selectors can never
    /// disagree about what "best" means.
    /// </summary>
    [Fact]
    public void PickTargets_FirstCandidate_IsExactlyPickTarget()
    {
        string[] tags = ["3.0.0-rc7.ci.4928", "3.0.0-rc7.ci.4900", "3.0.0-rc6", "main", "6943991"];

        var best = VersionSelect.PickTarget(tags, UpdatePolicyKind.Continuous);
        var all = VersionSelect.PickTargets(tags, UpdatePolicyKind.Continuous);

        Assert.Equal(best, all.First());
    }

    /// <summary>
    /// 🚨 The deadlock this exists to break. memex sat on 3.0.0-rc6 held against
    /// 3.0.0-rc7.ci.4928 because no sealed bake existed for that image — while three separate
    /// bakes published three OTHER identities. Newest-only selection can never escape that: each
    /// new build produces another unbaked tag. Walking the ordered list lets the caller take the
    /// newest release that is actually sealed.
    /// </summary>
    [Fact]
    public void PickTargets_IsOrderedNewestFirst_SoTheCallerCanSkipAnUnbakedHead()
    {
        string[] tags = ["3.0.0-rc7.ci.4900", "3.0.0-rc7.ci.4928", "3.0.0-rc6", "3.0.0-rc7.ci.4910"];

        var candidates = VersionSelect.PickTargets(tags, UpdatePolicyKind.Continuous);

        Assert.Equal(["3.0.0-rc7.ci.4928", "3.0.0-rc7.ci.4910", "3.0.0-rc7.ci.4900", "3.0.0-rc6"], candidates);

        // The caller's move: head is unbaked, so it rolls to the next one down rather than freezing.
        var sealedTags = new HashSet<string> { "3.0.0-rc7.ci.4910", "3.0.0-rc6" };
        Assert.Equal("3.0.0-rc7.ci.4910", candidates.First(sealedTags.Contains));
    }

    /// <summary>Every exclusion PickTarget applies applies here too — same filter, one place.</summary>
    [Fact]
    public void PickTargets_ExcludesRidTags_ShaTags_And_Edge()
    {
        string[] tags =
        [
            "3.0.0-rc7.ci.4928", "3.0.0-rc7.ci.4928-linux-x64", "3.0.0-rc7.ci.4928-linux-arm64",
            "main", "6943991", "3.0.0-edge.51",
        ];

        var candidates = VersionSelect.PickTargets(tags, UpdatePolicyKind.Continuous);

        Assert.Equal(["3.0.0-rc7.ci.4928"], candidates);
    }

    [Fact]
    public void PickTargets_Stable_TakesOnlyCleanReleases_And_None_TakesNothing()
    {
        string[] tags = ["3.1.0", "3.0.0-rc7.ci.4928", "3.0.0"];

        Assert.Equal(["3.1.0", "3.0.0"], VersionSelect.PickTargets(tags, UpdatePolicyKind.Stable));
        Assert.Empty(VersionSelect.PickTargets(tags, UpdatePolicyKind.None));
    }

    // ───────── #3542: the key is the sealed-publication LINEAGE, not the version string ─────────

    /// <summary>
    /// 🚨 <b>The incident, in one assertion.</b> On 2026-09-05 <c>Directory.Build.props</c> briefly
    /// read <c>3.1.0</c>, so runs 7832–7841 published as <c>3.1.0-ci.*</c> and were withdrawn. Ordered
    /// by SemVer those outrank every later, SEALED <c>3.0.0-ci.79xx</c> set forever — and on
    /// 2026-09-07 memex-cloud rolled itself onto <c>3.1.0-ci.7841</c>, three days behind, onto a line
    /// nobody had published for two days.
    ///
    /// <para>Nothing in the release process can prevent that: 7841 passed promote, bake, seal and
    /// register. Every gate it has says yes, and none of them asks whether this core is NEWER than
    /// what the fleet already runs. Only the ordering can, and only once it stops reading a
    /// hand-maintained label as if it were the publication order.</para>
    /// </summary>
    [Fact]
    public void AMislabelledLine_NeverOutranksALaterSealedSet()
    {
        // The registry as it stood on 2026-09-07, minus the noise.
        string[] tags =
        [
            "3.0.0-rc9.ci.7693", "3.1.0-ci.7832", "3.1.0-ci.7841",
            "3.0.0-ci.7955", "3.0.0-ci.7962", "3.0.0-ci.7977",
        ];

        Assert.Equal("3.0.0-ci.7977", VersionSelect.PickTarget(tags, UpdatePolicyKind.Continuous));
        Assert.Equal(
            [
                "3.0.0-ci.7977", "3.0.0-ci.7962", "3.0.0-ci.7955",
                "3.1.0-ci.7841", "3.1.0-ci.7832", "3.0.0-rc9.ci.7693",
            ],
            VersionSelect.PickTargets(tags, UpdatePolicyKind.Continuous));
    }

    /// <summary>
    /// 🚨 The other half of the same defect: once ON the mislabelled tag, nothing is ever newer, so
    /// the install can never leave under its own power — an operator had to move both AKS portals by
    /// hand. Lineage answers it (7977 was published after 7841, whatever the labels say), and the
    /// reverse must stay false or the two would trade places on every check.
    /// </summary>
    [Fact]
    public void AnInstallOnTheMislabelledTag_SeesTheLaterSealedSetAsNewer()
    {
        Assert.True(VersionSelect.IsNewer("3.0.0-ci.7977", "3.1.0-ci.7841"));
        Assert.False(VersionSelect.IsNewer("3.1.0-ci.7841", "3.0.0-ci.7977"));
    }

    /// <summary>
    /// 🚨 The trap one layer down, and the reason "just stop bumping the line" is not the fix: SemVer
    /// §11.4 compares pre-release identifiers as TEXT, so <c>rc9</c> sorts above <c>ci</c> and the
    /// retired rc images still in ACR outrank every clean-line build. With the 3.1.0 tags removed the
    /// selector simply picked the next mislabelled tag — a 2026-09-04 build — and would have rolled
    /// the portal onto it within the hour.
    /// </summary>
    [Fact]
    public void ARetiredPrereleaseLabel_DoesNotOutrankALaterRun()
    {
        Assert.Equal(
            "3.0.0-ci.7977",
            VersionSelect.PickTarget(["3.0.0-rc9.ci.7824", "3.0.0-ci.7977"], UpdatePolicyKind.Continuous));
        Assert.False(VersionSelect.IsNewer("3.0.0-rc9.ci.7824", "3.0.0-ci.7977"));
        Assert.True(VersionSelect.IsNewer("3.0.0-ci.7977", "3.0.0-rc9.ci.7824"));
    }

    /// <summary>
    /// 🚨 The regression the lineage key must NOT introduce. An official release is a PROMOTION and
    /// carries no run number of its own, so a Stable install running a continuous build has only the
    /// version string to compare against — and it must still be able to reach the clean release it is
    /// waiting for. That is why <c>IsNewer</c> falls back to SemVer whenever either side is a
    /// promotion, and why the Stable ordering is untouched.
    ///
    /// <para>🚨 <c>IsNewer</c> is the ORDER, not the POLICY: it says the release is newer, and
    /// <see cref="ContinuousFollowsTheCiLine_AndDoesNotJumpToTheRelease"/> is what stops a Continuous
    /// install acting on that. Both answers are wanted; see <c>OnTheContinuousLine</c>.</para>
    /// </summary>
    [Fact]
    public void AnOfficialRelease_IsStillReachableFromAContinuousBuild()
    {
        Assert.True(VersionSelect.IsNewer("3.1.0", "3.0.0-ci.7977"));
        Assert.True(VersionSelect.IsNewer("3.0.0", "3.0.0-ci.7977"));
        Assert.Equal(
            ["3.1.0", "3.0.0"],
            VersionSelect.PickTargets(["3.0.0", "3.1.0", "3.0.0-ci.7977"], UpdatePolicyKind.Stable));

        // Stable is where a release is actually TAKEN, and it still is.
        var stable = VersionSelect.SelectCandidates(
            ["3.0.0", "3.0.0-ci.7977"], "3.0.0-ci.7977", UpdatePolicyKind.Stable);
        Assert.Equal(["3.0.0"], stable.Candidates);
    }

    /// <summary>
    /// 🚨 <b>"Continuous follows the ci line"</b> — maintainer, 2026-09-07, and the reason it needs a
    /// test rather than a comment. The day <c>v3.0.0</c> is tagged, SemVer ranks the clean release
    /// above every <c>3.0.0-ci.&lt;n&gt;</c>. A Continuous install would take it and then sit there
    /// while later sealed ci builds pile up underneath — the release outranks all of them — which is
    /// the #3542 freeze again, wearing the release's clothes.
    ///
    /// <para>Three cases, and the middle one is the whole point: it is the state right after the tag,
    /// where nothing on the ci line is newer yet and the release is sitting there looking newer.</para>
    /// </summary>
    [Fact]
    public void ContinuousFollowsTheCiLine_AndDoesNotJumpToTheRelease()
    {
        string[] registry = ["3.0.0", "3.0.0-ci.7977", "3.0.0-ci.7989"];

        // 1. The ordering alone already prefers the line: the release is in the trailing band.
        Assert.Equal("3.0.0-ci.7989", VersionSelect.PickTarget(registry, UpdatePolicyKind.Continuous));

        // 2. 🚨 Nothing newer ON the line ⇒ nothing to take. The release must NOT be the answer, and
        //    it is exactly here that SemVer would have said it was.
        var settled = VersionSelect.SelectCandidates(
            registry, "3.0.0-ci.7989", UpdatePolicyKind.Continuous);
        Assert.Empty(settled.Candidates);
        Assert.False(settled.IsRecovery);

        // 3. A newer ci build ⇒ take it, and the release is not even a fallback the gate walk could
        //    drop through to.
        var rolling = VersionSelect.SelectCandidates(
            registry, "3.0.0-ci.7977", UpdatePolicyKind.Continuous);
        Assert.Equal(["3.0.0-ci.7989"], rolling.Candidates);

        // 4. An install NOT on the line is not held off it: it rejoins at the next line's ci builds.
        var offTheLine = VersionSelect.SelectCandidates(
            ["3.0.0", "3.1.0-ci.8100"], "3.0.0", UpdatePolicyKind.Continuous);
        Assert.Equal(["3.1.0-ci.8100"], offTheLine.Candidates);
    }

    /// <summary>
    /// The same-line case, where this selector and the module platform floor MUST agree: two builds of
    /// one line compare by build number, numerically. The floors now name a <c>3.0.0-ci.&lt;n&gt;</c>
    /// (MeshWeaver.Plugins#1447), so that is the whole of the overlap — and it is the one case where
    /// "newer than" and "satisfies the floor of" cannot diverge.
    /// </summary>
    [Fact]
    public void SameLineBuildsCompareNumerically_TheOneCaseTheFloorAlsoAsks()
    {
        Assert.True(VersionSelect.IsNewer("3.0.0-ci.7989", "3.0.0-ci.7977"));
        Assert.False(VersionSelect.IsNewer("3.0.0-ci.7977", "3.0.0-ci.7989"));
        Assert.False(VersionSelect.IsNewer("3.0.0-ci.7989", "3.0.0-ci.7989"));
    }

    /// <summary>The run number is read out of all four shapes the pipeline publishes — both
    /// separators (<c>Directory.Build.props</c> requires it), the retired rc line, the edge channel —
    /// and is absent exactly for a promotion tag.</summary>
    [Theory]
    [InlineData("3.0.0-ci.7977", 7977L)]
    [InlineData("3.0.0-rc9.ci.7824", 7824L)]
    [InlineData("3.0.0-edge.7977", 7977L)]
    [InlineData("3.0.0-ci.7977+build.638", 7977L)]
    [InlineData("3.0.0", null)]
    [InlineData("3.0.0-rc6", null)]
    [InlineData("main", null)]
    public void BuildOrdinal_ReadsThePublishingRunNumber(string version, long? expected)
        => Assert.Equal(expected, VersionSelect.BuildOrdinal(version));

    // ───────── #3543: "I am current" is not the same as "my tag no longer exists" ─────────

    /// <summary>
    /// 🚨 <b>The three states, and why the third one exists.</b> A check that asks a boolean about
    /// something it had to READ must not fold a failed read into a real negative: an unparseable
    /// running version, or a listing carrying no platform tags at all, means the question was never
    /// answered — and answering it "withdrawn" would strand-recover the whole fleet off one bad ACR
    /// response.
    /// </summary>
    [Fact]
    public void CheckInstalledTag_SeparatesCurrent_FromWithdrawn_FromCouldNotTell()
    {
        string[] registry = ["3.0.0-ci.7955", "3.0.0-ci.7977", "main", "6943991"];

        Assert.Equal(
            VersionSelect.InstalledTagResolution.Resolved,
            VersionSelect.CheckInstalledTag(registry, "3.0.0-ci.7977").Resolution);
        // The running InformationalVersion carries +build.<ticks>; the registry tag does not.
        Assert.Equal(
            VersionSelect.InstalledTagResolution.Resolved,
            VersionSelect.CheckInstalledTag(registry, "3.0.0-ci.7977+build.638123456789").Resolution);
        Assert.Equal(
            VersionSelect.InstalledTagResolution.Withdrawn,
            VersionSelect.CheckInstalledTag(registry, "3.1.0-ci.7841").Resolution);
        Assert.Equal(
            VersionSelect.InstalledTagResolution.Indeterminate,
            VersionSelect.CheckInstalledTag(registry, "unknown").Resolution);
        Assert.Equal(
            VersionSelect.InstalledTagResolution.Indeterminate,
            VersionSelect.CheckInstalledTag(["main", "6943991"], "3.0.0-ci.7977").Resolution);
        Assert.Equal(
            VersionSelect.InstalledTagResolution.Indeterminate,
            VersionSelect.CheckInstalledTag([], "3.0.0-ci.7977").Resolution);
    }

    /// <summary>
    /// 🚨 <b>The strand, and the way out.</b> The 3.1.0 tags were untagged from ACR on 2026-09-07, so
    /// the installed tag stopped resolving and the Deployment named an image no new pod could start
    /// from. The check still reported <i>"500 tag(s) listed, none newer than the installed
    /// 3.1.0-ci.7841"</i> — the same sentence a perfectly current install prints — and by construction
    /// nothing would ever be newer than a tag that already outranks everything left.
    ///
    /// <para>So when nothing is newer AND the installed tag is gone, the eligible list becomes a
    /// RECOVERY set: the best AVAILABLE release, even though it is older, because an image that
    /// exists beats one that does not.</para>
    ///
    /// <para>🚨 The numbers here are deliberately NOT the 2026-09-07 ones. Ranking by lineage already
    /// rescues that exact incident — <c>3.0.0-ci.7977</c> was published after <c>3.1.0-ci.7841</c>, so
    /// it now reads as newer and the install rolls forward normally. The strand survives the ordering
    /// fix in the case the ordering cannot reach: the withdrawn tag was the NEWEST publication, which
    /// is the state between an untagging and the next CD run.</para>
    /// </summary>
    [Fact]
    public void AWithdrawnInstalledTag_TurnsTheEligibleListIntoARecoverySet()
    {
        string[] registry = ["3.0.0-ci.7000", "3.0.0-ci.7100"];

        var selection = VersionSelect.SelectCandidates(
            registry, "3.1.0-ci.7841", UpdatePolicyKind.Continuous);

        Assert.True(selection.IsRecovery,
            "nothing is newer than a withdrawn tag, so 'nothing newer' cannot be the answer");
        Assert.Equal(["3.0.0-ci.7100", "3.0.0-ci.7000"], selection.Candidates);
        Assert.Equal(VersionSelect.InstalledTagResolution.Withdrawn, selection.Installed.Resolution);
        Assert.Equal(2, selection.Listed);
    }

    /// <summary>
    /// 🚨 The two fixes meet here, and the ORDER matters: the 2026-09-07 registry is no longer a
    /// strand at all, because <c>3.0.0-ci.7977</c> was PUBLISHED after the withdrawn
    /// <c>3.1.0-ci.7841</c> and lineage says so. The install rolls forward normally, and the recovery
    /// branch — which is allowed to roll BACKWARDS — is never reached. Pinning that keeps a future
    /// change from turning an ordinary update into a downgrade.
    /// </summary>
    [Fact]
    public void TheRealIncident_IsAnOrdinaryRollForward_NotARecovery()
    {
        var selection = VersionSelect.SelectCandidates(
            ["3.0.0-ci.7955", "3.0.0-ci.7962", "3.0.0-ci.7977"],
            "3.1.0-ci.7841",
            UpdatePolicyKind.Continuous);

        Assert.False(selection.IsRecovery);
        Assert.Equal("3.0.0-ci.7977", selection.Candidates[0]);
        Assert.Equal(VersionSelect.InstalledTagResolution.Withdrawn, selection.Installed.Resolution);
    }

    /// <summary>A current install recovers nothing and rolls nothing — the recovery path must be
    /// reachable ONLY from a proven withdrawal, or every check would roll backwards.</summary>
    [Fact]
    public void ACurrentInstall_SelectsNothingAndIsNotRecovering()
    {
        var selection = VersionSelect.SelectCandidates(
            ["3.0.0-ci.7955", "3.0.0-ci.7977"], "3.0.0-ci.7977", UpdatePolicyKind.Continuous);

        Assert.Empty(selection.Candidates);
        Assert.False(selection.IsRecovery);
        Assert.Equal(VersionSelect.InstalledTagResolution.Resolved, selection.Installed.Resolution);
    }

    /// <summary>🚨 A listing that could not answer must NOT recover: "the registry read returned
    /// nothing" and "your tag was withdrawn" have the same shape and opposite meanings, and acting on
    /// the second when it was the first rolls the fleet backwards off one bad response.</summary>
    [Fact]
    public void AnUnreadableListing_RecoversNothing()
    {
        var selection = VersionSelect.SelectCandidates([], "3.1.0-ci.7841", UpdatePolicyKind.Continuous);

        Assert.Empty(selection.Candidates);
        Assert.False(selection.IsRecovery);
        Assert.Equal(
            VersionSelect.InstalledTagResolution.Indeterminate, selection.Installed.Resolution);
    }

    /// <summary>A newer release is still an ordinary update — the recovery branch is only reached
    /// once the newer set is empty, so a withdrawn tag with something newer above it rolls forward
    /// normally and says nothing about a strand.</summary>
    [Fact]
    public void SomethingNewer_IsAnOrdinaryUpdate_EvenWhenTheInstalledTagIsGone()
    {
        var selection = VersionSelect.SelectCandidates(
            ["3.0.0-ci.7977"], "3.1.0-ci.7000", UpdatePolicyKind.Continuous);

        Assert.Equal(["3.0.0-ci.7977"], selection.Candidates);
        Assert.False(selection.IsRecovery);
    }
}
