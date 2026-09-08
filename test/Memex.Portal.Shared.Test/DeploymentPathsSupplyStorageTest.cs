using System.IO;
using System.Text.RegularExpressions;
using MeshWeaver.Deployment;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Every path that deploys a PORTAL must state its storage explicitly.
///
/// <para>🚨 <b>This is the dangerous direction of the first-run setup change, and it is the one
/// with a production outage on the other side.</b> The deployed image deliberately no longer bakes
/// a <c>Graph:Storage:Type</c> default — that default made the setup wizard unreachable on
/// Kubernetes (MeshWeaver.Plugins <c>DeployedImageDoesNotPreAnswerStorageTest</c>). The safety of
/// removing it rests ENTIRELY on the deployment paths supplying the value themselves. If one of
/// them ever stops, a configured production portal with real data reboots into a FIRST-RUN SETUP
/// WIZARD, serving no content and offering a stranger the chance to configure it — and nothing
/// fails, because "no storage configured" is a legitimate state by design.</para>
///
/// <para>So the two halves are guarded on opposite sides: the image must NOT answer, and these
/// must. Each guard lives with its subject, and neither can be satisfied by the other moving.</para>
/// </summary>
public class DeploymentPathsSupplyStorageTest
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }

    [Fact]
    public void TheHelmChartStatesTheStorageType()
    {
        // The AKS lane. Every portal the chart deploys reads Graph__Storage__Type from its
        // ConfigMap, and the ConfigMap emits the key only when the values state it.
        var values = Path.Combine(RepoRoot().FullName, "deploy", "helm", "values.yaml");
        Assert.True(File.Exists(values), $"the chart's values are not at {values}");

        var match = Regex.Match(File.ReadAllText(values),
            @"^\s*Graph__Storage__Type:\s*""(?<v>[^""]*)""", RegexOptions.Multiline);

        Assert.True(match.Success,
            $"{values} no longer states Graph__Storage__Type. The deployed image does not carry a "
            + "default any more, so every portal this chart deploys would boot into the first-run "
            + "SETUP WIZARD instead of serving its data.");
        Assert.False(string.IsNullOrWhiteSpace(match.Groups["v"].Value),
            "Graph__Storage__Type is present but EMPTY — the ConfigMap omits an empty key, which is "
            + "indistinguishable from not stating it at all.");
    }

    [Fact]
    public void TheAspireHostStatesTheStorageType()
    {
        // The Azure Container Apps lane, which does not use the chart at all — so the chart guard
        // above says nothing about it. It was the ACA portals that made this worth checking:
        // they configure the portal purely through environment variables.
        //
        // Since the Deployment record became the ONE input (2026-09-08), the adapter states no key
        // itself: every portal key is DERIVED from the record by the contract's
        // DeploymentPortalConfig.PortalConfig — the same derivation the Helm chart renders. So the
        // guard has two halves, each with its subject: the adapter must still route through that
        // derivation, and the derivation must state the storage type for a record that says
        // NOTHING about storage (a bare AddMemex(name) is exactly that record).
        var hosting = Path.Combine(RepoRoot().FullName, "memex", "aspire",
            "Memex.Aspire.Hosting", "MemexHostingExtensions.cs");
        Assert.True(File.Exists(hosting), $"the Aspire hosting extensions are not at {hosting}");
        Assert.Contains("DeploymentPortalConfig.PortalConfig(", File.ReadAllText(hosting));

        var config = DeploymentPortalConfig.PortalConfig(new DeploymentContent(), PortalConfigOptions.Aspire(mcpBaseUrl: null));

        Assert.True(config.TryGetValue("Graph__Storage__Type", out var type),
            "the record's portal-config derivation no longer emits Graph__Storage__Type for a record "
            + "that states no storage layout. The deployed image carries no default, so an Aspire/ACA "
            + "portal would boot into the first-run SETUP WIZARD rather than serving its data.");
        Assert.False(string.IsNullOrWhiteSpace(type),
            "Graph__Storage__Type derives to an EMPTY value, which reads as 'not configured'.");
    }
}
