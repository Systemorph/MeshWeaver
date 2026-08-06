using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Memex.Portal.Shared.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Settings tab where a user registers their own MeshWeaver installations and gets each one an
/// instance key. Self-service by design — registering is identity, not entitlement: a new instance
/// authenticates against this portal's plugin registry and is allowed to pull <b>nothing</b> until a
/// platform admin grants it specific plugins (grants live in the Admin partition, out of the
/// owner's reach).
///
/// <para>Built from framework controls only — a <see cref="DataGridControl"/> over a plain row
/// record and <c>Controls.*</c> for everything else. No hand-built HTML: see AGENTS.md, "Never
/// hand-roll UI".</para>
/// </summary>
public static class InstancesSettingsTab
{
    /// <summary>Settings menu id.</summary>
    public const string TabId = "MeshWeaverInstances";

    private const string FormDataId = "instanceRegisterForm";
    private const string ResultDataId = "instanceRegisterResult";
    private const string ListDataId = "instanceList";

    /// <summary>Registers the tab under Security, beside API Tokens. Available to every signed-in
    /// user — registering an installation grants it nothing, so it needs no permission gate.</summary>
    public static MessageHubConfiguration AddInstancesSettingsTab(this MessageHubConfiguration config) =>
        config.AddSettingsMenuItems(
            new SettingsMenuItemDefinition(
                Id: TabId,
                Label: "Instances",
                ContentBuilder: BuildContent,
                Group: "Security",
                Icon: FluentIcons.Server(),
                Order: 235,
                RequiredPermission: Permission.None)
            { LabelKey = "settings.instances", GroupKey = "settings.groupSecurity" });

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var context = accessService?.Context;
        var userId = context?.ObjectId ?? "";

        stack = stack
            .WithView(Controls.Title(host.Localize("settings.instances"), 2))
            .WithView(Controls.Markdown(host.Localize("instances.intro")));

        host.UpdateData(FormDataId, new Dictionary<string, object?>
        {
            ["instanceId"] = "",
            ["displayName"] = "",
            ["homeUrl"] = "",
        });

        stack = stack.WithView(RegisterSection(host, userId, context));

        // The freshly minted key, rendered once. Deliberately NOT seeded here: registering writes a
        // MeshNode, the workspace stream rebuilds this page, and a seeded empty value would wipe the
        // key the click handler just put there. StartWith supplies the initial blank instead.
        stack = stack.WithView(host.Stream
            .GetDataStream<string>(ResultDataId)
            .StartWith("")
            .Select(markdown => string.IsNullOrEmpty(markdown)
                ? (UiControl)Controls.Stack
                : Controls.Markdown(markdown)));

