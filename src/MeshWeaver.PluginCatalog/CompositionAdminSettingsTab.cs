using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Features;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Platform-admin surface for THIS environment's composition — the answer to "why does this portal
/// carry that, and why does it not carry this?".
///
/// <para>Two tables, because there are exactly two ways a package's presence is decided here:</para>
/// <list type="bullet">
///   <item>the environment's <b>feature flags</b> (<c>Features:Flags:*</c>) — an enabled flag
///     includes its packages, a declared-but-disabled one excludes them;</item>
///   <item>the <b>parameters</b> an installed package declares — a required connection string or
///     endpoint this environment does not supply REFUSES the install, and this is where an operator
///     sees which key to provision without reading a pod log.</item>
/// </list>
///
/// <para>Read-only by design: composition is a DEPLOYMENT decision that arrives through the
/// environment's values file (helm → ConfigMap → env), and a portal that let an admin edit it in the
/// browser would be overwritten by the next <c>helm upgrade</c> — the exact "hand-patching does not
/// stick" trap the chart already documents. The tab reports; the values file decides.</para>
/// </summary>
public static class CompositionAdminSettingsTab
{
    /// <summary>Settings menu id.</summary>
    public const string TabId = "Composition";

    private const string FlagListDataId = "compositionFlagList";
    private const string ParameterListDataId = "compositionParameterList";

