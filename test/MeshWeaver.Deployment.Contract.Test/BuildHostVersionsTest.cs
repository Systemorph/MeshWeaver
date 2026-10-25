using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// Versioned build servers (Doc/Architecture/DeployTestSwitchLatest §3, §6): the <c>v/&lt;version&gt;</c> and
/// <c>alias/&lt;name&gt;</c> key forms of <see cref="DeploymentContent.BuildHostSets"/>, the set token's optional
/// fifth part (the core test image), alias resolution, and the retention rule that decides what a teardown may
/// remove. Pinned: a four-part token written before the fifth part existed reads back byte-identically; an alias
/// is a VERSION (one leaf), never a token; and retention keeps every alias target, every attempt's version and
/// every capped package's version, and nothing else.
/// </summary>
public class BuildHostVersionsTest
{
    private const string Tester = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string Portal = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string Runner = "sha256:3333333333333333333333333333333333333333333333333333333333333333";
    private const string Tests = "sha256:4444444444444444444444444444444444444444444444444444444444444444";

    private static string Token(string version, string? coreTests = null) =>
        new BuildHostChannel { PlatformVersion = version, TesterDigest = Tester, PortalDigest = Portal, RunnerDigest = Runner, CoreTestsDigest = coreTests }.ToSetToken();

    private static ImmutableSortedDictionary<string, string> Sets(params (string Key, string Value)[] entries) =>
        entries.ToImmutableSortedDictionary(e => e.Key, e => e.Value, System.StringComparer.Ordinal);

    [Fact]
    public void TheFifthPart_IsOptional_AndAFourPartTokenReadsBackUnchanged()
    {
        Assert.NotNull(typeof(BuildHostChannel).GetProperty(nameof(BuildHostChannel.CoreTestsDigest))!
            .GetCustomAttributes(typeof(System.ComponentModel.BrowsableAttribute), false).OfType<System.ComponentModel.BrowsableAttribute>().SingleOrDefault(b => !b.Browsable));
        var four = Token("3.1.10500");
        Assert.Equal($"3.1.10500;{Tester};{Portal};{Runner}", four);
        Assert.Equal(four, BuildHostChannel.FromSetToken(four)!.ToSetToken());
        Assert.Null(BuildHostChannel.FromSetToken(four)!.CoreTestsDigest);

        var five = Token("3.1.10500", Tests);
        Assert.Equal($"{four};{Tests}", five);
        var back = BuildHostChannel.FromSetToken(five)!;
        Assert.Equal(Tests, back.CoreTestsDigest);
        Assert.Equal(five, back.ToSetToken());
        Assert.True(back.IsComplete);

        Assert.Null(BuildHostChannel.FromSetToken($"{five};extra"));
        Assert.Null(BuildHostChannel.FromSetToken("3.1.10500;a;b"));
    }

