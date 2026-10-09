using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>The version scheme, made true rather than merely written down</b> — policy
/// <c>platform-semver-versioning</c> (<c>Doc/Architecture/PlatformVersioning</c>).
///
/// <list type="bullet">
/// <item><c>PlatformVersion</c> names a LINE: <c>&lt;major&gt;.&lt;minor&gt;.0</c>, no label, patch 0.</item>
/// <item>A main CD build (<c>-p:PlatformBuildNumber=&lt;run&gt;</c>, passed only by
/// <c>main-cd.yml</c>): <c>&lt;major&gt;.&lt;minor&gt;.&lt;run&gt;</c> — a plain release version,
/// one per build, monotonic.</item>
/// <item>The release promotion (<c>-p:PublicRelease=true</c>): the clean line
/// <c>&lt;major&gt;.&lt;minor&gt;.0</c>.</item>
/// <item>Every other build (pull-request runs, local dev, satellite lanes compiling core):
/// <c>&lt;major&gt;.&lt;minor&gt;.0-dev</c> — it publishes no platform version and says so.</item>
/// </list>
///
/// <para>No channel word, no <c>ci</c>, no <c>rc</c>, no <c>preview</c> is ever MINTED. The retired
/// notations (<c>3.0.0-ci.&lt;run&gt;</c>, <c>3.0.0-rc9.ci.&lt;run&gt;</c>) are still READ by every
/// consumer (<c>PlatformReleaseOrderTest</c> pins that side) — this guard binds the minter only.</para>
///
/// <para><b>What a violation costs.</b> A label is not cosmetic, because SemVer §11.4 compares
/// pre-release identifiers as TEXT: in Sept. 2026 (#3554) 42 packages declared rc-line floors that no
/// shipping <c>3.0.0-ci.&lt;n&gt;</c> platform could satisfy and every self-update candidate was held on
/// both AKS portals. And a build version minted from the WRONG run counter (a pull-request run's
/// <c>GITHUB_RUN_NUMBER</c>) would be a clean release version that outranks or collides with a real
/// build — which is why the patch is handed in by name and never read from the environment.</para>
///
/// <para><b>Why MSBuild and not a text match on the props file.</b> The thing that must hold is the
/// COMPOSED string — what reaches an image tag, a package version and
/// <c>MESHWEAVER_PLATFORM_VERSION</c> — so this evaluates the real project through the real
/// evaluator, exactly as CD does, via <see cref="MsBuildPropertyProbe"/>.</para>
/// </summary>
public class PlatformVersionSchemeGuard
{
    /// <summary>
    /// The project evaluated. Any project inheriting the root <c>Directory.Build.props</c> would do;
    /// this is the one <see cref="CompiledVersionAttributesIgnoreVersionOverrideGuard"/> already
    /// probes, so the two guards measure the same composition.
    /// </summary>
    private const string ProbeProject = "src/MeshWeaver.ShortGuid/MeshWeaver.ShortGuid.csproj";

    /// <summary>The maintained line: <c>&lt;major&gt;.&lt;minor&gt;.0</c> and NOTHING else.</summary>
    private static readonly Regex Line = new(@"^\d+\.\d+\.0$", RegexOptions.Compiled);

    /// <summary>A main CD build: <c>&lt;major&gt;.&lt;minor&gt;.&lt;run&gt;</c>, run ≥ 1.</summary>
    private static readonly Regex Build = new(@"^\d+\.\d+\.[1-9]\d*$", RegexOptions.Compiled);

    /// <summary>A build that publishes no platform version: <c>&lt;major&gt;.&lt;minor&gt;.0-dev</c>.</summary>
    private static readonly Regex Source = new(@"^\d+\.\d+\.0-dev$", RegexOptions.Compiled);

    private static readonly string[] Probed = ["PlatformVersion", "Version", "AssemblyVersion"];

    /// <summary>
    /// <c>PlatformVersion</c> — the one maintained number — is a line: no label, patch 0. A label
    /// here is the single edit that could put the fleet back into #3554, and a non-zero patch would
    /// make the release promotion collide with a real build.
    /// </summary>
    [Fact]
    public void TheMaintainedPlatformVersionIsALine()
    {
        var platformVersion = Evaluate()["PlatformVersion"];

        Assert.False(string.IsNullOrWhiteSpace(platformVersion),
            "$(PlatformVersion) evaluated empty — the root Directory.Build.props no longer sets the "
            + "one maintained number, so every derived version is an SDK default.");

        Assert.True(Line.IsMatch(platformVersion),
            $"PlatformVersion is '{platformVersion}', which is not a <major>.<minor>.0 line. The "
            + "patch of every build is main-cd's run number (policy platform-semver-versioning), so "
            + "the property carries no label and no patch of its own. "
            + "See Doc/Architecture/PlatformVersioning.");
    }

