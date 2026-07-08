using Memex.Portal.Shared.Instances;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the pure logic behind the platform-admin Instances view: the Kubernetes-API JSON parse
/// (deployments + ingresses → instances), the version-tag extraction, the Grafana log-link builder,
/// and the guided create-instance plan generator. All deterministic — no cluster required.
/// </summary>
public class InstancesServiceTest
{
    private const string DeploymentsJson = """
    {
      "items": [
        {
          "metadata": { "name": "memex-portal-deployment", "namespace": "memex" },
          "spec": {
            "replicas": 2,
            "template": { "spec": { "containers": [
              { "name": "memex-portal", "image": "meshweaver.azurecr.io/memex-portal-ai:ci.612" }
            ] } }
          },
          "status": { "readyReplicas": 2 }
        },
        {
          "metadata": { "name": "memex-portal-deployment", "namespace": "atioz" },
          "spec": {
            "replicas": 1,
            "template": { "spec": { "containers": [
              { "name": "gate-node", "image": "meshweaver.azurecr.io/node-gate:ci.612" },
              { "name": "memex-portal", "image": "meshweaver.azurecr.io/memex-portal-ai:ci.610" }
            ] } }
          },
          "status": { "readyReplicas": 0 }
        }
      ]
    }
    """;

    private const string IngressesJson = """
    {
      "items": [
        { "metadata": { "namespace": "memex" }, "spec": { "rules": [ { "host": "memex.systemorph.com" } ] } },
        { "metadata": { "namespace": "atioz" }, "spec": { "rules": [ { "host": "portal.atioz.example" } ] } }
      ]
    }
    """;

    [Fact]
    public void ParseInstances_MapsNamespaceVersionHostAndReplicas_OrderedByNamespace()
    {
        var instances = KubernetesInstanceService.ParseInstances(DeploymentsJson, IngressesJson, new InstancesOptions());

        Assert.Equal(2, instances.Length);

        // Ordered by namespace (case-insensitive): atioz before memex.
        var atioz = instances[0];
        Assert.Equal("atioz", atioz.Namespace);
        Assert.Equal("portal.atioz.example", atioz.Domain);
        // Picks the memex-portal container's image, NOT the gate container's.
        Assert.Equal("ci.610", atioz.Version);
        Assert.Equal(0, atioz.ReadyReplicas);
        Assert.Equal(1, atioz.DesiredReplicas);

        var memex = instances[1];
        Assert.Equal("memex", memex.Namespace);
        Assert.Equal("memex.systemorph.com", memex.Domain);
        Assert.Equal("ci.612", memex.Version);
        Assert.Equal(2, memex.ReadyReplicas);
        Assert.Equal(2, memex.DesiredReplicas);
    }

    [Fact]
    public void ParseInstances_NoIngress_LeavesDomainEmpty_ButStillLists()
    {
        var instances = KubernetesInstanceService.ParseInstances(DeploymentsJson, """{ "items": [] }""", new InstancesOptions());
        Assert.Equal(2, instances.Length);
        Assert.All(instances, i => Assert.Equal(string.Empty, i.Domain));
    }

    [Theory]
    [InlineData("meshweaver.azurecr.io/memex-portal-ai:ci.612", "ci.612")]
    [InlineData("memex-portal-ai:latest", "latest")]
    [InlineData("registry:5000/repo:tag", "tag")]
    [InlineData("repo@sha256:abcdef", "")]
    [InlineData("", "")]
    public void VersionTag_ExtractsTagAfterLastColon_IgnoringDigest(string image, string expected)
        => Assert.Equal(expected, KubernetesInstanceService.VersionTag(image));

    [Fact]
    public void GrafanaLogsUrl_EmptyBase_ReturnsNull()
        => Assert.Null(new InstancesOptions().GrafanaLogsUrl("memex"));

    [Fact]
    public void GrafanaLogsUrl_WithBase_EncodesNamespaceQuery()
    {
        var url = new InstancesOptions { GrafanaBaseUrl = "https://grafana.example/" }.GrafanaLogsUrl("memex-cloud");
        Assert.NotNull(url);
        Assert.StartsWith("https://grafana.example/explore?", url);
        // The namespace ends up inside the URL-encoded Loki query.
        Assert.Contains("memex-cloud", Uri.UnescapeDataString(url!));
        Assert.Contains("loki", Uri.UnescapeDataString(url!));
    }

    [Fact]
    public void GrafanaLogsUrl_TemplateOverride_SubstitutesTokens()
    {
        var opts = new InstancesOptions
        {
            GrafanaBaseUrl = "https://g.example",
            GrafanaLogsUrlTemplate = "{base}/logs?ns={namespace}",
        };
        Assert.Equal("https://g.example/logs?ns=memex", opts.GrafanaLogsUrl("memex"));
    }

    [Theory]
    [InlineData("acme", true)]
    [InlineData("memex-cloud", true)]
    [InlineData("Acme", false)]      // uppercase not allowed
    [InlineData("-acme", false)]     // leading dash
    [InlineData("acme-", false)]     // trailing dash
    [InlineData("ac me", false)]     // space
    [InlineData("", false)]
    public void IsValidName_EnforcesK8sNamespaceRules(string name, bool expected)
        => Assert.Equal(expected, InstanceProvisioningPlan.IsValidName(name));

    [Fact]
    public void Generate_ValidInputs_EmitsParameterizedCommandPlan()
    {
        var plan = InstanceProvisioningPlan.Generate("acme", "acme.meshweaver.cloud", null, new InstancesOptions());

        Assert.Contains("Provisioning plan for instance `acme`", plan);
        Assert.Contains("acme.meshweaver.cloud", plan);
        // Database defaults from the name (dashes stripped) when not given.
        Assert.Contains("db create", plan);
        Assert.Contains("-d acme", plan);
        Assert.Contains("portalNamespaces", plan);
        Assert.Contains("deploy/aks/envs/acme/deploy.sh", plan);
    }

    [Fact]
    public void Generate_InvalidName_ReturnsCannotGenerate_NotCommands()
    {
        var plan = InstanceProvisioningPlan.Generate("Bad Name", "x.example", null, new InstancesOptions());
        Assert.Contains("Cannot generate a plan yet", plan);
        Assert.DoesNotContain("deploy.sh", plan);
    }
}
