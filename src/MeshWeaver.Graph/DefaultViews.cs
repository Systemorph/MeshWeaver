using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Default views that should be available for every node type:
/// - Details: Markdown view showing the properties of the node's content object
/// - Edit: Standard editor using MeshNodeEditorControl
/// </summary>
public static class DefaultViews
{
    public const string EditArea = "Edit";

    /// <summary>
    /// Adds the default views (Details, Edit) to the hub's layout.
    /// Details is set as the default area for empty path requests.
    /// </summary>
    public static MessageHubConfiguration AddDefaultViews(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithView(EditArea, Edit));



    /// <summary>
    /// Renders the Edit area using MeshNodeEditorControl.
    /// Includes a back button to return to Details.
    /// </summary>
    public static IObservable<UiControl> Edit(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence?.GetNodeAsync(nodePath, ct)!;

            var stack = Controls.Stack.WithWidth("100%");

            // Back button
            var detailsHref = $"/{nodePath}/{MeshNodeView.DetailsArea}";
            stack = stack.WithView(
                Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("margin-bottom: 16px;")
                    .WithView(Controls.Button("← Back to Details")
                        .WithClickAction(c => c.Host.UpdateArea(c.Area, new RedirectControl(detailsHref)))));

            // Editor control
            stack = stack.WithView(new MeshNodeEditorControl
            {
                NodePath = nodePath,
                NodeType = node?.NodeType
            });

            return (UiControl)stack;
        });
    }
}
