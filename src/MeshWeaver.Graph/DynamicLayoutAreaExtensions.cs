using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Extensions for dynamically registering type-specific layout areas based on NodeTypeConfigurations.
/// For example, a project hub would have a "Stories" area that shows all story children.
/// </summary>
public static class DynamicLayoutAreaExtensions
{
    /// <summary>
    /// Adds dynamic type-specific layout areas based on the registered NodeTypeConfigurations.
    /// Creates areas like "Stories", "Projects", "Persons" that filter children by node type.
    /// </summary>
    public static MessageHubConfiguration AddDynamicNodeTypeAreas(this MessageHubConfiguration configuration)
    {
        return configuration.AddLayout(layout =>
        {
            var meshCatalog = layout.Hub.ServiceProvider.GetService<IMeshCatalog>();
            if (meshCatalog == null)
                return layout;

            // Get all registered node types
            var nodeTypes = meshCatalog.GetNodeTypes();

            foreach (var nodeType in nodeTypes)
            {
                // Skip the root "graph" type - it doesn't need its own catalog area
                if (string.Equals(nodeType.NodeType, "graph", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Create area name: "story" -> "Stories", "org" -> "Orgs"
                var areaName = GetPluralAreaName(nodeType.DisplayName ?? nodeType.NodeType);

                // Register the layout area with a handler that filters by this type
                var capturedNodeType = nodeType.NodeType; // Capture for closure
                layout = layout.WithView(areaName, (host, ctx) =>
                    CreateFilteredCatalog(host, ctx, capturedNodeType, areaName));
            }

            return layout;
        });
    }

    /// <summary>
    /// Creates a filtered catalog view showing only children of the specified node type.
    /// </summary>
    private static IObservable<UiControl> CreateFilteredCatalog(
        LayoutAreaHost host,
        RenderingContext ctx,
        string nodeType,
        string displayName)
    {
        var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
        var parentPath = host.Hub.Address.ToString();

        if (meshCatalog == null)
        {
            return Observable.Return(Controls.Stack
                .WithWidth("100%")
                .WithView(Controls.Html($"<h2>{displayName}</h2>"))
                .WithView(Controls.Html("<p>Mesh catalog not available.</p>")));
        }

        return Observable.FromAsync(async ct =>
        {
            var children = await meshCatalog.Persistence.GetChildrenAsync(parentPath, ct);

            // Filter by node type
            var filtered = children.Where(n =>
                string.Equals(n.NodeType, nodeType, StringComparison.OrdinalIgnoreCase));

            return BuildTypeCatalogView(host, parentPath, displayName, filtered);
        });
    }

    private static UiControl BuildTypeCatalogView(
        LayoutAreaHost host,
        string parentPath,
        string displayName,
        IEnumerable<MeshNode> children)
    {
        var stack = Controls.Stack.WithWidth("100%");
        var childList = children.ToList();

        // Title with count
        stack = stack.WithView(Controls.Html($"<h2>{displayName} ({childList.Count})</h2>"));

        if (childList.Count == 0)
        {
            stack = stack.WithView(Controls.Html("<p>No items found.</p>"));
            return stack;
        }

        // Create a responsive grid layout with thumbnails
        var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));

        foreach (var child in childList)
        {
            grid = grid.WithView(
                MeshNodeThumbnailControl.FromNode(child, child.Prefix),
                itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(3));
        }

        stack = stack.WithView(grid);

        return stack;
    }

    /// <summary>
    /// Gets the plural form of a node type name for the area name.
    /// "Story" -> "Stories", "Person" -> "Persons", "Org" -> "Orgs"
    /// </summary>
    private static string GetPluralAreaName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "Items";

        // Capitalize first letter
        var displayName = char.ToUpper(name[0]) + name.Substring(1);

        // Handle common pluralization patterns
        if (displayName.EndsWith("y", StringComparison.OrdinalIgnoreCase) &&
            !displayName.EndsWith("ey", StringComparison.OrdinalIgnoreCase) &&
            !displayName.EndsWith("ay", StringComparison.OrdinalIgnoreCase) &&
            !displayName.EndsWith("oy", StringComparison.OrdinalIgnoreCase) &&
            !displayName.EndsWith("uy", StringComparison.OrdinalIgnoreCase))
        {
            return displayName[..^1] + "ies";
        }

        if (displayName.EndsWith("s", StringComparison.OrdinalIgnoreCase) ||
            displayName.EndsWith("x", StringComparison.OrdinalIgnoreCase) ||
            displayName.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ||
            displayName.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
        {
            return displayName + "es";
        }

        return displayName + "s";
    }
}
