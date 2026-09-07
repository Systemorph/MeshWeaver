using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>The version scheme has exactly TWO shapes, and this is what makes that true rather than
/// merely written down.</b>
///
/// <para>Maintainer, 2026-09-07: <i>"we will then not introduce the rc line anymore"</i> ·
/// <i>"let's just use version numbers -ci for temp then without -ci for final version"</i>. So:</para>
///
/// <list type="bullet">
/// <item><c>X.Y.Z-ci.&lt;n&gt;</c> — every continuous / temporary build.</item>
/// <item><c>X.Y.Z</c> — the release, with no pre-release label at all.</item>
/// </list>
///
/// <para>No <c>rc</c>, no <c>preview</c>, no <c>beta</c>, no labelled line, no <c>.ci.</c> separator
/// variant. Ever. The rule is stated in <c>Doc/Architecture/ReleaseProcess</c> §1 and in
/// <c>Directory.Build.props</c> itself — and AGENTS.md is explicit that a rule living only in prose
/// is not a rule: <i>"Prose asserts guards that do not exist"</i>. This is the guard.</para>
///
/// <para><b>What a violation costs.</b> A label is not cosmetic, because SemVer §11.4 compares
/// pre-release identifiers as TEXT: <c>"ci" &lt; "rc"</c>, so <c>3.0.0-ci.&lt;n&gt;</c> ranks below
/// <c>3.0.0-rc8</c> for EVERY n. Measured 2026-09-07 (Systemorph/MeshWeaver#3554): 42 packages
/// declared rc-line or clean-<c>3.0.0</c> floors that no shipping platform could satisfy, every
/// self-update candidate was held on both AKS portals, and every open pull request in
/// MeshWeaver.Plugins went red — while the registry held 1268 tags of which 48 were
/// <c>3.0.0-ci.*</c> and ZERO were <c>rc*</c>. Both failures are silent: the portal logs a hold and
/// carries on, and nothing in the release process asks whether the label it minted is orderable.</para>
///
/// <para><b>Why MSBuild and not a text match on the props file.</b> The thing that must hold is the
/// COMPOSED string — what reaches an image tag, a package version and
/// <c>MESHWEAVER_PLATFORM_VERSION</c> — and that is the product of five conditioned properties. A
/// regex over the XML would pass a props file whose <c>PlatformVersion</c> is clean but whose
/// composition reintroduces a label somewhere downstream. So this evaluates the real project through
/// the real evaluator, exactly as CD does, via <see cref="MsBuildPropertyProbe"/>.</para>
///
/// <para>🚨 <b><c>edge</c> is not a third shape, and widening the pattern to admit it would be a
/// mistake.</b> <c>edge-images.yml</c> computes <c>$(Version)</c> from this tree FIRST — so it starts
/// from <c>X.Y.Z-ci.&lt;n&gt;</c> — then rewrites the <c>ci</c> label to <c>edge</c> and keeps the same
/// run number. What this guard measures is <c>$(Version)</c> itself, before any lane renames it, and
/// that is exactly the surface the rule is about. A new delivery channel is added by extending
/// <c>PlatformReleaseOrder.ChannelLabels</c> and the re-label, never by putting a pre-release label on
/// <c>PlatformVersion</c>.</para>
///
/// <para>🚨 <b>MINTING one shape is not READING one.</b> This guard binds the MINTER only. The
/// rc-line images minted with the <c>.ci.</c> separator are still addressable, an install can be
/// running one, and every consumer that parses the run number back out of a version must keep
/// accepting both separators — <c>PlatformReleaseOrder.BuildOrdinal</c>, <c>VersionSelect</c>,
/// <c>edge-images.yml</c>, MeshWeaver.Plugins' <c>check-platform-pins.py</c>. That side is pinned by
/// <c>PlatformReleaseOrderTest</c> (<c>3.0.0-rc9.ci.7824</c> → <c>7824</c>); do not "simplify" it to
/// agree with this one, or a retired tag reads as carrying no run number and is promoted into the
/// promotion-ranked half of the self-updater's order — issue #3542's freeze, rebuilt by a tidy-up.</para>
/// </summary>
public class PlatformVersionSchemeGuard
{
    /// <summary>
    /// The project evaluated. Any project inheriting the root <c>Directory.Build.props</c> would do;
    /// this is the one <see cref="CompiledVersionAttributesIgnoreVersionOverrideGuard"/> already
    /// probes, so the two guards measure the same composition.
    /// </summary>
    private const string ProbeProject = "src/MeshWeaver.ShortGuid/MeshWeaver.ShortGuid.csproj";

