using System.Text.Json.Nodes;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// A client estate gets its OWN GitHub App, and the CONTROL instance dispatches that client's
/// pipelines as it (the maintainer's decision, 2026-09-15). The record says WHICH App
/// (<see cref="DeploymentContent.OpsGitHubApp"/>); the PEM stays in Key Vault and the record names
/// only the object and the configuration key the control instance reads it from.
///
/// <para>The property these tests hold: that block is read BY a control instance ABOUT another
/// deployment, so it must never leak into the described deployment's own portal configuration —
/// a client portal handed an ops client id would advertise, in its own ConfigMap, the App that can
/// start its infrastructure pipelines.</para>
/// </summary>
public class OpsGitHubAppTest
{
    private static DeploymentContent Client() =>
        new DeploymentContent()
            .WithHost("client.example.com", "example.com")
            .WithNamespace("client")
            .WithKeyVault("Systemorph", "client-")
            .WithGitHubApp("portal-client-id", "111", "Systemorph")
            .WithOpsGitHubApp("ops-client-id", "222", "Systemorph");

    [Fact]
    public void TheOpsAppIsARecordFieldAndNotPortalConfiguration()
    {
        var helm = DeploymentPortalConfig.PortalConfig(Client(), PortalConfigOptions.Helm);
        var aspire = DeploymentPortalConfig.PortalConfig(Client(), PortalConfigOptions.Aspire("http://localhost:8080"));

        // The portal's OWN identity is configuration — that is what GitHub__App__* is for.
        Assert.Equal("portal-client-id", helm["GitHub__App__ClientId"]);
        Assert.Equal("111", helm["GitHub__App__InstallationId"]);

        // The ops identity is NOT: no key of either renderer carries it, under any name.
        foreach (var rendered in new[] { helm, aspire })
        {
            Assert.DoesNotContain(rendered, pair => pair.Value == "ops-client-id");
            Assert.DoesNotContain(rendered, pair => pair.Key.Contains("Apps__", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheOpsAppSurvivesTheContractWithItsPemPointers()
    {
        var record = Client()
            .WithOpsGitHubApp(
                "ops-client-id",
                privateKeySecret: "client-GitHub-App-PrivateKey",
                privateKeyConfigKey: "GitHub__Apps__client__PrivateKey");

        var written = JsonNode.Parse(DeploymentRecordJson.Write(record))!.AsObject();
        var ops = written["opsGitHubApp"]!.AsObject();
        Assert.Equal("ops-client-id", (string?)ops["clientId"]);
        Assert.Equal("222", (string?)ops["installationId"]);           // kept: the builder carries the block forward
        Assert.Equal("Systemorph", (string?)ops["installationOwner"]);
        Assert.Equal("client-GitHub-App-PrivateKey", (string?)ops["privateKeySecret"]);
        Assert.Equal("GitHub__Apps__client__PrivateKey", (string?)ops["privateKeyConfigKey"]);

        var round = DeploymentRecordJson.Read(DeploymentRecordJson.Write(record))!;
        Assert.Equal("ops-client-id", round.OpsGitHubApp?.ClientId);
        Assert.Equal("client-GitHub-App-PrivateKey", round.OpsGitHubApp?.PrivateKeySecret);
        Assert.Equal("GitHub__Apps__client__PrivateKey", round.OpsGitHubApp?.PrivateKeyConfigKey);
    }

    [Fact]
    public void ARecordThatNamesNoOpsAppLeavesTheDispatcherOnTheControlInstancesOwn()
    {
        // memex, memex-cloud and build are exactly this shape — and must stay working unchanged.
        var record = new DeploymentContent().WithGitHubApp("portal-client-id", "111", "Systemorph");
        Assert.Null(record.OpsGitHubApp);

        // Setting the portal's own App never sets the ops one, in either direction.
        var ops = new DeploymentContent().WithOpsGitHubApp("ops-client-id");
        Assert.Null(ops.GitHubApp);
        Assert.Equal("ops-client-id", ops.OpsGitHubApp?.ClientId);
    }

    [Fact]
    public void TheBuilderIsAPureTransform()
    {
        var bare = new DeploymentContent();
        var built = bare.WithOpsGitHubApp("ops-client-id", "222");

        Assert.Null(bare.OpsGitHubApp);
        Assert.Equal("222", built.OpsGitHubApp?.InstallationId);

        // A later call keeps what it does not restate — the record is the default, never the builder.
        var narrowed = built.WithOpsGitHubApp("ops-client-id", privateKeySecret: "client-GitHub-App-PrivateKey");
        Assert.Equal("222", narrowed.OpsGitHubApp?.InstallationId);
        Assert.Equal("client-GitHub-App-PrivateKey", narrowed.OpsGitHubApp?.PrivateKeySecret);
    }
}
