using System.Reactive.Linq;
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

    /// <summary>Adds the data-bound composer view; <see cref="ComposerArea"/> is the default area.</summary>
    public static MessageHubConfiguration AddThreadComposerView(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(ComposerArea)
            .WithView(ComposerArea, Composer));

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