    /// <summary>The maintained line: three numeric parts and NOTHING else.</summary>
    private static readonly Regex CleanLine = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);

    /// <summary>
    /// The whole scheme: <c>X.Y.Z</c> (the release) or <c>X.Y.Z-ci.&lt;n&gt;</c> (every continuous
    /// build). Deliberately anchored, deliberately case-sensitive, and deliberately without an
    /// alternative separator — <c>X.Y.Z-&lt;label&gt;.ci.&lt;n&gt;</c> is the retired shape.
    /// </summary>
    private static readonly Regex TwoShapes = new(@"^\d+\.\d+\.\d+(-ci\.\d+)?$", RegexOptions.Compiled);

    private static readonly string[] Probed = ["PlatformVersion", "Version"];

    /// <summary>
    /// <c>PlatformVersion</c> — the one maintained number — carries no pre-release label. Everything
    /// else in the scheme is derived from it, so a label here is the single edit that can put the
    /// whole fleet back into #3554.
    /// </summary>
    [Fact]
    public void TheMaintainedPlatformVersionCarriesNoPreReleaseLabel()
    {
        var platformVersion = Evaluate()["PlatformVersion"];

        Assert.False(string.IsNullOrWhiteSpace(platformVersion),
            "$(PlatformVersion) evaluated empty — the root Directory.Build.props no longer sets the "
            + "one maintained number, so every derived version is an SDK default.");

        Assert.True(CleanLine.IsMatch(platformVersion),
            $"PlatformVersion is '{platformVersion}', which carries a pre-release label. The scheme "
            + "has exactly two shapes — X.Y.Z-ci.<n> for every continuous build and clean X.Y.Z for "
            + "the release (maintainer, 2026-09-07) — and this property is the clean line, always. "
            + "SemVer §11.4 compares pre-release identifiers as TEXT, so any label you add here "
            + "outranks or is outranked by 'ci' as a WORD: that is how 42 module packages came to "
            + "declare floors no 3.0.0-ci.<n> platform can ever satisfy, holding every self-update "
            + "candidate on both AKS portals (#3554). See Doc/Architecture/ReleaseProcess §1.");
    }

    /// <summary>
    /// The composed <c>$(Version)</c> — the image tag, the package version and
    /// <c>MESHWEAVER_PLATFORM_VERSION</c> — is one of the two shapes on BOTH channels: the
    /// continuous default and <c>-p:PublicRelease=true</c>.
    /// </summary>
    [Fact]
    public void TheComposedVersionIsOneOfTheTwoShapes()
    {
        var continuous = Evaluate()["Version"];
        var released = Evaluate("-p:PublicRelease=true")["Version"];

        AssertIsOneOfTheTwoShapes(continuous, "the continuous channel (the default)");
        AssertIsOneOfTheTwoShapes(released, "the released channel (-p:PublicRelease=true)");

        // 🚨 Shape-conformance alone is not enough: `X.Y.Z` satisfies TwoShapes, so a composition
        // that dropped the suffix entirely would pass the assertion above while making every
        // continuous build indistinguishable from the release it has not been promoted to.
        Assert.True(continuous.Contains("-ci.", StringComparison.Ordinal),
            $"the continuous channel composed '{continuous}', which carries no -ci.<n> suffix. "
            + "Every continuous and temporary build must be distinguishable from the release: the "
            + "release is a PROMOTION of a sealed continuous set (release.yml retags it), so a "
            + "continuous build wearing the clean version would collide with the very tag the "
            + "promotion writes.");

        // …and the release channel must be clean, for the same reason read the other way.
        Assert.True(CleanLine.IsMatch(released),
            $"the released channel composed '{released}', which is not a clean X.Y.Z. A Stable "
            + "install selects !IsPrerelease, so a labelled release is invisible to every install "
            + "waiting for it.");
    }

    /// <summary>
    /// 🚨 <b>The control arm — proof this guard can FAIL.</b> Reintroduce the retired label through
    /// the real evaluator and require the composed version to be REJECTED. Without this the two
    /// assertions above pass in every world where the regex is wrong, the probe silently returned a
    /// constant, or the override never reached MSBuild — a guard measuring nothing, dressed as a
    /// green tick (AGENTS.md: <i>"a verification step that cannot fail is not a verification step"</i>).
    ///
    /// <para>It doubles as the executable statement of what a labelled line DOES to the composition
    /// now that <c>_CiSep</c> is retired: there is no branch left to make <c>3.0.0-rc8</c> compose a
    /// well-formed <c>.ci.</c> pre-release, so the minter cannot emit the retired shape even under
    /// an override — it emits something outside the scheme, and this says so.</para>
    /// </summary>
    [Fact]
    public void ReintroducingALabelLeavesTheTwoShapes_MeasuredThroughRealMSBuild()
    {
        const string RetiredLine = "3.0.0-rc8";

        var probed = Evaluate($"-p:PlatformVersion={RetiredLine}");
        var platformVersion = probed["PlatformVersion"];
        var composed = probed["Version"];

        // The override actually reached the evaluation — otherwise the rejection below would be a
        // rejection of nothing, and this whole test would be measuring the bare build twice.
        Assert.Equal(RetiredLine, platformVersion);

        Assert.False(CleanLine.IsMatch(platformVersion),
            "the clean-line predicate accepted a labelled PlatformVersion, so "
            + $"{nameof(TheMaintainedPlatformVersionCarriesNoPreReleaseLabel)} cannot fail and is "
            + "guarding nothing. Fix the predicate, not this assertion.");

        Assert.False(TwoShapes.IsMatch(composed),
            $"$(Version) composed '{composed}' from the retired line '{RetiredLine}' and the "
            + "two-shape predicate ACCEPTED it, so "
            + $"{nameof(TheComposedVersionIsOneOfTheTwoShapes)} cannot fail and is guarding nothing. "
            + "Fix the predicate, not this assertion.");
    }

    /// <summary>
    /// The predicate itself, against every shape the platform has actually minted or been asked to
    /// mint. Pure — no MSBuild — so it stays a readable statement of the scheme, and so the retired
    /// shapes are written down somewhere that fails when someone widens the pattern to admit them.
    /// </summary>
    [Theory]
    // The scheme.
    [InlineData("3.0.0", true)]
    [InlineData("3.0.0-ci.0", true)]
    [InlineData("3.0.0-ci.7989", true)]
    [InlineData("4.12.7-ci.1", true)]
    // The retired rc line — both the label alone and the `.ci.` separator it minted.
    [InlineData("3.0.0-rc8", false)]
    [InlineData("3.0.0-rc13", false)]
    [InlineData("3.0.0-rc9.ci.7818", false)]
    // Labels that were never minted here and never may be.
    [InlineData("3.0.0-preview1", false)]
    [InlineData("3.0.0-beta.1", false)]
    [InlineData("3.0.0-alpha", false)]
    // Near-misses: the suffix must be exactly `-ci.<digits>`, case included.
    [InlineData("3.0.0-CI.7989", false)]
    [InlineData("3.0.0-ci", false)]
    [InlineData("3.0.0-ci.", false)]
    [InlineData("3.0.0-ci.abc", false)]
    [InlineData("3.0.0.7989", false)]
    [InlineData("3.0-ci.7989", false)]
    public void ThePredicateAdmitsTheSchemeAndNothingElse(string version, bool admitted)
        => Assert.Equal(admitted, TwoShapes.IsMatch(version));

    private static void AssertIsOneOfTheTwoShapes(string version, string channel)
    {
        Assert.False(string.IsNullOrWhiteSpace(version),
            $"$(Version) evaluated empty on {channel} — the composition in Directory.Build.props no "
            + "longer produces a version, so the SDK's default is what would tag the image.");

        Assert.True(TwoShapes.IsMatch(version),
            $"$(Version) composed '{version}' on {channel}, which is not one of the two shapes "
            + "(X.Y.Z-ci.<n> or X.Y.Z). This string becomes the ACR/GHCR image tag, the package "
            + "version and MESHWEAVER_PLATFORM_VERSION, and it is ordered against every other tag "
            + "in the registry — a third shape is either unorderable or orders by TEXT against 'ci' "
            + "(SemVer §11.4), which is #3554 and #3542. See Doc/Architecture/ReleaseProcess §1, and "
            + "note the separator is a literal '-': the '.ci.' form is READ for history and never "
            + "minted again.");
    }

    private static IReadOnlyDictionary<string, string> Evaluate(params string[] extraArguments)
        => MsBuildPropertyProbe.Evaluate(ProbeProject, Probed, extraArguments);
}
