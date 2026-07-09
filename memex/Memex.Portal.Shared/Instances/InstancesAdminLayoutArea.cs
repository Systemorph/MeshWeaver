using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Instances;

/// <summary>
/// Platform-admin "Instances" overview. Queries the live Kubernetes cluster and lists every portal
/// instance — its public host, namespace, running version (image tag) and replica health — with a
/// per-instance link to the Grafana logs. A guided "Create a new instance" section turns a few
/// inputs into the exact provisioning command sequence (a plan; it mutates NOTHING). Gated to
/// platform admins (Admin-partition <c>Permission.All</c>), like the Partitions tab.
/// </summary>
public static class InstancesAdminLayoutArea
{
    /// <summary>Area name for the instances overview.</summary>
    public const string InstancesArea = "Instances";

    private const string SettingsTabId = "Instances";
    private const string RefreshId = "instancesRefresh";
    private const string CreateFormId = "instancesCreateForm";
    private const string CreatePlanId = "instancesCreatePlan";

    /// <summary>Plain row record bound into the <see cref="DataGridControl"/> (camelCased property names).</summary>
    public sealed record InstanceRow
    {
        /// <summary>Public host from the namespace's Ingress.</summary>
        public string Domain { get; init; } = string.Empty;
        /// <summary>Kubernetes namespace.</summary>
        public string Namespace { get; init; } = string.Empty;
        /// <summary>Running version (portal image tag).</summary>
        public string Version { get; init; } = string.Empty;
        /// <summary>Replica health, "ready/desired".</summary>
        public string Replicas { get; init; } = string.Empty;
    }

    /// <summary>Registers the global-admin "Instances" tab on the node's Settings page. The provider
    /// yields the tab only once the viewer is confirmed a global admin; the content gates again as
    /// defense-in-depth.</summary>
    public static MessageHubConfiguration AddInstancesAdminSettingsTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(new SettingsMenuItemProvider(GetInstancesTab));

    private static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> GetInstancesTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        IReadOnlyList<SettingsMenuItemDefinition> none = Array.Empty<SettingsMenuItemDefinition>();

        var viewerId = ResolveViewerId(host);
        if (string.IsNullOrEmpty(viewerId))
            return Observable.Return(none);

        var tab = new SettingsMenuItemDefinition(
            Id: SettingsTabId,
            Label: "Instances",
            ContentBuilder: BuildInstancesTab,
            Group: "Administration",
            Icon: FluentIcons.Server(),
            GroupIcon: FluentIcons.Shield(),
            Order: 305,
            Keywords: ["instances", "instance", "cluster", "aks", "kubernetes", "namespaces", "deployments",
                "version", "grafana", "logs", "infra", "infrastructure", "administration", "platform",
                "create instance", "provision", "environment"]);

