using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>The version the minter composes has exactly the shapes of the SemVer notation, and this is
/// what makes that true rather than merely written down</b> — policy
/// <c>platform-semver-versioning</c> (<c>Doc/Architecture/PlatformVersioning</c>).
///
/// <list type="bullet">
/// <item><c>&lt;major&gt;.&lt;minor&gt;.&lt;run&gt;</c> — every continuous build under CI. NO
/// pre-release label; the patch is the CD run number, the same monotonic counter the retired
/// <c>-ci.&lt;run&gt;</c> suffix carried, so both notations share one lineage.</item>
/// <item><c>&lt;major&gt;.&lt;minor&gt;.0-ci.0</c> — a LOCAL source build (run ordinal 0: never ordered
/// against a publication).</item>
/// <item><c>X.Y.Z</c> — <c>-p:PublicRelease=true</c>, kept for local experiments only.</item>
/// </list>
///
/// <para><b>What a violation costs.</b> SemVer §11.4 compares pre-release identifiers as TEXT, so a
/// label on <c>PlatformVersion</c> made <c>3.0.0-ci.&lt;n&gt;</c> rank below <c>3.0.0-rc8</c> for
/// every n (#3554), and a mislabelled line outranked every later sealed set (#3542). The run number
/// is the one key the pipeline never gets wrong; this guard keeps it where every reader expects it.</para>
///
/// <para><b>Why MSBuild and not a text match on the props file.</b> The thing that must hold is the
/// COMPOSED string — what reaches an image tag, a package version and
/// <c>MESHWEAVER_PLATFORM_VERSION</c> — so this evaluates the real project through the real
/// evaluator, exactly as CD does (<c>-getProperty:Version -p:CIRun=true</c>), via
/// <see cref="MsBuildPropertyProbe"/>.</para>
///
/// <para>🚨 <b>MINTING one notation is not READING one.</b> This guard binds the MINTER only. Every
/// <c>3.0.0-ci.&lt;n&gt;</c> and <c>3.0.0-rc9.ci.&lt;n&gt;</c> image is still addressable, an install
/// can be running one, and every reader of a run number keeps accepting both notations —
/// <c>PlatformReleaseOrderTest</c> pins that side.</para>
/// </summary>
public class PlatformVersionSchemeGuard
{
    /// <summary>
    /// The project evaluated. Any project inheriting the root <c>Directory.Build.props</c> would do;
    /// this is the one <see cref="CompiledVersionAttributesIgnoreVersionOverrideGuard"/> already
    /// probes, so the two guards measure the same composition.
    /// </summary>
    private const string ProbeProject = "src/MeshWeaver.ShortGuid/MeshWeaver.ShortGuid.csproj";

    /// <summary>The run number the CI evaluation is handed, standing in for GITHUB_RUN_NUMBER.</summary>
    private const string RunNumber = "10050";

