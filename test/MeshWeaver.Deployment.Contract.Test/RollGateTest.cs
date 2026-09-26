using System.Text.Json.Nodes;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// The control instance's record declares a ROLL GATE (Systemorph/Memex
/// <c>docs/control-instance.md</c> §4): <c>{ "after": ["memex-cloud", "memex"], "soakMinutes": 120,
/// "approval": "required" }</c>. The contract's JSON options SKIP unmapped members on purpose, so
/// until the field was typed here the gate would have been dropped silently at the first read — the
/// control plane would have seen an ungated record and rolled control unattended.
///
/// <para>These pin both directions: a gated record keeps its gate through the contract, and every
/// record written before the field existed reads exactly as it did (a null gate).</para>
/// </summary>
public class RollGateTest
{
    private const string Gated = """
        {
          "$type": "DeploymentContent",
          "host": "control.systemorph.com",
          "updatePolicy": "Continuous",
          "updatePattern": "3.0.0-ci*",
          "rollGate": { "after": ["memex-cloud", "memex"], "soakMinutes": 120, "approval": "required" }
        }
        """;

    private const string Ungated = """
        {
          "$type": "DeploymentContent",
          "host": "memex.systemorph.com",
          "updatePolicy": "Continuous",
          "updatePattern": "3.0.0-ci*"
        }
        """;

    [Fact]
    public void AGatedRecordKeepsItsGateThroughTheContract()
    {
        var record = DeploymentRecordJson.Read(Gated)!;
        Assert.NotNull(record.RollGate);
        Assert.Equal(new[] { "memex-cloud", "memex" }, record.RollGate!.After);
        Assert.Equal(120, record.RollGate.SoakMinutes);
        Assert.Equal("required", record.RollGate.Approval);

        var written = JsonNode.Parse(DeploymentRecordJson.Write(record))!.AsObject();
        var gate = written["rollGate"]!.AsObject();
        Assert.Equal(120, (int)gate["soakMinutes"]!);
        Assert.Equal("required", (string?)gate["approval"]);
        Assert.Equal(2, gate["after"]!.AsArray().Count);
    }

    [Fact]
    public void ARecordWithoutAGateReadsExactlyAsBefore()
    {
        var record = DeploymentRecordJson.Read(Ungated)!;
        Assert.Null(record.RollGate);
        Assert.Equal("Continuous", record.UpdatePolicy);
        Assert.Equal("3.0.0-ci*", record.UpdatePattern);
        // Writing it back invents no gate.
        Assert.DoesNotContain("rollGate", DeploymentRecordJson.Write(record));
    }

    [Fact]
    public void TheGateNamesNoTagAndRendersNoPortalConfiguration()
    {
        var gated = new DeploymentContent()
            .WithHost("control.systemorph.com", "systemorph.com")
            .WithUpdatePolicy("Continuous")
            .WithUpdatePattern("3.0.0-ci*")
            .WithRollGate(["memex-cloud", " memex ", ""], 120);

        Assert.Equal(new[] { "memex-cloud", "memex" }, gated.RollGate!.After);
        Assert.Equal("required", gated.RollGate.Approval);
        // Not a pin: the pattern and the (absent) pinned tag are exactly what they were.
        Assert.Equal("3.0.0-ci*", gated.UpdatePattern);
        Assert.Null(gated.PinnedImageTag);

        // Rendered nowhere — the control instance reads it ABOUT this deployment.
        var ungated = gated with { RollGate = null };
        foreach (var options in new[] { PortalConfigOptions.Helm, PortalConfigOptions.Aspire("http://localhost:8080") })
            Assert.Equal(
                DeploymentPortalConfig.PortalConfig(ungated, options).OrderBy(p => p.Key),
                DeploymentPortalConfig.PortalConfig(gated, options).OrderBy(p => p.Key));
    }
}
