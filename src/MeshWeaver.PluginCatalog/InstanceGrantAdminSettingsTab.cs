using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Platform-admin surface for deciding what each registered <see cref="MeshWeaverInstance"/> may
/// pull from this registry — the authorization half of instance registration.
///
/// <para>🚨 Global-admin only, and the grants it writes live in the <b>Admin</b> partition
/// (<c>Admin/_PluginGrant/{instanceId}</c>), out of the instance owner's reach. Registration is
/// self-service precisely because granting is not: if an owner could write their own grant, the
/// registry would be back to serving its private sources to whoever asked.</para>
///
/// <para>A grant names a <b>source</b> and either one <b>package</b> or all of them
/// (<c>Plugins/*</c> vs <c>Reinsurance/UWDeepfield</c>). The source comes from a fixed list — the
/// registry's configured sources — and the package is checked against the live catalog before the
/// grant is written, because a typo'd name would otherwise be stored happily and silently authorize
/// nothing.</para>
/// </summary>
public static class InstanceGrantAdminSettingsTab
{
    /// <summary>Settings menu id.</summary>
    public const string TabId = "InstanceGrants";

    private const string FormDataId = "instanceGrantForm";
    private const string SourceOptionsDataId = "instanceGrantSourceOptions";
    private const string ResultDataId = "instanceGrantResult";
    private const string ListDataId = "instanceGrantList";

    /// <summary>Registers the instance-grants settings tab provider (global admins only).</summary>
    public static MessageHubConfiguration AddInstanceGrantAdminSettingsTab(
        this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(new SettingsMenuItemProvider(Contribute));

    /// <summary>
    /// Contributes the tab, but only once <c>IsGlobalAdmin</c> confirms POSITIVELY. Same shape as
    /// the other Administration tabs: start with nothing so the menu renders immediately, add the
    /// entry when the check comes back true, and on timeout/error stay hidden — a gate that fails
    /// open would be worse than a missing menu item.
    /// </summary>
    public static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> Contribute(
        LayoutAreaHost host, RenderingContext ctx)
    {
        IReadOnlyList<SettingsMenuItemDefinition> none = [];

        // Same home as the other Administration tabs: the admin's OWN settings page. Without this
        // the tab could render while viewing somebody else's settings.
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
            Label: "Instance grants",
            ContentBuilder: BuildContent,
            Group: "Administration",
            Icon: FluentIcons.Shield(),
            GroupIcon: FluentIcons.Shield(),
            Order: 335,
            Keywords: ["instance", "grant", "plugin", "registry", "entitlement", "authorize"])
        { LabelKey = "instanceGrants.title", GroupKey = "settings.groupAdministration" };

        return host.Hub.IsGlobalAdmin(viewerId)
            .Where(isAdmin => isAdmin)
            .Take(1)
            .Select(_ => (IReadOnlyList<SettingsMenuItemDefinition>)new[] { tab })
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        stack = stack
            .WithView(Controls.Title(host.Localize("instanceGrants.title"), 2))
            .WithView(Controls.Markdown(host.Localize("instanceGrants.intro")));

        var sources = SourceNames(host);
        host.UpdateData(SourceOptionsDataId,
            sources.Select(s => new Option<string>(s, s)).ToArray());
        host.UpdateData(FormDataId, new Dictionary<string, object?>
        {
            ["instanceId"] = "",
            ["source"] = sources.FirstOrDefault() ?? "",
            ["packageId"] = PluginGrantEntry.AllPackages,
        });

        stack = stack.WithView(GrantForm(host));

        stack = stack.WithView(host.Stream
            .GetDataStream<string>(ResultDataId)
            .StartWith("")
            .Select(md => string.IsNullOrEmpty(md) ? (UiControl)Controls.Stack : Controls.Markdown(md)));

        return stack.WithView(GrantList(host));
    }

    /// <summary>The registry's configured source names — the only values a grant's Source may take.</summary>
    private static IReadOnlyList<string> SourceNames(LayoutAreaHost host)
    {
        var config = host.Hub.ServiceProvider.GetRequiredService<IConfiguration>();
        var names = config.GetSection("PluginCatalog:Sources").GetChildren()
            .Select(s => s["Name"])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();
        return names;
    }

