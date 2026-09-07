using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using NuGet.Versioning;
using MeshWeaver.Hosting.SelfUpdate;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// Picks the update target from a registry's image tags, per the update policy, and decides whether a
/// target is newer than the running version.
///
/// <para>🚨 <b>The key is the SEALED-PUBLICATION LINEAGE, never the version string (#3542).</b> Every
/// continuous image is tagged <c>&lt;line&gt;[-.]ci.&lt;run&gt;</c>, where <c>&lt;run&gt;</c> is the
/// GitHub Actions run number of the delivery workflow that published it —
/// <c>Directory.Build.props</c> calls that number MONOTONIC and load-bearing, and
/// <c>MissedBuildFact</c> already orders publications by the same value. The version LINE in front of
/// it is a label an author maintains by hand, and a hand-maintained label can be wrong: on
/// 2026-09-05 <c>Directory.Build.props</c> briefly read <c>3.1.0</c>, so ten sets were published as
/// <c>3.1.0-ci.7832…7841</c> and then withdrawn. Ordered by SemVer those outrank every later, sealed
/// <c>3.0.0-ci.79xx</c> set FOREVER — and they did: memex-cloud rolled itself onto
/// <c>3.1.0-ci.7841</c> and then reported <i>"500 tag(s) listed, none newer than the installed
/// 3.1.0-ci.7841"</i> on every subsequent check, because nothing ever outranks the highest-sorting
/// tag. SemVer §11.4 produces the same trap one layer down: the pre-release identifiers <c>ci</c> and
/// <c>rc9</c> compare as TEXT, so <c>3.0.0-rc9.ci.7824</c> (2026-09-04) sorts above
/// <c>3.0.0-ci.7977</c> (2026-09-07).</para>
///
/// <para>So the run number is the ordering key wherever both sides carry one, and the version string
/// is what it always was — a label. A tag with a higher line and a LOWER run number is exactly the
/// slip case, and it loses.</para>
///
/// <para>An official release tag (clean <c>3.0.0</c>) carries no run number of its own: it is a
/// PROMOTION, a retag of one sealed continuous set (<c>release.yml</c>), so its lineage lives on the
/// sibling tag it was cut from and cannot be read off the tag. That is why the two questions this
/// class answers use the key differently, and deliberately:</para>
/// <list type="bullet">
/// <item><see cref="PickTargets"/> needs a TOTAL order over a heterogeneous set, so lineage-bearing
/// tags order among themselves by run number and rank ahead of the promotion tags, which order among
/// themselves by SemVer. Under <see cref="UpdatePolicyKind.Stable"/> no continuous tag is eligible at
/// all, so that branch is a pure SemVer ordering exactly as before; under
/// <see cref="UpdatePolicyKind.Continuous"/> a promotion tag names the same bytes as a continuous tag
/// already in the list, so ranking it behind them costs nothing.</item>
/// <item><see cref="IsNewer"/> is a PAIRWISE question with no transitivity to preserve, so it answers
/// on the strongest key BOTH sides share: run number when both are continuous builds, SemVer when
/// either side is a promotion. That is what keeps a Stable install on a continuous build able to
/// reach the clean release it is waiting for.</item>
/// </list>
///
/// <para>🔴 The run number must stay MONOTONIC (<c>Directory.Build.props</c>, fed from
/// <c>GITHUB_RUN_NUMBER</c>). A non-monotonic build number would make a newer build sort lower and
/// break "pick the newest" — that is the one property this ordering rests on, and it is the property
/// the version string does NOT have.</para>
/// </summary>
public static class VersionSelect
{
    // The multi-arch container build publishes, per version, a manifest-list tag (e.g. 3.0.0-ci.43) PLUS
    // one image per RID (3.0.0-ci.43-linux-x64, 3.0.0-ci.43-linux-arm64). The RID suffix parses as an extra
    // SemVer pre-release identifier that sorts ABOVE the clean tag (numeric 43 < alphanumeric 43-linux-x64),
    // so without this filter PickTarget rolls to the x64-only image — wrong arch on an arm64 node, and never
    // the intended manifest list. Drop RID-suffixed tags; the manifest list is the canonical deploy tag.
    private static readonly Regex RuntimeIdentifierSuffix =
        new(@"-(linux|win|osx)-(x64|x86|arm|arm64)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 🔴 A real platform tag is DOTTED SemVer (3.0.0 / 3.0.0-ci.133). The multi-arch build ALSO pushes a
    // 7-char git-sha tag per version (e.g. 6943991, 4779b4e) and a bare `main` tag — those are NOT deploy
    // targets. But NuGetVersion.TryParse accepts a bare number, so an ALL-DIGIT sha like "6943991" parses
    // as the version 6943991.0.0 and sorts ABOVE every real release (3.x). PickTarget would then pick that
    // sha and pin the self-updater to whatever release it belongs to — the prod symptom where every portal
    // froze on ci.122 (whose sha, 6943991, is all digits) and reverted any manual roll to a newer ci.N
    // (ci.133's sha, 4779b4e, has letters so it never even parsed). Require the MAJOR.MINOR.PATCH dotted
    // shape that a git-sha / `main` can never have.
    private static readonly Regex PlatformVersionTag =
        new(@"^\d+\.\d+\.\d+([-+].*)?$", RegexOptions.Compiled);

    /// <summary>
    /// The pre-release identifiers that mark a CD channel and are FOLLOWED by the publishing run
    /// number: <c>3.0.0-ci.7977</c>, the retired <c>3.0.0-rc9.ci.7824</c>, and the unverified
    /// <c>3.0.0-edge.7977</c> (<c>edge-images.yml</c> rewrites <c>.ci.</c> to <c>.edge.</c> and keeps
    /// the same number). Both separators must be accepted — <c>Directory.Build.props</c> says so in as
    /// many words, because the retired rc line used <c>.ci.</c> and its tags are still in ACR.
    /// </summary>
    private static readonly string[] ChannelLabels = ["ci", "edge"];

    /// <summary>
    /// The CD run number <paramref name="version"/> was published by — its SEALED-PUBLICATION LINEAGE
    /// — or <c>null</c> for a version that carries none (an official release, which is a promotion of
    /// a set rather than a publication of its own; or anything unparseable).
    ///
    /// <para>Read as the numeric identifier that FOLLOWS a channel label, so all four shapes the
    /// pipeline produces answer the same way: <c>3.0.0-ci.7977</c> → 7977, <c>3.0.0-rc9.ci.7824</c> →
    /// 7824, <c>3.0.0-edge.7977</c> → 7977, <c>3.0.0</c> → null.</para>
    /// </summary>
    public static long? BuildOrdinal(string version) =>
        NuGetVersion.TryParse(version, out var parsed) ? BuildOrdinal(parsed) : null;

    private static long? BuildOrdinal(NuGetVersion version)
    {
        var labels = version.ReleaseLabels?.ToArray() ?? [];
        for (var i = 0; i < labels.Length - 1; i++)
            if (ChannelLabels.Contains(labels[i], StringComparer.OrdinalIgnoreCase)
                && long.TryParse(
                    labels[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
                return ordinal;

        return null;
    }

    /// <summary>
    /// The best tag to roll to under <paramref name="policy"/>, or <c>null</c> when nothing qualifies.
    /// <see cref="UpdatePolicyKind.Continuous"/> considers every parseable tag (incl. build-numbered
    /// pre-releases); <see cref="UpdatePolicyKind.Stable"/> considers only clean releases
    /// (<c>!IsPrerelease</c>); <see cref="UpdatePolicyKind.None"/> always returns <c>null</c>.
    /// Returns the ORIGINAL tag string (so the image patch uses the exact registry tag).
    ///
    /// <para>Literally the head of <see cref="PickTargets"/> — one filter, one ordering, no second
    /// copy of "which release is best" to drift from the first.</para>
    /// </summary>
    public static string? PickTarget(
        IEnumerable<string> tags, UpdatePolicyKind policy, bool requireCiGreen = true) =>
        PickTargets(tags, policy, requireCiGreen).FirstOrDefault();

    /// <summary>
    /// Every eligible tag under <paramref name="policy"/>, NEWEST FIRST — where "newest" is the
    /// sealed-publication lineage described on <see cref="VersionSelect"/>, not the version string.
    /// <see cref="PickTarget"/> is simply this sequence's first element.
    ///
    /// <para>🚨 <b>Why the caller needs the whole ordered list and not just the best one.</b> A
    /// target is only rollable if a sealed content bake exists for the framework identity that
    /// exact image resolves to. Picking the newest tag and stopping means one unbaked release
    /// freezes the instance FOREVER: it holds, the next platform build produces another unbaked
    /// tag, the bake publishes yet another identity, and the two never meet. That is not
    /// hypothetical — memex sat on 3.0.0-rc6 held against 3.0.0-rc7.ci.4928 while three separate
    /// bakes published three other identities, every job green throughout.</para>
    ///
    /// <para>Walking the list newest-first and taking the first SEALED one converts that deadlock
    /// into ordinary progress: the instance always advances to the best release that can actually
    /// serve, and a not-yet-baked newer build simply waits its turn instead of blocking the ones
    /// behind it. It never rolls backwards — the caller still requires the target to be newer than
    /// what is installed — and it never rolls into a boot storm, because unsealed candidates are
    /// skipped rather than forced.</para>
    /// </summary>
    public static IReadOnlyList<string> PickTargets(
        IEnumerable<string> tags, UpdatePolicyKind policy, bool requireCiGreen = true)
    {
        if (policy == UpdatePolicyKind.None)
            return [];

        var parsed = Parse(tags);

        if (policy == UpdatePolicyKind.Stable)
            parsed = parsed.Where(x => !x.ver.IsPrerelease);

        // CI-green gate: the verified channel (continuous delivery, which builds+pushes ONLY when the
        // test workflow is green) never carries the `edge` pre-release label. An unverified "edge"
        // channel (publish-on-every-build, e.g. `3.0.0-edge.51`) would. requireCiGreen excludes those,
        // so the install never auto-rolls to a build that hasn't passed CI. Off => edge builds eligible.
        if (requireCiGreen)
            parsed = parsed.Where(x => !IsEdge(x.ver));

        return
        [
            .. parsed
                // 🚨 THE ORDER, and the whole of #3542. Lineage first: a continuous build is ranked by
                // the run number that published it, so a mislabelled line can never outrank a later
                // sealed set. Promotion tags (a clean release, which has no run number of its own)
                // follow, ordered among themselves by SemVer — which is the only key they share, and
                // the correct one for a deliberate human act. Keeping them in a separate band is what
                // makes this a TOTAL order: mixing the two keys pairwise is not transitive (a slip tag
                // A beats a release C beats a sealed tag B beats A), and an intransitive comparer hands
                // a sort an arbitrary answer.
                .OrderByDescending(x => x.ordinal.HasValue)
                .ThenByDescending(x => x.ordinal ?? 0L)
                .ThenByDescending(x => x.ver)
                .Select(x => x.tag),
        ];
    }

    /// <summary>The structural filter every reader of a tag listing applies: drop the per-RID images
    /// and the git-sha / <c>main</c> pointers, keep what parses as a platform version.</summary>
    private static IEnumerable<(string tag, NuGetVersion ver, long? ordinal)> Parse(
        IEnumerable<string> tags) =>
        tags
            .Where(t => !RuntimeIdentifierSuffix.IsMatch(t))   // exclude per-RID image tags; keep the manifest list
            .Where(t => PlatformVersionTag.IsMatch(t))         // exclude bare git-sha / `main` tags (see PlatformVersionTag)
            .Select(t => (tag: t, ver: NuGetVersion.TryParse(t, out var v) ? v : null))
            .Where(x => x.ver is not null)
            .Select(x => (x.tag, ver: x.ver!, ordinal: BuildOrdinal(x.ver!)));

    /// <summary>An UNVERIFIED edge/pre-merge build — identified by an <c>edge</c> SemVer pre-release
    /// label (e.g. <c>3.0.0-edge.51</c>). Verified CD builds use <c>-ci.&lt;n&gt;</c> or a clean release,
    /// never <c>edge</c>.</summary>
    private static bool IsEdge(NuGetVersion version) =>
        version.ReleaseLabels.Any(label => string.Equals(label, "edge", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True if <paramref name="targetTag"/> is strictly newer than <paramref name="currentVersion"/> —
    /// by SEALED-PUBLICATION LINEAGE when both name a continuous build, and by SemVer when either side
    /// is an official release (which carries no run number of its own; see <see cref="VersionSelect"/>).
    /// An unparseable current version (e.g. <c>"unknown"</c> on an unstamped build) returns
    /// <c>false</c> — we never auto-update when we can't establish the running version.
    /// </summary>
    public static bool IsNewer(string targetTag, string currentVersion)
    {
        if (!NuGetVersion.TryParse(targetTag, out var target))
            return false;
        if (!NuGetVersion.TryParse(currentVersion, out var current))
            return false;

        // 🚨 Both sides are CD publications ⇒ the run number decides and the version line is ignored.
        // This is what lets an install stranded on a withdrawn 3.1.0-ci.7841 see the sealed
        // 3.0.0-ci.7977 as newer, and what stops a 3.0.0-rc9.ci.7824 from looking newer than it is.
        if (BuildOrdinal(target) is { } targetOrdinal && BuildOrdinal(current) is { } currentOrdinal)
            return targetOrdinal > currentOrdinal;

        // One side is a promotion (a clean release) with no lineage of its own — the version string is
        // the only key both sides share, and for a deliberately cut release it is a trustworthy one.
        // Keeping this branch on SemVer is what preserves the Stable path: an install running a
        // continuous build must still be able to reach the clean release it is waiting for.
        return target > current;
    }

    /// <summary>
    /// What ONE check may roll to, and why — the whole selection decision, pure, so it is testable
    /// without a poller, a registry or a mesh.
    /// </summary>
    /// <param name="Candidates">Newest first, ready for the availability/combo walk. Empty when there
    /// is nothing to roll to.</param>
    /// <param name="IsRecovery">🚨 True when these candidates are NOT newer than what is installed:
    /// the installed tag no longer resolves, so the best AVAILABLE release was taken instead of
    /// reporting "up to date". The roll may go backwards in lineage — deliberately, because an image
    /// that exists beats one that does not.</param>
    /// <param name="Installed">Whether the installed tag still resolves, and the sentence explaining
    /// it.</param>
    /// <param name="Listed">How many tags the registry answered with — quoted in the verdict, and the
    /// number a reader uses to judge whether the listing was worth anything.</param>
    public readonly record struct RollCandidates(
        ImmutableArray<string> Candidates,
        bool IsRecovery,
        InstalledTagCheck Installed,
        int Listed);

    /// <summary>
    /// 🚨 <b>The check's decision, in one place: what may this install roll to?</b>
    ///
    /// <para>Normally: every eligible tag that is NEWER than what runs, newest first. The interesting
    /// part is what happens when that set is EMPTY, because until #3543 there was only one answer for
    /// it — "up to date" — and two states produce it. The second is that the installed tag has been
    /// WITHDRAWN: the workloads name an image that no longer exists, no new pod can start, and no
    /// future publication can rescue the install by being "newer", since the withdrawn tag outranks
    /// everything left by construction. That is not "current"; it is stranded, and the way out is to
    /// take the best AVAILABLE release even though it is older.</para>
    ///
    /// <para>The third state — the listing could not answer — takes NEITHER branch. See
    /// <see cref="InstalledTagResolution.Indeterminate"/>: a failed read that recovered as if the tag
    /// were withdrawn would roll the whole fleet backwards off one bad ACR response.</para>
    /// </summary>
    public static RollCandidates SelectCandidates(
        IReadOnlyList<string> tags,
        string installedVersion,
        UpdatePolicyKind policy,
        bool requireCiGreen = true)
    {
        var eligible = PickTargets(tags, policy, requireCiGreen);
        var newer = eligible.Where(tag => IsNewer(tag, installedVersion)).ToImmutableArray();
        var installed = CheckInstalledTag(tags, installedVersion);

        if (newer.Length > 0)
            return new(newer, IsRecovery: false, installed, tags.Count);

        return installed.Resolution == InstalledTagResolution.Withdrawn
            ? new([.. eligible], IsRecovery: true, installed, tags.Count)
            : new([], IsRecovery: false, installed, tags.Count);
    }

    /// <summary>
    /// Whether the tag this install RUNS still resolves in a registry listing — the three-valued
    /// answer to a question that must never be collapsed into a boolean (#3543).
    /// </summary>
    public enum InstalledTagResolution
    {
        /// <summary>🚨 The listing could not answer the question at all: the running build reports no
        /// parseable platform version, or the listing carries no platform tags to compare against. A
        /// failed read, NOT a negative — reporting it as <see cref="Withdrawn"/> would roll an install
        /// off a perfectly good image on the strength of a read that never happened.</summary>
        Indeterminate,

        /// <summary>The listing contains the installed version. "Nothing newer" here genuinely means
        /// up to date.</summary>
        Resolved,

        /// <summary>🚨 The listing is populated and does NOT contain the installed version: the image
        /// this install's Deployment names has been untagged, so it cannot start a new pod at all.
        /// "Nothing newer than the installed X" is then not a healthy verdict — it is a STRAND, and by
        /// construction nothing will ever be newer than a tag that outranks everything left.</summary>
        Withdrawn,
    }

    /// <summary>The <see cref="InstalledTagResolution"/> plus the sentence that explains it — carried
    /// together so a caller can report the reason without re-deriving it.</summary>
    /// <param name="Resolution">Which of the three states the listing established.</param>
    /// <param name="Explanation">One clause, ready to fold into a verdict message.</param>
    public readonly record struct InstalledTagCheck(InstalledTagResolution Resolution, string Explanation);

    /// <summary>
    /// 🚨 <b>Does the tag this install runs still exist in <paramref name="tags"/>?</b>
    ///
    /// <para>The listing is already fetched — the "no newer release" verdict quotes its size — so this
    /// costs nothing beyond a scan, and it is the difference between the two states that until now
    /// printed the same sentence: <i>"I am current"</i> and <i>"the version I run was withdrawn and I
    /// can never move again"</i>. Both portals sat in the second on 2026-09-07 and had to be moved off
    /// by an operator.</para>
    ///
    /// <para>Compared by parsed VERSION, not by string: the running value carries
    /// <c>+build.&lt;ticks&gt;</c> build metadata that the registry tag does not, and SemVer ignores
    /// metadata in comparison. The structural filters are the same ones
    /// <see cref="PickTargets"/> applies — a per-RID image or a git-sha pointer is not the tag a
    /// Deployment names — but the POLICY filters deliberately are not: the question is whether the
    /// image still exists, never whether this install would choose it.</para>
    /// </summary>
    public static InstalledTagCheck CheckInstalledTag(IEnumerable<string> tags, string installedVersion)
    {
        if (!NuGetVersion.TryParse(installedVersion, out var installed))
            return new(
                InstalledTagResolution.Indeterminate,
                $"the running build reports '{installedVersion}', which is not a platform version, so "
                + "whether its image still exists could not be established");

        var listed = Parse(tags).Select(x => x.ver).ToArray();

        // 🚨 The denominator, checked before the verdict. A listing with no platform tags in it is the
        // shape of a read that answered nothing — an unreachable registry, the wrong repository, a
        // paged response that came back empty — and it is INDISTINGUISHABLE from a registry that
        // genuinely holds none. Calling that "withdrawn" would strand-recover every install in the
        // fleet off a single bad read.
        if (listed.Length == 0)
            return new(
                InstalledTagResolution.Indeterminate,
                "the registry listing carried no platform version tags at all, which is the same shape "
                + "as a read that returned nothing — so it cannot be read as 'the installed tag is gone'");

        return listed.Any(v => v == installed)
            ? new(InstalledTagResolution.Resolved, $"the installed {installedVersion} is still published")
            : new(
                InstalledTagResolution.Withdrawn,
                $"the installed {installedVersion} is NOT among the {listed.Length} platform tag(s) "
                + "in the registry — the image this install's workloads name has been untagged, so no "
                + "new pod can start from it");
    }
}
