using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// An inline env row can DECLARE its removal although its ConfigMap twin carries a different value
/// (<see cref="InlineEnvOverride.RemoveDespiteDifference"/>, with the twin's value in
/// <see cref="InlineEnvOverride.ShadowedValue"/>). The Hosting plugin's Reconcile reads both from the
/// record, so they must survive the record's own JSON reader and writer, and a row that does not state
/// them must read as "not declared" — never as a removal.
/// </summary>
public class InlineEnvRemoveDespiteDifferenceRecordTest
{
    private const string RecordJson = """
        {
          "$type": "DeploymentContent",
          "host": "portal.example.com",
          "inlineEnv": [
            {
              "$type": "InlineEnvOverride",
              "key": "PluginCatalog__Sources__1__RepoPath",
              "value": "https://github.com/acme/legacy",
              "shadows": "memex-portal-config",
              "agreesWithShadowed": false,
              "retiredBy": "acme/config#1",
              "reason": "legacy repository name; the ConfigMap carries the canonical one",
              "removeDespiteDifference": true,
              "shadowedValue": "https://github.com/acme/canonical"
            },
            {
              "$type": "InlineEnvOverride",
              "key": "PreWarm__GateReadiness",
              "value": "false",
              "shadows": "memex-portal-config",
              "agreesWithShadowed": false
            }
          ]
        }
        """;

    [Fact]
    public void TheRecordsJson_CarriesTheDeclaration_AndWritesItBack()
    {
        var record = DeploymentRecordJson.Read(RecordJson)!;

        var declared = record.InlineEnv[0];
        Assert.True(declared.RemoveDespiteDifference);
        Assert.Equal("https://github.com/acme/canonical", declared.ShadowedValue);

        var written = DeploymentRecordJson.Write(record);
        // The wire NAMES are what the record files and the control instance's reader depend on, so they are
        // asserted on the parsed JSON (independent of the writer's formatting), not on the text.
        var row = System.Text.Json.Nodes.JsonNode.Parse(written)!["inlineEnv"]![0]!;
        Assert.True(row["removeDespiteDifference"]!.GetValue<bool>());
        Assert.Equal("https://github.com/acme/canonical", row["shadowedValue"]!.GetValue<string>());

        var reread = DeploymentRecordJson.Read(written)!;
        Assert.True(reread.InlineEnv[0].RemoveDespiteDifference);
        Assert.Equal("https://github.com/acme/canonical", reread.InlineEnv[0].ShadowedValue);
    }

    [Fact]
    public void ARowThatDoesNotStateIt_IsNotDeclared()
    {
        // Negative control: the same record's second row says nothing about a removal.
        var record = DeploymentRecordJson.Read(RecordJson)!;

        var undeclared = record.InlineEnv[1];
        Assert.False(undeclared.RemoveDespiteDifference);
        Assert.Null(undeclared.ShadowedValue);
        Assert.Null(undeclared.RetiredBy);
    }
}