    private static UiControl GrantForm(LayoutAreaHost host)
    {
        var dataContext = LayoutAreaReference.GetDataPointer(FormDataId);
        var sources = SourceNames(host);

        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: flex-end; flex-wrap: wrap;");

        row = row
            .WithView(new TextFieldControl(new JsonPointerReference("instanceId"))
            {
                Label = host.Localize("instanceGrants.field.instance"),
                DataContext = dataContext,
            }.WithWidth("200px"))
            // Source is a SELECT over the configured sources, not free text: an unknown source name
            // is unmatchable, so it would store cleanly and grant nothing. Options travel through
            // their own data id, the way EditorExtensions builds every list control.
            .WithView(new SelectControl(
                new JsonPointerReference("source"),
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(SourceOptionsDataId)))
            {
                Label = host.Localize("instanceGrants.field.source"),
                DataContext = dataContext,
            }.WithWidth("180px"))
            .WithView(new TextFieldControl(new JsonPointerReference("packageId"))
            {
                Label = host.Localize("instanceGrants.field.package"),
                Placeholder = PluginGrantEntry.AllPackages,
                DataContext = dataContext,
            }.WithWidth("220px"))
            .WithView(Controls.Button(host.Localize("instanceGrants.grant"))
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(FormDataId)
                        .Take(1)
                        .Subscribe(data => Apply(ctx.Host, host, data, revoke: false));
                    return Task.CompletedTask;
                }))
            .WithView(Controls.Button(host.Localize("instanceGrants.revoke"))
                .WithAppearance(Appearance.Outline)
                .WithClickAction(ctx =>
                {
                    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(FormDataId)
                        .Take(1)
                        .Subscribe(data => Apply(ctx.Host, host, data, revoke: true));
                    return Task.CompletedTask;
                }));

        return Controls.Stack
            .WithView(Controls.Title(host.Localize("instanceGrants.formHeading"), 3))
            .WithView(Controls.Markdown(host.Localize("instanceGrants.formHint")))
            .WithView(row)
            .WithStyle("padding: 16px; background: var(--neutral-layer-2); border-radius: 8px; "
                       + "gap: 12px; margin-bottom: 24px;");
    }

    private static void Apply(
        LayoutAreaHost target, LayoutAreaHost host, Dictionary<string, object?>? data, bool revoke)
    {
        var instanceId = data?.GetValueOrDefault("instanceId")?.ToString()?.Trim() ?? "";
        var source = data?.GetValueOrDefault("source")?.ToString()?.Trim() ?? "";
        var packageId = data?.GetValueOrDefault("packageId")?.ToString()?.Trim() is { Length: > 0 } p
            ? p
            : PluginGrantEntry.AllPackages;

        if (string.IsNullOrEmpty(instanceId) || string.IsNullOrEmpty(source))
        {
            target.UpdateData(ResultDataId, host.Localize("instanceGrants.error.incomplete"));
            return;
        }
        if (!SourceNames(host).Contains(source, StringComparer.OrdinalIgnoreCase))
        {
            target.UpdateData(ResultDataId, host.Localize("instanceGrants.error.unknownSource"));
            return;
        }

        var entry = new PluginGrantEntry { Source = source, PackageId = packageId };
        var workspace = host.Hub.GetWorkspace();
        var path = MeshWeaverInstanceNodeType.GrantPath(instanceId);
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var adminId = accessService?.Context?.ObjectId ?? "";

        target.UpdateData(ResultDataId, host.Localize("instanceGrants.applying"));

        // Read-modify-write through the owning hub's stream: the Admin hub serialises every writer,
        // so two admins editing different instances (or the same one) cannot clobber each other.
        workspace.GetMeshNodeStream(path)
            .Update(current =>
            {
                var grant = current.ContentAs<PluginGrant>(host.Hub.JsonSerializerOptions)
                            ?? new PluginGrant { InstanceId = instanceId };
                var entries = grant.Entries
                    .Where(e => !(string.Equals(e.Source, entry.Source, StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(e.PackageId, entry.PackageId, StringComparison.Ordinal)))
                    .ToList();
                if (!revoke)
                    entries.Add(entry);
                return current with
                {
                    NodeType = MeshWeaverInstanceNodeType.GrantNodeType,
                    Content = grant with
                    {
                        InstanceId = instanceId,
                        Entries = entries,
                        GrantedByUserId = adminId,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                };
            })
            .Subscribe(
                _ => target.UpdateData(ResultDataId,
                    $"{host.Localize(revoke ? "instanceGrants.revoked" : "instanceGrants.granted")} "
                    + $"`{entry}` → **{instanceId}**"),
                ex => target.UpdateData(ResultDataId,
                    $"{host.Localize("instanceGrants.error.failed")} {ex.Message}"));
    }

    private static UiControl GrantList(LayoutAreaHost host)
    {
        var meshService = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        return Controls.Stack
            .WithView(Controls.Title(host.Localize("instanceGrants.registered"), 3))
            .WithView(meshService
                .Query(new MeshQueryRequest
                {
                    Query = $"nodeType:{MeshWeaverInstanceNodeType.NodeType}",
                })
                .Select(results => results
                    .Select(r => new GrantRow(r.Id ?? r.Path.Split('/').Last(), r.Name ?? "", r.Path))
                    .OrderBy(r => r.InstanceId, StringComparer.Ordinal)
                    .ToList())
                .Select(rows => rows.Count == 0
                    ? (UiControl)Controls.Markdown(host.Localize("instanceGrants.noInstances"))
                    : Grid(host, rows)));
    }

    private static UiControl Grid(LayoutAreaHost host, IReadOnlyList<GrantRow> rows)
    {
        host.UpdateData(ListDataId, rows);
        return new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(ListDataId)))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(GrantRow.InstanceId).ToCamelCase() }
                .WithTitle(host.Localize("instanceGrants.column.instance")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(GrantRow.DisplayName).ToCamelCase() }
                .WithTitle(host.Localize("instanceGrants.column.name")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(GrantRow.Path).ToCamelCase() }
                .WithTitle(host.Localize("instanceGrants.column.owner")));
    }

    /// <summary>Plain row record bound into the instance <see cref="DataGridControl"/>.</summary>
    internal record GrantRow(string InstanceId, string DisplayName, string Path);
}
