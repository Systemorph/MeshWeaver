using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// The per-user <see cref="ThreadComposer"/> node's default layout area — the data-bound chat composer.
/// Set up the SAME standard way as every other node layout area (Overview / Edit): the framework's
/// <see cref="EditLayoutArea.BuildPropertyForm"/> renders the bound controls off the
/// <see cref="ThreadComposer"/> content (message editor + agent/model/harness <c>MeshNodePicker</c>s,
/// per the property attributes) and <see cref="OverviewLayoutArea.SetupAutoSave"/> persists every edit
/// to the node. A Send button submits the typed message via the canonical
/// <see cref="HubThreadExtensions.StartThread"/>.
/// </summary>
public static class ThreadComposerView
{
    /// <summary>The composer area name — registered as the node's default area.</summary>
    public const string ComposerArea = "Composer";

    /// <summary>
    /// The selectors-only area: just the harness/agent/model <c>[MeshNode]</c> pickers, data-bound to
    /// THIS node and auto-persisting — the SAME standard wiring as <see cref="ComposerArea"/> but without
    /// the message editor / Send button. The Blazor chat (<c>ThreadChatView</c>) embeds this so its
    /// harness/agent/model selection is 100% data-bound (no hand-rolled dropdowns) while keeping its own
    /// Monaco editor + attachments.
    /// </summary>
    public const string SelectorsArea = "Selectors";

    /// <summary>Adds the data-bound composer + selectors views; <see cref="ComposerArea"/> is the default area.</summary>
    public static MessageHubConfiguration AddThreadComposerView(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(ComposerArea)
            .WithView(ComposerArea, Composer)
            .WithView(SelectorsArea, ComposerSelectors));

    /// <summary>The composer content properties shown in <see cref="SelectorsArea"/>, in display order.</summary>
    private static readonly string[] SelectorPropertyNames =
        [nameof(ThreadComposer.Harness), nameof(ThreadComposer.AgentName), nameof(ThreadComposer.ModelName)];

    /// <summary>
    /// Renders just the harness/agent/model pickers, data-bound + auto-persisting against THIS node —
    /// the standard <c>EditNode</c> wiring (<see cref="LayoutAreaHost.UpdateData"/> +
    /// <see cref="OverviewLayoutArea.SetupAutoSave"/>) with the framework's per-property control mapper
    /// (<see cref="EditorExtensions.MapToToggleableControl"/>) over the three selector properties only.
    /// </summary>
    public static UiControl ComposerSelectors(LayoutAreaHost host, RenderingContext context)
        => Controls.Stack.WithWidth("100%")
            .WithView((h, _) => h.Workspace.GetMeshNodeStream()
                .Select(node =>
                {
                    if (node?.Content is null)
                        return (UiControl?)Controls.Stack;

                    var instance = node.Content;
                    if (instance is JsonElement je)
                        instance = JsonSerializer.Deserialize<object>(je.GetRawText(), h.Hub.JsonSerializerOptions)!;

                    var nodePath = node.Path ?? string.Empty;
                    var dataId = EditLayoutArea.GetDataId(nodePath);
                    h.UpdateData(dataId, instance);
                    OverviewLayoutArea.SetupAutoSave(h, dataId, instance, node);

                    // Horizontal row sized to share its width across the 3 pickers (flex: 1 1 0), kept
                    // on ONE line (nowrap) and bottom-aligned, so the chat footer fits pickers +
                    // attachment chips + Send on a single bottom row. Each picker shrinks rather than
                    // wrapping; min-width keeps them usable.
                    var row = Controls.Stack
                        .WithOrientation(Orientation.Horizontal)
                        .WithStyle("gap: 6px; flex-wrap: nowrap; align-items: flex-end; width: 100%;");
                    foreach (var prop in SelectorProperties(instance.GetType()))
                        row = row.WithView(
                            Controls.Stack.WithStyle("flex: 1 1 0; min-width: 70px; max-width: 220px;")
                                .WithView(h.Hub.ServiceProvider.MapToToggleableControl(prop, dataId, canEdit: true, h, isToggleable: false)));
                    return (UiControl?)row;
                }));

    private static IEnumerable<PropertyInfo> SelectorProperties(Type contentType) =>
        SelectorPropertyNames
            .Select(contentType.GetProperty)
            .Where(p => p is not null)!;

    /// <summary>Renders the standard data-bound + auto-persisting editor for THIS node + a Send button.</summary>
    public static UiControl Composer(LayoutAreaHost host, RenderingContext context)
    {
        var access = host.Hub.ServiceProvider.GetService<AccessService>();
        var user = access?.CircuitContext?.ObjectId ?? access?.Context?.ObjectId ?? "";

        return Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;")
            .WithView((h, _) => h.Workspace.GetMeshNodeStream()
                .Select(node =>
                {
                    if (node?.Content is null)
                        return (UiControl?)Controls.Stack;

                    var instance = node.Content;
                    if (instance is JsonElement je)
                        instance = JsonSerializer.Deserialize<object>(je.GetRawText(), h.Hub.JsonSerializerOptions)!;

                    // Standard node-edit wiring — identical to MeshNodeLayoutAreas.EditNode.
                    var nodePath = node.Path ?? string.Empty;
                    var dataId = EditLayoutArea.GetDataId(nodePath);
                    h.UpdateData(dataId, instance);
                    OverviewLayoutArea.SetupAutoSave(h, dataId, instance, node);

                    var form = EditLayoutArea.BuildPropertyForm(
                        h, instance.GetType(), dataId, canEdit: true, isToggleable: false);

                    return (UiControl?)Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;")
                        .WithView(form)
                        .WithView(BuildSendButton(dataId, user));
                }));
    }

    /// <summary>
    /// Send button — reads the current form data (standard one-shot click read) and submits the typed
    /// message to a new thread via <see cref="HubThreadExtensions.StartThread"/>. The model/agent/harness
    /// are passed as the picked node PATHS; downstream loads the node.
    /// </summary>
    private static UiControl BuildSendButton(string dataId, string user)
        => Controls.Button("Send")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                ctx.Host.Stream.GetDataStream<ThreadComposer>(dataId)
                    .Take(1)
                    .Subscribe(edited =>
                    {
                        if (!string.IsNullOrWhiteSpace(edited?.MessageContent))
                            ctx.Host.Hub.StartThread(
                                namespacePath: user,
                                userText: edited.MessageContent!,
                                agentName: edited.AgentName,
                                modelName: edited.ModelName,
                                harness: edited.Harness,
                                createdBy: user);
                    });
                return Task.CompletedTask;
            });
}
