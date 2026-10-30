using System.Linq;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// The operations record's <c>buildHosts</c> block (Doc/Architecture/BuildHosts, gap G7): the
/// standing build host per release channel, its platform set by DIGEST and its replica ceiling.
/// Pinned here: the block round-trips the record's JSON with every field and both channels; an
/// absent block reads as NONE (null — never an enabled default); a channel's ceiling defaults to
/// two and an explicit value survives; the computed <c>isComplete</c> is a derivation the contract
/// never writes; and a channel names a whole set only with a version and three sha256 digests
/// (tester, portal, runner).
/// </summary>
public class BuildHostsRecordTest
{
    private const string Tester = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string Portal = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string Runner = "sha256:3333333333333333333333333333333333333333333333333333333333333333";

    private const string Json = """
        {
          "$type": "DeploymentContent",
          "host": "control.example.com",
          "buildHosts": {
            "enabled": true,
            "channels": {
              "latest": { "platformVersion": "3.1.10412",
                          "testerDigest": "sha256:1111111111111111111111111111111111111111111111111111111111111111",
                          "portalDigest": "sha256:2222222222222222222222222222222222222222222222222222222222222222",
                          "runnerDigest": "sha256:3333333333333333333333333333333333333333333333333333333333333333",
                          "maxReplicas": 3 },
              "stable": { "platformVersion": "3.1.10388" }
            }
          }
        }
        """;

    [Fact]
    public void TheRecordsJson_CarriesBothChannels_AndWritesThemBack()
    {
        var record = DeploymentRecordJson.Read(Json)!;
        var hosts = record.BuildHosts!;

        Assert.True(hosts.Enabled);
        Assert.Equal(["latest", "stable"], hosts.Channels.Keys);
        var latest = hosts.Channel(BuildHostsBlock.LatestChannel)!;
        Assert.Equal(("3.1.10412", Tester, Portal, Runner, 3), (latest.PlatformVersion, latest.TesterDigest, latest.PortalDigest, latest.RunnerDigest, latest.MaxReplicas));
        Assert.True(latest.IsComplete);
        var stable = hosts.Channel(BuildHostsBlock.StableChannel)!;
        Assert.Equal(BuildHostChannel.DefaultMaxReplicas, stable.MaxReplicas);
        Assert.Null(stable.TesterDigest);

        var json = DeploymentRecordJson.Write(record);
        var reread = DeploymentRecordJson.Read(json)!;
        Assert.True(reread.BuildHosts!.Enabled);
        Assert.Equal(hosts.Channels.Keys, reread.BuildHosts.Channels.Keys);
        foreach (var (name, channel) in hosts.Channels)
            Assert.Equal(channel, reread.BuildHosts.Channels[name]);

        // camelCase on the wire, and the derivation is never written.
        var written = JsonNode.Parse(json)!["buildHosts"]!;
        Assert.Equal(3, (int)written["channels"]!["latest"]!["maxReplicas"]!);
        Assert.DoesNotContain("isComplete", json);
        Assert.DoesNotContain("IsComplete", json);
    }

    [Fact]
    public void AChannelsFloor_IsDeclared_DefaultsToZero_AndRoundTrips()
    {
        // Declared, never derived (Doc/Architecture/BuildHosts → "Declared host floor"): an unstated floor scales to zero.
        var record = DeploymentRecordJson.Read(Json)!;
        Assert.Equal(0, record.BuildHosts!.Channel(BuildHostsBlock.LatestChannel)!.MinReplicas);

        var floored = DeploymentRecordJson.Read(Json.Replace("\"maxReplicas\": 3", "\"maxReplicas\": 3, \"minReplicas\": 1"))!;
        Assert.Equal((1, 3), (floored.BuildHosts!.Channels["latest"].MinReplicas, floored.BuildHosts.Channels["latest"].MaxReplicas));
        var json = DeploymentRecordJson.Write(floored);
        Assert.Equal(1, (int)JsonNode.Parse(json)!["buildHosts"]!["channels"]!["latest"]!["minReplicas"]!);
        Assert.Equal(1, DeploymentRecordJson.Read(json)!.BuildHosts!.Channels["latest"].MinReplicas);
        Assert.Equal(0, floored.BuildHosts.Channels["stable"].MinReplicas);
    }

    [Fact]
    public void ARecordThatSaysNothing_RunsNoHosts()
    {
        var record = DeploymentRecordJson.Read("""{ "$type": "DeploymentContent", "host": "portal.example.com" }""")!;
        Assert.Null(record.BuildHosts);
        Assert.DoesNotContain("buildHosts", DeploymentRecordJson.Write(record));

        var empty = DeploymentRecordJson.Read("""{ "$type": "DeploymentContent", "buildHosts": {} }""")!;
        Assert.False(empty.BuildHosts!.Enabled);
        Assert.Empty(empty.BuildHosts.Channels);
    }

