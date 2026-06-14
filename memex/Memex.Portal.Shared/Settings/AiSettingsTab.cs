using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Settings tab that edits the per-user <see cref="AiSettings"/> node (<c>{user}/_Memex/AiSettings</c>):
/// enabled harnesses + the agent/model picker query templates.
///
/// <para><b>100% data-bound via the framework, set up the SAME standard way as every node editor</b>
/// (<see cref="MeshNodeLayoutAreas"/>' <c>EditNode</c>): the auto-generated property form
/// (<see cref="EditLayoutArea.BuildPropertyForm"/>) binds DIRECTLY to the node stream via a node-bound
/// DataContext — each edit writes straight back to the node (ONE source of truth, no <c>/data</c> replica,
/// no save subscription). No hand-rolled <c>Edit</c>-macro callback, no <c>.Take(1)</c>, no Save button.</para>
/// </summary>
public static class AiSettingsTab
{
    public const string TabId = "AiSettings";

    public static MessageHubConfiguration AddAiSettingsTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(
            new SettingsMenuItemDefinition(
                Id: TabId,
                Label: "AI Settings",
                ContentBuilder: BuildContent,
                Group: "AI",
                Icon: FluentIcons.Sparkle(),
                GroupIcon: FluentIcons.Sparkle(),
                Order: 210,
                RequiredPermission: Permission.Read));

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var userId = accessService?.Context?.ObjectId ?? "";

        stack = stack.WithView(Controls.H2("AI Settings").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size:0.85rem; color:var(--neutral-foreground-hint); margin-bottom:16px;\">" +
            "Choose which AI harnesses appear in your chat composer, and customise the agent/model " +
            "picker queries (one query per row). Changes save automatically.</p>"));

        if (string.IsNullOrEmpty(userId))
            return stack.WithView(Controls.Html(
                "<p style=\"color:var(--neutral-foreground-hint);\">No user identity available.</p>"));

        var path = AiSettingsNodeType.PathFor(userId);

        // Robust: create the node with defaults if it doesn't exist (existing + new users).
        AiSettingsNodeType.EnsureExists(host.Hub, host.Hub.ServiceProvider, userId);

        // Standard node editor — identical wiring to MeshNodeLayoutAreas.EditNode: the property form
        // binds DIRECTLY to the node's Content (node-bound DataContext) so each edit writes straight
        // back to the node stream. No /data replica, no save subscription. The first emission only
        // supplies the content TYPE (to generate the form); a one-way /data projection keeps the
        // derived-label read views (dimension/options/date) correct from the Layout layer.
        var dataId = EditLayoutArea.GetDataId(path);
        var boundContext = LayoutAreaReference.GetMeshNodeDataContext(path, bindContent: true);
        host.RegisterForDisposal($"aisettings-content-projection_{dataId}",
            host.Workspace.GetMeshNodeStream(path)
                .Select(n => n?.Content)
                .Where(c => c is not null)
                .Subscribe(content => host.UpdateData(dataId, content!)));

        return stack.WithView((h, _) => h.Workspace.GetMeshNodeStream(path)
            .Where(n => n?.Content is not null)
            .Select(n =>
            {
                var instance = n!.Content!;
                if (instance is JsonElement je)
                    instance = JsonSerializer.Deserialize<object>(je.GetRawText(), h.Hub.JsonSerializerOptions)!;

                return (UiControl?)EditLayoutArea.BuildPropertyForm(
                    h, instance.GetType(), dataId, canEdit: true, isToggleable: false, boundDataContext: boundContext);
            })
            .DistinctUntilChanged(n => n?.GetType()));
    }
}