    /// <summary>Registers the composition settings tab provider (global admins only).</summary>
    /// <param name="config">The hub configuration to extend.</param>
    /// <returns>The same configuration, for chaining.</returns>
    public static MessageHubConfiguration AddCompositionAdminSettingsTab(
        this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(new SettingsMenuItemProvider(Contribute));

    /// <summary>
    /// Contributes the tab, but only once <c>IsGlobalAdmin</c> confirms POSITIVELY — the same shape
    /// as the other Administration tabs: start with nothing so the menu renders immediately, add the
    /// entry when the check comes back true, and on timeout/error stay hidden.
    /// </summary>
    /// <param name="host">The rendering layout-area host.</param>
    /// <param name="ctx">The rendering context.</param>
    /// <returns>The tab definitions this viewer may see.</returns>
    public static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> Contribute(
        LayoutAreaHost host, RenderingContext ctx)
    {
        IReadOnlyList<SettingsMenuItemDefinition> none = [];

        // Same home as the other Administration tabs: the admin's OWN settings page.
        var hubPath = host.Hub.Address.ToString();
        var nodeOwnerId = hubPath.StartsWith("User/", StringComparison.OrdinalIgnoreCase)
            ? hubPath["User/".Length..]
            : hubPath;

        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewerId = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
        if (string.IsNullOrEmpty(viewerId)
            || !string.Equals(viewerId, nodeOwnerId, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(none);

        var tab = new SettingsMenuItemDefinition(
            Id: TabId,
            Label: "Composition",
            ContentBuilder: BuildContent,
            Group: "Administration",
            Icon: FluentIcons.Flag(),
            GroupIcon: FluentIcons.Shield(),
            Order: 336,
            Keywords: ["composition", "feature", "flag", "environment", "package", "parameter"])
        { LabelKey = "composition.title", GroupKey = "settings.groupAdministration" };

        return host.Hub.IsGlobalAdmin(viewerId)
            .Where(isAdmin => isAdmin)
            .Take(1)
            .Select(_ => (IReadOnlyList<SettingsMenuItemDefinition>)new[] { tab })
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node) =>
        stack
            .WithView(Controls.Title(host.Localize("composition.title"), 2))
            .WithView(Controls.Markdown(host.Localize("composition.intro")))
            .WithView(Controls.Title(host.Localize("composition.flags.heading"), 3))
            .WithView(FlagSection(host))
            .WithView(Controls.Title(host.Localize("composition.parameters.heading"), 3))
            .WithView(Controls.Markdown(host.Localize("composition.parameters.hint")))
            .WithView(ParameterSection(host));

    /// <summary>
    /// The declared flags, bound to the LIVE flag surface — <see cref="IFeatureFlags.All"/> pushes
    /// again on a configuration reload, so the table is never a startup snapshot.
    /// </summary>
    private static UiControl FlagSection(LayoutAreaHost host)
    {
        var flags = host.Hub.ServiceProvider.GetService<IFeatureFlags>();
        if (flags is null)
            return Controls.Markdown(host.Localize("composition.noFlags"));

        return Controls.Stack.WithView(flags.All
            .Select(all => all.Values
                .Select(flag => new FlagRow(
                    flag.Name,
                    host.Localize(flag.Enabled ? "composition.state.on" : "composition.state.off"),
                    flag.Packages.Count == 0
                        ? ""
                        : host.Localize(flag.Enabled
                            ? "composition.effect.includes"
                            : "composition.effect.excludes"),
                    string.Join(", ", flag.Packages),
                    flag.Description ?? ""))
                .ToList())
            .Select(rows => rows.Count == 0
                ? (UiControl)Controls.Markdown(host.Localize("composition.noFlags"))
                : FlagGrid(host, rows)));
    }

    private static UiControl FlagGrid(LayoutAreaHost host, IReadOnlyList<FlagRow> rows)
    {
        host.UpdateData(FlagListDataId, rows);
        return new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(FlagListDataId)))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(FlagRow.Flag).ToCamelCase() }
                .WithTitle(host.Localize("composition.column.flag")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(FlagRow.State).ToCamelCase() }
                .WithTitle(host.Localize("composition.column.state")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(FlagRow.Effect).ToCamelCase() }
                .WithTitle(host.Localize("composition.column.effect")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(FlagRow.Packages).ToCamelCase() }
                .WithTitle(host.Localize("composition.column.packages")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(FlagRow.Description).ToCamelCase() }
                .WithTitle(host.Localize("composition.column.description")));
    }

    /// <summary>
    /// The parameters the INSTALLED packages declare, and whether this environment supplies each.
    /// An unsupplied REQUIRED parameter is the reason an install was refused, so the "provision"
    /// column carries the exact env var to set — the same string the refusal logs.
    /// </summary>
    private static UiControl ParameterSection(LayoutAreaHost host)
    {
        var configuration = host.Hub.ServiceProvider.GetService<IConfiguration>();
        return Controls.Stack.WithView(CatalogLayoutAreas.ObserveInstalledManifests(host)
            .Select(manifests => manifests
                .SelectMany(m => m.Parameters.Select(p => new ParameterRow(
                    m.Id,
                    p.Name,
                    p.Kind.ToString(),
                    PackageParameters.EnvironmentVariable(p),
                    host.Localize(PackageParameters.Resolve(configuration, p) is not null
                        ? "composition.supplied.yes"
                        : p.Optional
                            ? "composition.supplied.optional"
                            : "composition.supplied.no"))))
                .OrderBy(r => r.Package, StringComparer.Ordinal)
                .ThenBy(r => r.Parameter, StringComparer.Ordinal)
                .ToList())
            .Select(rows => rows.Count == 0
                ? (UiControl)Controls.Markdown(host.Localize("composition.noParameters"))
                : ParameterGrid(host, rows)));
    }

    private static UiControl ParameterGrid(LayoutAreaHost host, IReadOnlyList<ParameterRow> rows)
    {
        host.UpdateData(ParameterListDataId, rows);
        return new DataGridControl(
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(ParameterListDataId)))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(ParameterRow.Package).ToCamelCase() }
                .WithTitle(host.Localize("composition.column.package")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(ParameterRow.Parameter).ToCamelCase() }
                .WithTitle(host.Localize("composition.column.parameter")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(ParameterRow.Kind).ToCamelCase() }
                .WithTitle(host.Localize("composition.column.kind")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(ParameterRow.Provision).ToCamelCase() }
                .WithTitle(host.Localize("composition.column.provision")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(ParameterRow.Supplied).ToCamelCase() }
                .WithTitle(host.Localize("composition.column.supplied")));
    }

    /// <summary>Plain row record bound into the flag <see cref="DataGridControl"/>.</summary>
    internal record FlagRow(
        string Flag, string State, string Effect, string Packages, string Description);

    /// <summary>Plain row record bound into the parameter <see cref="DataGridControl"/>.</summary>
    internal record ParameterRow(
        string Package, string Parameter, string Kind, string Provision, string Supplied);
}