    [Fact]
    public void ThePullIdentity_RoundTrips_AndIsAbsentUnlessDeclared()
    {
        const string ClientId = "00000000-0000-0000-0000-000000000001";
        var record = DeploymentRecordJson.Read($$"""{ "$type": "DeploymentContent", "buildHosts": { "enabled": true, "pullIdentityClientId": "{{ClientId}}" } }""")!;
        Assert.Equal(ClientId, record.BuildHosts!.PullIdentityClientId);
        var json = DeploymentRecordJson.Write(record);
        Assert.Equal(ClientId, (string?)JsonNode.Parse(json)!["buildHosts"]!["pullIdentityClientId"]);
        Assert.Equal(ClientId, DeploymentRecordJson.Read(json)!.BuildHosts!.PullIdentityClientId);

        // Undeclared → no credential, and nothing written for it.
        var none = DeploymentRecordJson.Read("""{ "$type": "DeploymentContent", "buildHosts": { "enabled": true } }""")!;
        Assert.Null(none.BuildHosts!.PullIdentityClientId);
        Assert.DoesNotContain("pullIdentityClientId", DeploymentRecordJson.Write(none));
    }

    [Fact]
    public void AChannelChangedWithWith_KeepsTheOtherChannelAndTheSwitch()
    {
        // The receiver's shape: write ONE channel's set, keep everything else.
        var record = DeploymentRecordJson.Read(Json)!;
        var moved = record with
        {
            BuildHosts = record.BuildHosts! with
            {
                Channels = record.BuildHosts.Channels.SetItem(BuildHostsBlock.LatestChannel,
                    record.BuildHosts.Channels[BuildHostsBlock.LatestChannel] with { PlatformVersion = "3.1.10500" }),
            },
        };
        var reread = DeploymentRecordJson.Read(DeploymentRecordJson.Write(moved))!;
        Assert.True(reread.BuildHosts!.Enabled);
        Assert.Equal("3.1.10500", reread.BuildHosts.Channels["latest"].PlatformVersion);
        Assert.Equal(3, reread.BuildHosts.Channels["latest"].MaxReplicas);
        Assert.Equal("3.1.10388", reread.BuildHosts.Channels["stable"].PlatformVersion);
    }

    // The AUTHORED block states which channels run (and their ceilings); the SET each runs is the
    // record's mesh-owned BuildHostSets, written by CD.
    private static DeploymentContent WithHosts(bool enabled, string? latest, string? stable) => new()
    {
        Host = "control.example.com",
        BuildHosts = new BuildHostsBlock
        {
            Enabled = enabled,
            Channels = ImmutableSortedDictionary<string, BuildHostChannel>.Empty
                .Add(BuildHostsBlock.LatestChannel, new BuildHostChannel())
                .Add(BuildHostsBlock.StableChannel, new BuildHostChannel()),
        },
        BuildHostSets = ImmutableSortedDictionary<string, string>.Empty
            .Add(BuildHostsBlock.LatestChannel, new BuildHostChannel { PlatformVersion = latest, TesterDigest = Tester, PortalDigest = Portal, RunnerDigest = Runner }.ToSetToken())
            .Add(BuildHostsBlock.StableChannel, new BuildHostChannel { PlatformVersion = stable }.ToSetToken()),
    };

    [Fact]
    public void TheCdOwnedSets_AreKeptOutOfGeneratedEditors()
    {
        var property = typeof(DeploymentContent).GetProperty(nameof(DeploymentContent.BuildHostSets))!;
        Assert.False(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.ComponentModel.BrowsableAttribute>(property)?.Browsable ?? true,
            "a CD-written member is never offered for hand-editing, and so shows no untranslated label");
    }