    /// <summary>
    /// The composed <c>$(Version)</c> on each of the three ways a build can be invoked, and that
    /// none of them carries a channel word.
    /// </summary>
    [Fact]
    public void TheComposedVersionHasTheShapeOfItsBuild()
    {
        var line = Evaluate()["PlatformVersion"];
        var majorMinor = line[..line.LastIndexOf('.')];

        var main = Evaluate("-p:CIRun=true", "-p:PlatformBuildNumber=10340")["Version"];
        Assert.True(Build.IsMatch(main), $"a main CD build composed '{main}', not <major>.<minor>.<run>.");
        Assert.Equal($"{majorMinor}.10340", main);

        var released = Evaluate("-p:PublicRelease=true")["Version"];
        Assert.Equal(line, released);

        var local = Evaluate()["Version"];
        Assert.True(Source.IsMatch(local), $"a local build composed '{local}', not <major>.<minor>.0-dev.");

        var pullRequest = Evaluate("-p:CIRun=true")["Version"];
        Assert.True(Source.IsMatch(pullRequest),
            $"a CIRun build WITHOUT -p:PlatformBuildNumber composed '{pullRequest}'. Only main-cd "
            + "passes a build number; every other workflow (and its own GITHUB_RUN_NUMBER) must "
            + "compose the -dev shape, never a clean release version from the wrong counter.");

        foreach (var v in new[] { main, released, local, pullRequest })
            Assert.DoesNotContain("ci", v, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 🚨 A build number that is not a positive integer — empty, zero, text — never becomes a
    /// release version; it falls to the <c>-dev</c> shape, which <c>main-cd.yml</c>'s shape
    /// assertion then refuses to publish.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("01")]
    public void AnInvalidBuildNumberNeverMintsAReleaseVersion(string buildNumber)
    {
        var composed = Evaluate("-p:CIRun=true", $"-p:PlatformBuildNumber={buildNumber}")["Version"];
        Assert.True(Source.IsMatch(composed), $"PlatformBuildNumber={buildNumber} composed '{composed}'.");
    }

    /// <summary>
    /// 🚨 The binding identity stays <c>&lt;major&gt;.0.0.0</c> across a minor bump: module bytes
    /// sealed on a 3.1 build are adopted by a lagging 3.0 portal (same compatibility key), and the
    /// default load context refuses a reference to a HIGHER assembly version than it carries.
    /// </summary>
    [Fact]
    public void TheAssemblyVersionIsTheMajorAlone()
    {
        var probed = Evaluate("-p:CIRun=true", "-p:PlatformBuildNumber=10340");
        var major = probed["PlatformVersion"].Split('.')[0];
        Assert.Equal($"{major}.0.0.0", probed["AssemblyVersion"]);
    }

    /// <summary>
    /// 🚨 <b>The control arm — proof this guard can FAIL.</b> Reintroduce a labelled line through the
    /// real evaluator and require the composition to be REJECTED, so the assertions above are not
    /// passing in a world where the regexes are wrong or the probe returned a constant.
    /// </summary>
    [Fact]
    public void ReintroducingALabelLeavesTheScheme_MeasuredThroughRealMSBuild()
    {
        const string RetiredLine = "3.0.0-rc8";

        var probed = Evaluate($"-p:PlatformVersion={RetiredLine}");
        Assert.Equal(RetiredLine, probed["PlatformVersion"]);
        Assert.False(Line.IsMatch(probed["PlatformVersion"]),
            $"the line predicate accepted '{RetiredLine}', so {nameof(TheMaintainedPlatformVersionIsALine)} cannot fail.");

        var nonZeroPatch = Evaluate("-p:PlatformVersion=3.1.7");
        Assert.False(Line.IsMatch(nonZeroPatch["PlatformVersion"]),
            "the line predicate accepted a non-zero patch, so a line could collide with a real build.");
    }

    /// <summary>
    /// The predicates themselves, against every shape the platform has minted or been asked to mint.
    /// Pure — no MSBuild — so the retired shapes are written down where widening a pattern fails.
    /// </summary>
    [Theory]
    [InlineData("3.1.10340", true, false)]
    [InlineData("4.0.1", true, false)]
    [InlineData("3.1.0-dev", false, true)]
    [InlineData("3.1.0", false, false)]
    // Retired notations: read by consumers, never minted.
    [InlineData("3.0.0-ci.10330", false, false)]
    [InlineData("3.0.0-ci.0", false, false)]
    [InlineData("3.0.0-rc9.ci.7818", false, false)]
    [InlineData("3.0.0-rc8", false, false)]
    // Near-misses.
    [InlineData("3.1.010", false, false)]
    [InlineData("3.1.10340-dev", false, false)]
    [InlineData("3.1.0-DEV", false, false)]
    [InlineData("3.1", false, false)]
    [InlineData("3.1.0.10340", false, false)]
    public void ThePredicatesAdmitTheSchemeAndNothingElse(string version, bool isBuild, bool isSource)
    {
        Assert.Equal(isBuild, Build.IsMatch(version));
        Assert.Equal(isSource, Source.IsMatch(version));
    }

    private static IReadOnlyDictionary<string, string> Evaluate(params string[] extraArguments)
        => MsBuildPropertyProbe.Evaluate(ProbeProject, Probed, extraArguments);
}
