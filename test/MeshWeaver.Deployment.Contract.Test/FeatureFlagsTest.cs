using System.Text.Json.Nodes;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// Memex#376: memex-cloud's <c>fleetops</c> feature flag (off, excluding <c>Plugins/Hosting</c>) lived
/// only in an overlay's top-level <c>features:</c> block. The contract's JSON options skip unmapped
/// members, so a record could not carry it at all — and once every roll re-applies the record (policy
/// <c>record-change-applies-itself</c>) the flag would have been dropped and fleet ops re-enabled.
/// These pin that a record now keeps its flags through the contract, in the chart's own shape, and
/// that a record without any reads exactly as before.
/// </summary>
public class FeatureFlagsTest
{
    private const string Flagged = """
        {
          "$type": "DeploymentContent",
          "host": "memex.meshweaver.cloud",
          "features": {
            "fleetops": { "enabled": false, "packages": ["Plugins/Hosting"] },
            "betaChat": { "enabled": true, "description": "the new chat" }
          }
        }
        """;

    [Fact]
    public void ARecordKeepsItsFeatureFlagsThroughTheContract()
    {
        var record = DeploymentRecordJson.Read(Flagged)!;
        Assert.Equal(2, record.Features.Count);
        Assert.False(record.Features["fleetops"].Enabled);
        Assert.Equal(new[] { "Plugins/Hosting" }, record.Features["fleetops"].Packages);
        Assert.True(record.Features["betaChat"].Enabled);
        Assert.Equal("the new chat", record.Features["betaChat"].Description);

        var written = JsonNode.Parse(DeploymentRecordJson.Write(record))!.AsObject();
        var fleetops = written["features"]!["fleetops"]!.AsObject();
        Assert.False((bool)fleetops["enabled"]!);
        Assert.Equal("Plugins/Hosting", (string?)fleetops["packages"]![0]);
    }

    [Fact]
    public void ARecordWithoutFlagsReadsExactlyAsBefore()
    {
        var record = DeploymentRecordJson.Read("""{ "$type": "DeploymentContent", "host": "memex.systemorph.com" }""")!;
        Assert.Empty(record.Features);
    }
}