        return stack.WithView(InstanceList(host, userId));
    }

    private static UiControl RegisterSection(LayoutAreaHost host, string userId, AccessContext? context)
    {
        var form = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: flex-end; flex-wrap: wrap;");

        var dataContext = LayoutAreaReference.GetDataPointer(FormDataId);

        form = form
            .WithView(new TextFieldControl(new JsonPointerReference("instanceId"))
            {
                Label = host.Localize("instances.field.id"),
                Placeholder = host.Localize("instances.field.idPlaceholder"),
                DataContext = dataContext,
            }.WithWidth("200px"))
            .WithView(new TextFieldControl(new JsonPointerReference("displayName"))
            {
                Label = host.Localize("instances.field.name"),
                DataContext = dataContext,
            }.WithWidth("220px"))
            .WithView(new TextFieldControl(new JsonPointerReference("homeUrl"))
            {
                Label = host.Localize("instances.field.url"),
                Placeholder = "https://…",
                DataContext = dataContext,
            }.WithWidth("260px"))
            .WithView(Controls.Button(host.Localize("instances.register"))
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateData(ResultDataId, host.Localize("instances.registering"));
                    // Read the current form values, then register. Subscribe — never await: the
                    // service is a cold observable and the click handler must not block.
                    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(FormDataId)
                        .Take(1)
                        .Subscribe(data => Register(ctx.Host, host, data, userId, context));
                    return Task.CompletedTask;
                }));

        return Controls.Stack
            .WithView(Controls.Title(host.Localize("instances.registerHeading"), 3))
            .WithView(Controls.Markdown(host.Localize("instances.registerHint")))
            .WithView(form)
            .WithStyle("padding: 16px; background: var(--neutral-layer-2); border-radius: 8px; "
                       + "gap: 12px; margin-bottom: 24px;");
    }

    private static void Register(
        LayoutAreaHost target, LayoutAreaHost host,
        Dictionary<string, object?>? data, string userId, AccessContext? context)
    {
        var instanceId = data?.GetValueOrDefault("instanceId")?.ToString()?.Trim() ?? "";
        var displayName = data?.GetValueOrDefault("displayName")?.ToString()?.Trim() ?? "";
        var homeUrl = data?.GetValueOrDefault("homeUrl")?.ToString()?.Trim() ?? "";

        if (!MeshWeaverInstanceService.IsValidInstanceId(instanceId))
        {
            target.UpdateData(ResultDataId, host.Localize("instances.error.invalidId"));
            return;
        }

        var service = host.Hub.ServiceProvider.GetRequiredService<MeshWeaverInstanceService>();
        service.Register(userId, context?.Name ?? "", context?.Email ?? "",
                instanceId, displayName, homeUrl: homeUrl)
            .Subscribe(
                result => target.UpdateData(ResultDataId,
                    // The key is shown ONCE — only its hash is stored, so there is no second chance
                    // to read it. Say so where the user is looking, not in a doc.
                    $"**{host.Localize("instances.keyIssued")}**\n\n"
                    + $"```\n{result.RawKey}\n```\n\n"
                    + host.Localize("instances.keyShownOnce") + "\n\n"
                    + host.Localize("instances.noGrantsYet")),
                ex => target.UpdateData(ResultDataId,
                    $"{host.Localize("instances.error.registerFailed")} {ex.Message}"));
    }

    private static UiControl InstanceList(LayoutAreaHost host, string userId)
    {
        // A live query, not a one-shot read: a registration made above commits a node, the query
        // re-emits, and the list picks it up without any refresh trigger.
        var query = $"nodeType:{MeshWeaver.Graph.Configuration.MeshWeaverInstanceNodeType.NodeType} "
                    + $"namespace:{userId}/{MeshWeaverInstanceService.InstanceNamespace}";

        return Controls.Stack
            .WithView(Controls.Title(host.Localize("instances.yourInstances"), 3))
            .WithView(host.Hub.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>()
                .Query(new MeshWeaver.Mesh.Services.MeshQueryRequest { Query = query, UserId = userId })
                .Select(Rows)
                .Select(rows => rows.Count == 0
                    ? (UiControl)Controls.Markdown(host.Localize("instances.none"))
                    : Grid(host, rows)));
    }

    private static IReadOnlyList<InstanceRow> Rows(IReadOnlyCollection<QueryResult> results) =>
        results.Select(r => new InstanceRow(
                r.Id ?? r.Path.Split('/').Last(),
                r.Name ?? "",
                r.Path))
            .OrderBy(r => r.InstanceId, StringComparer.Ordinal)
            .ToList();

    private static UiControl Grid(LayoutAreaHost host, IReadOnlyList<InstanceRow> rows)
    {
        host.UpdateData(ListDataId, rows);
        return new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(ListDataId)))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(InstanceRow.InstanceId).ToCamelCase() }
                .WithTitle(host.Localize("instances.column.id")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(InstanceRow.DisplayName).ToCamelCase() }
                .WithTitle(host.Localize("instances.column.name")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(InstanceRow.Path).ToCamelCase() }
                .WithTitle(host.Localize("instances.column.path")));
    }

    /// <summary>One row of the instance grid — a plain record so the grid binds it directly.</summary>
    internal record InstanceRow(string InstanceId, string DisplayName, string Path);
}
