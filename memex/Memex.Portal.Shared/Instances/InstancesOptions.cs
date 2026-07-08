using System.Text.Json;

namespace Memex.Portal.Shared.Instances;

/// <summary>
/// Configuration for the platform-admin Instances view (<see cref="InstancesAdminLayoutArea"/>).
/// Defaults target the standard shared-AKS topology (see Doc/Architecture/Instances.md). Override
/// per environment via the <c>Instances:*</c> configuration section — most importantly
/// <see cref="GrafanaBaseUrl"/>, which is empty until the environment sets its Grafana host.
/// </summary>
public record InstancesOptions
{
    /// <summary>The k8s label whose value marks a portal Deployment (and Ingress). The chart stamps
    /// <c>app.kubernetes.io/component=memex-portal</c> on the portal workloads; the instance query
    /// selects on it so only portals are listed, never migrations or other pods.</summary>
    public string PortalComponentLabel { get; init; } = "app.kubernetes.io/component";

    /// <summary>The <see cref="PortalComponentLabel"/> value that identifies a portal.</summary>
    public string PortalComponentValue { get; init; } = "memex-portal";

    /// <summary>The container name within a portal Deployment whose image tag IS the running version.</summary>
    public string PortalContainer { get; init; } = "memex-portal";

    /// <summary>Base URL of the Grafana that serves this platform's logs (e.g.
    /// <c>https://grafana.systemorph.com</c>). Empty → the Instances view shows no logs link and a
    /// hint to configure it. Set via <c>Instances:GrafanaBaseUrl</c>.</summary>
    public string GrafanaBaseUrl { get; init; } = string.Empty;

    /// <summary>Optional override for the per-instance Grafana logs URL, with <c>{base}</c> and
    /// <c>{namespace}</c> tokens (the namespace value is URL-encoded). Set this when your Grafana
    /// version's Explore URL format differs from the built-in default. Empty → the built-in
    /// Grafana-Explore (Loki) deep link is used.</summary>
    public string GrafanaLogsUrlTemplate { get; init; } = string.Empty;

    // ── Displayed / used for the guided create-instance command generation (Instances.md) ──

    /// <summary>AKS cluster name (display + generated commands).</summary>
    public string ClusterName { get; init; } = "memexaks-cluster";

    /// <summary>Azure resource group of the cluster + PG server (generated commands).</summary>
    public string ResourceGroup { get; init; } = "memex-aks-rg";

    /// <summary>Shared PostgreSQL Flexible Server name (generated commands).</summary>
    public string PostgresServer { get; init; } = "memexaks-pg";

    /// <summary>DNS zone new instance hosts live under (generated commands / display).</summary>
    public string DnsZone { get; init; } = "meshweaver.cloud";

    /// <summary>Container registry the portal image is pulled from (display).</summary>
    public string Registry { get; init; } = "meshweaver.azurecr.io";

    /// <summary>
    /// The Grafana logs deep-link for a namespace, or null when <see cref="GrafanaBaseUrl"/> is
    /// unset. Uses <see cref="GrafanaLogsUrlTemplate"/> when provided, otherwise builds a Grafana
    /// Explore (Loki) link querying <c>{namespace="&lt;ns&gt;"}</c>. Pure — unit-tested.
    /// </summary>
    public string? GrafanaLogsUrl(string @namespace)
    {
        if (string.IsNullOrWhiteSpace(GrafanaBaseUrl))
            return null;
        var baseUrl = GrafanaBaseUrl.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(GrafanaLogsUrlTemplate))
            return GrafanaLogsUrlTemplate
                .Replace("{base}", baseUrl)
                .Replace("{namespace}", Uri.EscapeDataString(@namespace));

        // Grafana Explore (Loki) — the `panes` schema (Grafana 10+). Built with JsonSerializer so
        // the nested braces are never a string-literal hazard, then URL-encoded as one query param.
        var panes = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["mw"] = new
            {
                datasource = "loki",
                queries = new[] { new { refId = "A", expr = $"{{namespace=\"{@namespace}\"}}" } },
                range = new { from = "now-1h", to = "now" },
            },
        });
        return $"{baseUrl}/explore?schemaVersion=1&orgId=1&panes={Uri.EscapeDataString(panes)}";
    }
}
