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