    [Fact]
    public void ASet_IsOneToken_ThatRoundTrips_AndRefusesAnythingElse()
    {
        var set = new BuildHostChannel { PlatformVersion = "3.1.10500", TesterDigest = Tester, PortalDigest = Portal, RunnerDigest = Runner };
        var token = set.ToSetToken();
        Assert.Equal($"3.1.10500;{Tester};{Portal};{Runner}", token);
        var back = BuildHostChannel.FromSetToken(token)!;
        Assert.Equal(set.PlatformVersion, back.PlatformVersion);
        Assert.Equal(set.RunnerDigest, back.RunnerDigest);
        Assert.True(back.IsComplete);
        Assert.Null(BuildHostChannel.FromSetToken(new BuildHostChannel { PlatformVersion = "3.1.10500", TesterDigest = Tester }.ToSetToken())!.RunnerDigest);
        Assert.Null(BuildHostChannel.FromSetToken("3.1.10500;a;b"));
        Assert.Null(BuildHostChannel.FromSetToken(null));
        // The record's JSON carries each set as ONE string — the unit a three-way merge decides whole.
        var record = new DeploymentContent { Host = "h", BuildHostSets = ImmutableSortedDictionary<string, string>.Empty.Add("latest", token) };
        var json = System.Text.Json.JsonSerializer.Serialize(record, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Contains($"\"latest\":\"{token}\"", json);
        Assert.Equal("3.1.10500", record.BuildHostSet("latest")?.PlatformVersion);
    }

    [Fact]
    public void TheBarrier_ReadsTheMeshOwnedSet_NeverAnAuthoredChannelsSetFields()
    {
        var authoredOnly = new DeploymentContent
        {
            Host = "control.example.com",
            BuildHosts = new BuildHostsBlock
            {
                Enabled = true,
                Channels = ImmutableSortedDictionary<string, BuildHostChannel>.Empty
                    .Add(BuildHostsBlock.LatestChannel, new BuildHostChannel { PlatformVersion = "3.1.10412" }),
            },
        };
        Assert.Empty(BarrierKeys(DeploymentPortalConfig.PortalConfig(authoredOnly)));
        var setForAnUnrunChannel = authoredOnly with
        {
            BuildHostSets = ImmutableSortedDictionary<string, string>.Empty
                .Add(BuildHostsBlock.StableChannel, new BuildHostChannel { PlatformVersion = "3.1.10388" }.ToSetToken()),
        };
        Assert.Empty(BarrierKeys(DeploymentPortalConfig.PortalConfig(setForAnUnrunChannel)));
        Assert.Equal("3.1.10388", setForAnUnrunChannel.BuildHostSet("stable")?.PlatformVersion);
        Assert.Null(authoredOnly.BuildHostSet("latest"));
    }

    private static string[] BarrierKeys(IDictionary<string, string> config) =>
        config.Keys.Where(k => k.StartsWith("Hosting__Builds__Channels__", StringComparison.Ordinal)).ToArray();

    [Fact]
    public void AnEnabledBlock_RendersTheClaimBarrierForEveryChannelWithAVersion()
    {
        foreach (var options in new[] { PortalConfigOptions.Helm, PortalConfigOptions.Aspire("http://localhost:8080") })
        {
            var config = DeploymentPortalConfig.PortalConfig(WithHosts(true, " 3.1.10412 ", "3.1.10388"), options);
            Assert.Equal("3.1.10412", config["Hosting__Builds__Channels__latest__PlatformVersion"]);
            Assert.Equal("3.1.10388", config["Hosting__Builds__Channels__stable__PlatformVersion"]);
            Assert.Equal(2, BarrierKeys(config).Length);
            Assert.Equal("Hosting__Builds__Channels__stable__PlatformVersion", DeploymentPortalConfig.BuildHostPlatformVersionKey("stable"));
        }
    }

    [Fact]
    public void ADisabledBlock_RendersNoBarrier()
    {
        Assert.Empty(BarrierKeys(DeploymentPortalConfig.PortalConfig(WithHosts(false, "3.1.10412", "3.1.10388"))));
    }

    [Fact]
    public void ABlankVersion_RendersNoBarrierForThatChannel()
    {
        var config = DeploymentPortalConfig.PortalConfig(WithHosts(true, "3.1.10412", "  "));
        Assert.Equal(["Hosting__Builds__Channels__latest__PlatformVersion"], BarrierKeys(config));
        Assert.Empty(BarrierKeys(DeploymentPortalConfig.PortalConfig(WithHosts(true, null, null))));
    }

    [Fact]
    public void ARecordWithoutTheBlock_RendersNothingNew()
    {
        Assert.Empty(BarrierKeys(DeploymentPortalConfig.PortalConfig(new DeploymentContent { Host = "portal.example.com" })));
    }

    [Theory]
    [InlineData("3.1.10412", Tester, Portal, Runner, true)]
    [InlineData(null, Tester, Portal, Runner, false)]
    [InlineData("3.1.10412", "3.1.10412", Portal, Runner, false)]                                                   // a tag is not a digest
    [InlineData("3.1.10412", "sha256:ABCDEF1111111111111111111111111111111111111111111111111111111111", Portal, Runner, false)] // upper case
    [InlineData("3.1.10412", Tester, "sha256:22", Runner, false)]                                                    // short
    [InlineData("3.1.10412", Tester, Portal, null, false)]                                                           // no runner
    public void AChannelNamesAWholeSet_OnlyWithAVersionAndThreeDigests(string? version, string? tester, string? portal, string? runner, bool complete)
    {
        var channel = new BuildHostChannel { PlatformVersion = version, TesterDigest = tester, PortalDigest = portal, RunnerDigest = runner };
        Assert.Equal(complete, channel.IsComplete);
    }

    [Fact]
    public void TheBlockIsInitOnly_NeverAPositionalConstructor()
    {
        // The binary-break rule: a positional parameter replaces the record's signature.
        foreach (var t in new[] { typeof(BuildHostsBlock), typeof(BuildHostChannel) })
            Assert.Contains(t.GetConstructors(), c => c.GetParameters().Length == 0);
        Assert.Equal(ImmutableSortedDictionary<string, BuildHostChannel>.Empty.Count, new BuildHostsBlock().Channels.Count);
    }
}
