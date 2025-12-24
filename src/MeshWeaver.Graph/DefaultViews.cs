using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
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
    public const string DetailsArea = "Details";
    public const string EditArea = "Edit";

    /// <summary>
    /// Adds the default views (Details, Edit) to the hub's layout.
    /// Details is set as the default area for empty path requests.
    /// </summary>
    public static MessageHubConfiguration AddDefaultViews(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(DetailsArea)
            .WithView(DetailsArea, Details)
            .WithView(EditArea, Edit));

    /// <summary>
    /// Renders the Details area showing the node's content properties as markdown.
    /// Displays all public properties of the content object in a readable format.
    /// </summary>
    public static IObservable<UiControl> Details(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        if (persistence == null)
        {
            return Observable.Return(Controls.Stack
                .WithWidth("100%")
                .WithView(Controls.Html($"<h2>{hubPath}</h2>"))
                .WithView(Controls.Html("<p>Persistence service not available.</p>")));
        }

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            return BuildDetailsContent(host, node);
        });
    }

    private static UiControl BuildDetailsContent(LayoutAreaHost host, MeshNode? node)
    {
        var nodePath = node?.Prefix ?? host.Hub.Address.ToString();
        var stack = Controls.Stack.WithWidth("100%");

        if (node == null)
        {
            stack = stack.WithView(Controls.Html($"<h2>{nodePath}</h2>"));
            stack = stack.WithView(Controls.Html("<p><em>Node not found.</em></p>"));
            return stack;
        }

        // Header with title and edit button
        var title = node.Name ?? node.Id;
        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 16px;")
            .WithView(Controls.Html($"<h1 style=\"margin: 0;\">{System.Web.HttpUtility.HtmlEncode(title)}</h1>"))
            .WithView(Controls.Button("Edit")
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area,
                    new RedirectControl($"/{nodePath}/{EditArea}"))));

        stack = stack.WithView(headerStack);

        // Build markdown content from node properties
        var markdown = BuildPropertiesMarkdown(node);
        stack = stack.WithView(new MarkdownControl(markdown));

        return stack;
    }

    private static string BuildPropertiesMarkdown(MeshNode node)
    {
        var sb = new StringBuilder();

        // Node metadata section
        if (!string.IsNullOrWhiteSpace(node.Description))
        {
            sb.AppendLine(node.Description);
            sb.AppendLine();
        }

        // Content properties section
        if (node.Content != null)
        {
            sb.AppendLine("## Properties");
            sb.AppendLine();

            var contentType = node.Content.GetType();
            var properties = contentType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead)
                .OrderBy(p => p.Name);

            foreach (var prop in properties)
            {
                try
                {
                    var value = prop.GetValue(node.Content);
                    var displayValue = FormatPropertyValue(value);

                    if (!string.IsNullOrEmpty(displayValue))
                    {
                        sb.AppendLine($"**{prop.Name}:** {displayValue}");
                        sb.AppendLine();
                    }
                }
                catch
                {
                    // Skip properties that throw on access
                }
            }
        }

        // Node info section
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Node Info");
        sb.AppendLine();
        sb.AppendLine($"**Path:** `{node.Path}`");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(node.NodeType))
        {
            sb.AppendLine($"**Type:** `{node.NodeType}`");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(node.IconName))
        {
            sb.AppendLine($"**Icon:** {node.IconName}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatPropertyValue(object? value)
    {
        if (value == null)
            return string.Empty;

        return value switch
        {
            string s when string.IsNullOrWhiteSpace(s) => string.Empty,
            string s when s.StartsWith("http://") || s.StartsWith("https://") => $"[{s}]({s})",
            string s => System.Web.HttpUtility.HtmlEncode(s),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss"),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
            bool b => b ? "Yes" : "No",
            _ => System.Web.HttpUtility.HtmlEncode(value.ToString() ?? string.Empty)
        };
    }

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
            var detailsHref = $"/{nodePath}/{DetailsArea}";
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