        return host.Hub.IsGlobalAdmin(viewerId)
            .Where(isAdmin => isAdmin)
            .Take(1)
            .Select(_ => (IReadOnlyList<SettingsMenuItemDefinition>)new[] { tab })
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }

    private static UiControl BuildInstancesTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
        => stack.WithView(BuildArea(host));

    /// <summary>The reactive Instances area, registrable via
    /// <c>AddLayout(layout =&gt; layout.WithView(InstancesArea, InstancesOverview))</c>.</summary>
    [Browsable(false)]
    public static IObservable<UiControl?> InstancesOverview(LayoutAreaHost host, RenderingContext _)
        => BuildArea(host);

    private static IObservable<UiControl?> BuildArea(LayoutAreaHost host)
    {
        var viewerId = ResolveViewerId(host);
        if (string.IsNullOrEmpty(viewerId))
            return Observable.Return<UiControl?>(AccessDenied());

        return host.Hub.IsGlobalAdmin(viewerId)
            .Where(isAdmin => isAdmin)
            .Take(1)
            .Select(_ => true)
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<bool, Exception>(_ => Observable.Return(false))
            .Select(isAdmin => isAdmin
                ? BuildAdminView(host)
                : Observable.Return<UiControl?>(AccessDenied()))
            .Switch();
    }

    private static UiControl AccessDenied()
        => Controls.Markdown("Access denied — platform admins only.");

    private static IObservable<UiControl?> BuildAdminView(LayoutAreaHost host)
    {
        var options = host.Hub.ServiceProvider.GetService<InstancesOptions>() ?? new InstancesOptions();
        var service = host.Hub.ServiceProvider.GetService<IClusterInstanceService>();
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(InstancesAdminLayoutArea));

        // Seed the create-instance form ONCE (outside the refresh loop, so typed values survive a
        // Refresh) so clicking "Generate plan" before touching any field still emits — the plan
        // generator then returns actionable "fill in the fields" feedback instead of silence.
        host.RegisterForDisposal(Observable.Return(new Dictionary<string, object?>())
            .Subscribe(seed => host.UpdateData(CreateFormId, seed)));

        // Re-query on each Refresh click (a fresh guid retriggers the Switch).
        return host.Stream.GetDataStream<string>(RefreshId).StartWith("")
            .Select(_ =>
            {
                if (service is null || !service.CanQuery)
                    return Observable.Return<UiControl?>(
                        BuildView(host, ImmutableArray<InstanceInfo>.Empty, options, canQuery: false, logger));
                return service.GetInstances()
                    .Select(instances => (UiControl?)BuildView(host, instances, options, canQuery: true, logger))
                    .StartWith((UiControl?)Controls.Markdown("*Querying the cluster…*"));
            })
            .Switch();
    }

    private static UiControl BuildView(
        LayoutAreaHost host, ImmutableArray<InstanceInfo> instances,
        InstancesOptions options, bool canQuery, ILogger? logger)
    {
        var stack = Controls.Stack
            .WithView(Controls.Title("Platform Instances", 1))
            .WithView(Controls.Markdown(
                "Live from the Kubernetes cluster — every portal instance on "
                + $"`{options.ClusterName}`, its running version and replica health. "
                + "Use the log links to jump to Grafana."));

        // Refresh
        stack = stack.WithView(Controls.Button("Refresh")
            .WithAppearance(Appearance.Outline)
            .WithIconStart(FluentIcons.ArrowClockwise())
            .WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(RefreshId, Guid.NewGuid().ToString("N"));
                return Task.CompletedTask;
            }));

        if (!canQuery)
            stack = stack.WithView(Controls.Markdown(
                "> **Cluster query unavailable.** This install can't read the cluster — it isn't running "
                + "in Kubernetes, or its service account lacks cluster-read RBAC. Enable "
                + "`instancesAdmin.clusterRead` on THIS instance's Helm release (granted only to the "
                + "company instance, never public/customer instances). See "
                + "[Instances](/Doc/Architecture/Instances)."));
        else if (instances.Length == 0)
            stack = stack.WithView(Controls.Markdown("*No portal instances found on the cluster.*"));
        else
        {
            var rows = instances
                .Select(i => new InstanceRow
                {
                    Domain = i.Domain,
                    Namespace = i.Namespace,
                    Version = i.Version,
                    Replicas = $"{i.ReadyReplicas}/{i.DesiredReplicas}",
                })
                .ToImmutableArray();

            var id = Guid.NewGuid().ToString();
            host.RegisterForDisposal(Observable.Return(rows).Subscribe(data => host.UpdateData(id, data)));

            stack = stack.WithView(Controls.DataGrid(new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)))
                .WithColumn(new PropertyColumnControl<string>
                    { Property = nameof(InstanceRow.Domain).ToCamelCase() }.WithTitle("Instance"))
                .WithColumn(new PropertyColumnControl<string>
                    { Property = nameof(InstanceRow.Namespace).ToCamelCase() }.WithTitle("Namespace"))
                .WithColumn(new PropertyColumnControl<string>
                    { Property = nameof(InstanceRow.Version).ToCamelCase() }.WithTitle("Version"))
                .WithColumn(new PropertyColumnControl<string>
                    { Property = nameof(InstanceRow.Replicas).ToCamelCase() }.WithTitle("Ready"))
                .Resizable());

            // Grafana log links — one per instance. Markdown anchors (robust for external hosts).
            stack = stack.WithView(Controls.Title("Logs", 2));
            if (string.IsNullOrWhiteSpace(options.GrafanaBaseUrl))
                stack = stack.WithView(Controls.Markdown(
                    "*Set `Instances:GrafanaBaseUrl` to enable per-instance Grafana log links.*"));
            else
            {
                var links = instances.Select(i =>
                {
                    var label = string.IsNullOrEmpty(i.Domain) ? i.Namespace : i.Domain;
                    var url = options.GrafanaLogsUrl(i.Namespace);
                    return url is null ? $"- {label}" : $"- [{label} — logs]({url})";
                });
                stack = stack.WithView(Controls.Markdown(string.Join("\n", links)));
            }
        }

        stack = stack.WithView(BuildCreateSection(options, logger));
        return stack;
    }

    /// <summary>
    /// Guided create-instance: inputs → a generated provisioning PLAN (commands), rendered below.
    /// Mutates no infrastructure — the admin runs the emitted commands themselves.
    /// </summary>
    private static UiControl BuildCreateSection(InstancesOptions options, ILogger? logger)
    {
        var form = Controls.Stack
            .WithView(Controls.Title("Create a new instance", 2))
            .WithView(Controls.Markdown(
                "Generate the vetted provisioning command sequence for a new instance on this "
                + "cluster. **This produces a plan only — nothing is deployed automatically.**"));

        var inputs = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("align-items: flex-end; flex-wrap: wrap;");
        inputs = inputs.WithView(new TextFieldControl(new JsonPointerReference("name"))
        {
            Label = "Instance name (namespace)",
            Placeholder = "e.g. acme",
            DataContext = LayoutAreaReference.GetDataPointer(CreateFormId),
        }.WithWidth("220px"));
        inputs = inputs.WithView(new TextFieldControl(new JsonPointerReference("domain"))
        {
            Label = "Domain",
            Placeholder = $"e.g. acme.{options.DnsZone}",
            DataContext = LayoutAreaReference.GetDataPointer(CreateFormId),
        }.WithWidth("260px"));
        inputs = inputs.WithView(new TextFieldControl(new JsonPointerReference("database"))
        {
            Label = "Database (optional)",
            Placeholder = "defaults from name",
            DataContext = LayoutAreaReference.GetDataPointer(CreateFormId),
        }.WithWidth("220px"));
        inputs = inputs.WithView(Controls.Button("Generate plan")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.CloudAdd())
            .WithClickAction(ctx =>
            {
                ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(CreateFormId)
                    .Take(1)
                    .Subscribe(f =>
                    {
                        string? Get(string k) => f.GetValueOrDefault(k)?.ToString()?.Trim();
                        var plan = InstanceProvisioningPlan.Generate(Get("name"), Get("domain"), Get("database"), options);
                        ctx.Host.UpdateData(CreatePlanId, plan);
                    },
                    ex => logger?.LogWarning(ex, "[Instances] reading the create-instance form failed"));
                return Task.CompletedTask;
            }));

        form = form.WithView(inputs);
        // The generated plan renders here, updating in place when "Generate plan" is clicked.
        form = form.WithView((h, _) => h.Stream.GetDataStream<string>(CreatePlanId)
            .Select(md => (UiControl?)Controls.Markdown(string.IsNullOrEmpty(md)
                ? "*Fill in the fields and click **Generate plan**.*"
                : md))
            .StartWith((UiControl?)Controls.Markdown("*Fill in the fields and click **Generate plan**.*")));
        return form;
    }

    private static string? ResolveViewerId(LayoutAreaHost host)
    {
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        return accessService?.Context?.ObjectId
               ?? accessService?.CircuitContext?.ObjectId;
    }
}