    [Theory]
    [InlineData("3.1.10412", true)]
    [InlineData("3.0.0-ci.9898", true)]
    [InlineData("10.20.30", true)]
    [InlineData("3.1", false)]
    [InlineData("3.1.x", false)]
    [InlineData(" 3.1.1", false)]
    [InlineData("3.1.1-", false)]
    [InlineData("3.1.1-bad_char", false)]
    [InlineData("3.1.1-alpha.", false)]
    [InlineData("3.1.1-alpha-", false)]
    [InlineData("3.1.1-alpha.1", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AVersion_IsMajorMinorPatch_WithAnOptionalPrerelease(string? version, bool valid) =>
        Assert.Equal(valid, BuildHostSetKeys.IsVersion(version));

    [Fact]
    public void TheKeyForms_SeparateVersions_Aliases_AndTheOlderChannelKeys()
    {
        Assert.Equal("v/3.1.10412", BuildHostSetKeys.Version("3.1.10412"));
        Assert.Equal("alias/latest", BuildHostSetKeys.Alias(BuildHostSetKeys.LatestAlias));
        Assert.Equal("3.1.10412", BuildHostSetKeys.VersionOf("v/3.1.10412"));
        Assert.Null(BuildHostSetKeys.VersionOf("v/not-a-version"));
        Assert.Null(BuildHostSetKeys.VersionOf("latest"));
        Assert.Equal("candidate", BuildHostSetKeys.AliasName("alias/candidate"));
        Assert.Null(BuildHostSetKeys.AliasName("alias/"));
        Assert.Null(BuildHostSetKeys.AliasName("v/3.1.1"));
        Assert.Equal("build-3-1-10412", BuildHostSetKeys.DeploymentName("3.1.10412"));
        Assert.Equal("build-3-0-0-ci-9898", BuildHostSetKeys.DeploymentName("3.0.0-ci.9898"));
        // The name is not injective, so a second version with the same name is a COLLISION, never a second deployment.
        var sets = Sets(("v/3.1.1-alpha.1", Token("3.1.1-alpha.1")));
        Assert.Equal("3.1.1-alpha.1", BuildHostSetKeys.NameCollision(sets, "3.1.1-alpha-1"));
        Assert.Null(BuildHostSetKeys.NameCollision(sets, "3.1.1-alpha.1"));
        Assert.Null(BuildHostSetKeys.NameCollision(sets, "3.1.2"));
    }

    [Fact]
    public void AnAlias_NamesAVersion_AndResolvesThroughItsEntry()
    {
        var record = new DeploymentContent
        {
            Host = "control.example.com",
            BuildHostSets = Sets(
                ("v/3.1.10500", Token("3.1.10500")), ("v/3.1.10600", Token("3.1.10600", Tests)),
                ("alias/latest", "3.1.10600"), ("alias/previous", "3.1.10500"), ("alias/candidate", "3.1.10700"),
                ("latest", Token("3.1.10400"))),
        };
        Assert.Equal("3.1.10600", record.BuildHostAliasSet("latest")?.PlatformVersion);
        Assert.Equal(Tests, record.BuildHostAliasSet("latest")?.CoreTestsDigest);
        Assert.Equal("3.1.10500", record.BuildHostAliasSet("previous")?.PlatformVersion);
        Assert.Null(record.BuildHostAliasSet("candidate"));
        Assert.Null(record.BuildHostAliasSet("stable"));
        // The older channel key still reads as before; the versioned entries do not change it.
        Assert.Equal("3.1.10400", record.BuildHostSet("latest")?.PlatformVersion);
        Assert.Equal(new[] { "3.1.10500", "3.1.10600" }, BuildHostSetKeys.Versions(record.BuildHostSets).Keys);
    }

    [Fact]
    public void Retention_KeepsAliasTargets_Attempts_AndCaps_AndTearsDownTheRest()
    {
        var sets = Sets(
            ("v/3.1.10100", Token("3.1.10100")), ("v/3.1.10200", Token("3.1.10200")), ("v/3.1.10300", Token("3.1.10300")),
            ("v/3.1.10400", Token("3.1.10400")), ("v/3.1.10500", Token("3.1.10500")), ("v/3.1.10600", Token("3.1.10600")),
            ("alias/latest", "3.1.10500"), ("alias/previous", "3.1.10400"), ("alias/candidate", "3.1.10600"));
        var retention = new BuildHostRetention();
        var keep = retention.Keep(sets, attemptVersions: new[] { "3.1.10200" }, capVersions: new[] { "3.1.10100" });
        Assert.Equal(new[] { "3.1.10100", "3.1.10200", "3.1.10400", "3.1.10500", "3.1.10600" }, keep);
        Assert.Equal(new[] { "3.1.10300" }, retention.Teardown(sets, new[] { "3.1.10200" }, new[] { "3.1.10100" }));
        // Without the previous-latest rule its version is no longer held.
        Assert.Contains("3.1.10400", new BuildHostRetention { KeepPreviousLatest = false }.Teardown(sets, [], []));
        // An attempt or cap naming a malformed version keeps nothing extra; nothing in an empty record is torn down.
        Assert.Equal(new[] { "3.1.10400", "3.1.10500", "3.1.10600" }, retention.Keep(sets, new[] { "garbage" }, []));
        Assert.Empty(retention.Teardown(null, [], []));
        Assert.Equal(BuildHostRetention.DefaultMaxVersions, retention.MaxVersions);
    }
}
