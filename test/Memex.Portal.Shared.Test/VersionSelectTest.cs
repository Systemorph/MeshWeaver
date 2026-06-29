using System.Text.Json;
using System.Text.Json.Serialization;
using Memex.Portal.Shared.SelfUpdate;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the self-update DECISION logic: which registry tag each policy rolls to, and when a target is
/// "newer" than the running version. This is the safety core of the feature (a wrong pick auto-rolls
/// the whole platform), so the truth table is fixed here. Also covers the policy-node content parse.
/// </summary>
public class VersionSelectTest
{
    // 3.1.0-ci.5 is the newest overall (base 3.1.0 beats 3.0.0); the only CLEAN release is 3.0.0.
    private static readonly string[] Tags =
        ["3.0.0", "3.0.0-ci.40", "3.0.0-ci.51", "3.1.0-ci.5", "garbage", "latest", "main"];

    [Fact]
    public void Continuous_PicksNewestTag_IncludingBuildNumbered()
        => Assert.Equal("3.1.0-ci.5", VersionSelect.PickTarget(Tags, UpdatePolicyKind.Continuous));

    [Fact]
    public void Stable_PicksNewestCleanRelease_IgnoringBuildNumbered()
        => Assert.Equal("3.0.0", VersionSelect.PickTarget(Tags, UpdatePolicyKind.Stable));

    [Fact]
    public void None_PicksNothing()
        => Assert.Null(VersionSelect.PickTarget(Tags, UpdatePolicyKind.None));

    [Fact]
    public void PickTarget_NoParseableTags_ReturnsNull()
        => Assert.Null(VersionSelect.PickTarget(["latest", "main", "garbage"], UpdatePolicyKind.Continuous));

    // CI-green gate: a `-edge.N` build is UNVERIFIED. Default (RequireCiGreen=true) must skip it even
    // though it is the newest; opting into "any" (RequireCiGreen=false) rolls to it.
    [Fact]
    public void GreenOnly_Default_ExcludesEdgeBuilds_EvenWhenNewest()
        => Assert.Equal("3.0.0-ci.51",
            VersionSelect.PickTarget(["3.0.0-ci.51", "3.1.0-edge.7"], UpdatePolicyKind.Continuous));

    [Fact]
    public void Any_RequireCiGreenFalse_IncludesEdgeBuilds()
        => Assert.Equal("3.1.0-edge.7",
            VersionSelect.PickTarget(["3.0.0-ci.51", "3.1.0-edge.7"], UpdatePolicyKind.Continuous, requireCiGreen: false));

    [Fact]
    public void UpdatePolicyContent_DefaultsToRequireCiGreen()
        => Assert.True(new UpdatePolicyContent().RequireCiGreen, "green-only must be the safe default");

    [Theory]
    [InlineData("3.1.0-ci.5", "3.0.0-ci.51", true)]   // higher base wins
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
    public void ParseContent_Null_DefaultsToContinuous()
        => Assert.Equal(UpdatePolicyKind.Continuous, UpdatePolicyNodeType.ParseContent(null, Web).Policy);

    [Fact]
    public void ParseContent_JsonElement_DeserializesEnumByName()
    {
        var element = JsonSerializer.SerializeToElement(
            new UpdatePolicyContent { Policy = UpdatePolicyKind.None }, Web);
        Assert.Equal(UpdatePolicyKind.None, UpdatePolicyNodeType.ParseContent(element, Web).Policy);
    }

    private static readonly JsonSerializerOptions Web =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}