    /// <summary>The maintained line: three numeric parts and NOTHING else.</summary>
    private static readonly Regex CleanLine = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);

    /// <summary>A continuous build: <c>&lt;major&gt;.&lt;minor&gt;.&lt;run&gt;</c>, run never 0.</summary>
    private static readonly Regex ContinuousShape = new(@"^\d+\.\d+\.[1-9]\d*$", RegexOptions.Compiled);

    /// <summary>A local source build: <c>&lt;major&gt;.&lt;minor&gt;.0-ci.0</c>.</summary>
    private static readonly Regex LocalShape = new(@"^\d+\.\d+\.0-ci\.0$", RegexOptions.Compiled);

    private static readonly string[] Probed = ["PlatformVersion", "Version", "AssemblyVersion"];

    /// <summary>
    /// <c>PlatformVersion</c> — the one maintained number — carries no pre-release label, and its
    /// line is at or above the first line of the SemVer notation (3.1).
    /// </summary>
    [Fact]
    public void TheMaintainedPlatformVersionIsACleanLineOfTheSemVerNotation()
    {
        var platformVersion = Evaluate()["PlatformVersion"];

        Assert.False(string.IsNullOrWhiteSpace(platformVersion),
            "$(PlatformVersion) evaluated empty — the root Directory.Build.props no longer sets the "
            + "one maintained number, so every derived version is an SDK default.");
        Assert.True(CleanLine.IsMatch(platformVersion),
            $"PlatformVersion is '{platformVersion}', which carries a pre-release label. It is the clean "
            + "line, always (policy platform-semver-versioning; #3554).");

        var parts = platformVersion.Split('.');
        var major = int.Parse(parts[0]);
        var minor = int.Parse(parts[1]);
        Assert.True(major > 3 || (major == 3 && minor >= 1),
            $"PlatformVersion is '{platformVersion}', below line 3.1 — the readers "
            + "(PlatformReleaseOrder.SemVerEraStart) read a plain <major>.<minor>.<run> as a build only "
            + "from 3.1 on, so a 3.0.<run> image would land in the promotion band and never be selected.");
    }

    /// <summary>
    /// The composed <c>$(Version)</c> on each channel: CI mints <c>&lt;major&gt;.&lt;minor&gt;.&lt;run&gt;</c>
    /// with the run as the patch, a local build mints the ordinal-0 stamp, and the release channel is
    /// clean.
    /// </summary>
    [Fact]
    public void TheComposedVersionIsTheSemVerNotation_OnEveryChannel()
    {
        var ci = Evaluate("-p:CIRun=true", $"-p:GITHUB_RUN_NUMBER={RunNumber}");
        var local = Evaluate("-p:CIRun=false")["Version"];
        var released = Evaluate("-p:PublicRelease=true")["Version"];
        var line = ci["PlatformVersion"][..ci["PlatformVersion"].LastIndexOf('.')];

        Assert.Equal($"{line}.{RunNumber}", ci["Version"]);
        Assert.Matches(ContinuousShape, ci["Version"]);
        Assert.Equal($"{line}.0-ci.0", local);
        Assert.Matches(LocalShape, local);
        Assert.Matches(CleanLine, released);
    }

    /// <summary>
    /// 🚨 The runtime binding identity follows the MAJOR only: a deliberate minor bump must not move
    /// <c>AssemblyVersion</c>, or every compiled module and NodeType bound to the previous minor would
    /// reference an assembly version the platform no longer carries (policy
    /// <c>platform-backwards-compatibility</c>: same major is compatible).
    /// </summary>
    [Fact]
    public void TheBindingIdentityFollowsTheMajorOnly()
    {
        var probed = Evaluate("-p:PlatformVersion=3.7.0");
        Assert.Equal("3.7.0", probed["PlatformVersion"]);
        Assert.Equal("3.0.0.0", probed["AssemblyVersion"]);
        Assert.Equal("4.0.0.0", Evaluate("-p:PlatformVersion=4.2.0")["AssemblyVersion"]);
    }

    /// <summary>
    /// 🚨 <b>The control arm — proof the predicates can FAIL.</b> Through the real evaluator, a
    /// labelled line is rejected by the line predicate, and a run number of 0 under CI (what an
    /// off-Actions CI build without a run number would mint at midnight) is rejected by the continuous
    /// shape — so neither guard above passes in a world where its regex is wrong or the override never
    /// reached MSBuild.
    /// </summary>
    [Fact]
    public void TheShapesRejectWhatTheyMustReject_MeasuredThroughRealMSBuild()
    {
        const string RetiredLine = "3.1.0-rc8";
        var labelled = Evaluate($"-p:PlatformVersion={RetiredLine}");
        Assert.Equal(RetiredLine, labelled["PlatformVersion"]);
        Assert.DoesNotMatch(CleanLine, labelled["PlatformVersion"]);

        var zero = Evaluate("-p:CIRun=true", "-p:GITHUB_RUN_NUMBER=0")["Version"];
        Assert.DoesNotMatch(ContinuousShape, zero);
    }

    /// <summary>The predicates themselves, against every shape the platform has minted or been
    /// asked to mint.</summary>
    [Theory]
    [InlineData("3.1.10050", true, false)]
    [InlineData("4.0.1", true, false)]
    [InlineData("3.1.0-ci.0", false, true)]
    [InlineData("3.1.0", false, false)]
    [InlineData("3.0.0-ci.7989", false, false)]   // the retired notation — read, never minted again
    [InlineData("3.0.0-rc9.ci.7818", false, false)]
    [InlineData("3.1.10050-rc1", false, false)]
    [InlineData("3.1.10050-ci.1", false, false)]
    [InlineData("3.1.0-CI.0", false, false)]
    [InlineData("3.1-10050", false, false)]
    public void ThePredicatesAdmitTheNotationAndNothingElse(string version, bool continuous, bool local)
    {
        Assert.Equal(continuous, ContinuousShape.IsMatch(version));
        Assert.Equal(local, LocalShape.IsMatch(version));
    }

    private static IReadOnlyDictionary<string, string> Evaluate(params string[] extraArguments)
        => MsBuildPropertyProbe.Evaluate(ProbeProject, Probed, extraArguments);
}
