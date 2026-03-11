using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Builds the "Partitions" tab content for the Global Settings page.
/// Lists all Partition nodes from Admin/Partition namespace.
/// </summary>
internal static class PartitionSettingsTab
{
    internal static UiControl BuildPartitionsTab(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2("Partitions").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 16px;\">" +
            "Storage partitions and their namespace mappings. Each organization gets its own partition.</p>"));

        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshService == null)
        {
            stack = stack.WithView(Controls.Html(
                "<p style=\"color: var(--neutral-foreground-hint);\">Mesh service not available.</p>"));
            return stack;
        }

        stack = stack.WithView((h, _) =>
            meshService
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{PartitionNodeType.Namespace} nodeType:{PartitionNodeType.NodeType}"))
                .Select(change =>
                {
                    var nodes = change.Items?.ToList() ?? [];
                    if (nodes.Count == 0)
                        return (UiControl?)Controls.Html(
                            "<p style=\"color: var(--neutral-foreground-hint);\">No partitions registered.</p>");

                    return (UiControl?)BuildPartitionsList(nodes);
                }));

        return stack;
    }

    private static UiControl BuildPartitionsList(List<MeshNode> nodes)
    {
        var container = Controls.Stack.WithWidth("100%").WithStyle("gap: 12px;");

        foreach (var node in nodes.OrderBy(n => n.Name))
        {
            var def = node.Content as PartitionDefinition;
            var basePaths = def?.BasePaths != null && def.BasePaths.Count > 0
                ? string.Join(", ", def.BasePaths)
                : "(none)";

            var card = Controls.Stack.WithWidth("100%")
                .WithStyle("padding: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; gap: 8px;");

            // Header row: name + storage type badge
            var header = Controls.Stack.WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 8px;");
            header = header.WithView(Controls.Html(
                $"<div style=\"font-size: 1.05rem; font-weight: 600;\">{Esc(node.Name ?? node.Path)}</div>"));

            if (!string.IsNullOrEmpty(def?.StorageType))
            {
                header = header.WithView(Controls.Html(
                    $"<span style=\"font-size: 0.75rem; padding: 2px 8px; background: var(--neutral-layer-2); border-radius: 4px;\">{Esc(def.StorageType)}</span>"));
            }

            card = card.WithView(header);

            // Base paths
            card = card.WithView(Controls.Html(
                $"<div style=\"font-size: 0.85rem;\"><strong>Base Paths:</strong> {Esc(basePaths)}</div>"));

            // Description
            if (!string.IsNullOrEmpty(def?.Description))
            {
                card = card.WithView(Controls.Html(
                    $"<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">{Esc(def.Description)}</div>"));
            }

            // Open button
            var capturedPath = node.Path;
            var buttonRow = Controls.Stack.WithOrientation(Orientation.Horizontal)
                .WithStyle("gap: 8px; margin-top: 4px;");
            buttonRow = buttonRow.WithView(Controls.Button("Open")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    var navService = ctx.Host.Hub.ServiceProvider.GetService<INavigationService>();
                    navService?.NavigateTo($"/{capturedPath}");
                    return Task.CompletedTask;
                }));
            card = card.WithView(buttonRow);

            container = container.WithView(card);
        }

        return container;
    }

    private static string Esc(string? text) =>
        System.Web.HttpUtility.HtmlEncode(text ?? "");
}
