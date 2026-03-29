using System.ComponentModel;
using System.IO.Compression;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for exporting mesh node subtrees as ZIP archives
/// stored in a user-selected content collection.
/// Uses file persister formats (.md, .cs, .json) for round-trip compatibility with import.
/// </summary>
[Browsable(false)]
public static class ExportLayoutArea
{
    public const string ExportArea = "Export";

    /// <summary>
    /// Returns the Export menu item if the user has Read permission.
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, string? nodeName, Permission perms)
    {
        if (!perms.HasFlag(Permission.Export))
            return null;
        var label = string.IsNullOrEmpty(nodeName) ? "Export" : $"Export {nodeName}";
        return new(label, MeshNodeLayoutAreas.ExportArea,
            RequiredPermission: Permission.Export, Order: 26,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.ExportArea));
    }

    /// <summary>
    /// Layout area handler for the Export action.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Export(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        return host.StreamView<MeshNode>(
            (nodes, h) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return BuildExportDialog(h, node, hubPath);
            },
            hubPath);
    }

    /// <summary>
    /// Builds the export dialog. Can be called from data source thumbnail or node menu.
    /// </summary>
    internal static UiControl BuildExportDialog(LayoutAreaHost host, MeshNode? node, string nodePath)
    {
        var formId = $"export_{Guid.NewGuid().AsString()}";

        // Get available writable content collections
        var contentService = host.Hub.ServiceProvider.GetService<IContentService>();
        var collections = contentService?.GetAllCollectionConfigs()
            .Where(c => c.IsEditable)
            .ToList() ?? [];

        var optionsDataId = $"{formId}_options";
        var options = collections
            .Select(c => (Option)new Option<string>(c.Name, c.DisplayName ?? c.Name))
            .ToArray();

        host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["collection"] = collections.FirstOrDefault()?.Name ?? "",
            ["exportPath"] = "exports"
        });
        host.UpdateData(optionsDataId, options);

        var dataContext = LayoutAreaReference.GetDataPointer(formId);

        var content = Controls.Stack.WithWidth("100%").WithStyle("gap: 16px;");

        content = content.WithView(Controls.Html(
            $"<p style=\"color: var(--neutral-foreground-hint);\">Export <strong>{Esc(node?.Name ?? nodePath)}</strong> and its subtree as a ZIP archive using native file formats (.md, .cs, .json).</p>"));

        // Content collection combobox
        if (collections.Count == 0)
        {
            content = content.WithView(Controls.Html(
                "<p style=\"color: var(--warning-color);\">No writable content collections available.</p>"));
        }
        else
        {
            content = content.WithView(new ComboboxControl(
                new JsonPointerReference("collection"),
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsDataId)))
            {
                Label = "Destination Collection",
                DataContext = dataContext,
                Autocomplete = ComboboxAutocomplete.Both
            }.WithStyle("width: 100%;"));
        }

        // Export path
        content = content.WithView(new TextFieldControl(new JsonPointerReference("exportPath"))
        {
            Label = "Export Folder",
            Placeholder = "e.g. exports",
            DataContext = dataContext
        }.WithStyle("width: 100%;"));

        // Export button
        content = content.WithView(Controls.Button("Export")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.ArrowExport())
            .WithDisabled(collections.Count == 0)
            .WithClickAction(async ctx =>
            {
                await ExecuteExport(ctx, host, formId, node, nodePath);
            }));

        var dialog = Controls.Dialog(content, $"Export: {node?.Name ?? nodePath}")
            .WithSize("M")
            .WithClosable(true);

        return dialog;
    }

    private static async Task ExecuteExport(
        UiActionContext ctx,
        LayoutAreaHost host,
        string formId,
        MeshNode? node,
        string nodePath)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>();

        var formValues = await ctx.Host.Stream
            .GetDataStream<Dictionary<string, object?>>(formId).FirstAsync();

        var collectionName = formValues?.GetValueOrDefault("collection")?.ToString()?.Trim() ?? "";
        var exportPath = formValues?.GetValueOrDefault("exportPath")?.ToString()?.Trim() ?? "exports";

        if (string.IsNullOrEmpty(collectionName))
        {
            ShowDialog(ctx, "Validation Error", "Please select a content collection.");
            return;
        }

        try
        {
            var contentService = host.Hub.ServiceProvider.GetRequiredService<IContentService>();
            var collection = await contentService.GetCollectionAsync(collectionName);
            if (collection == null)
            {
                ShowDialog(ctx, "Error", $"Content collection '{collectionName}' not found.");
                return;
            }

            var exportService = host.Hub.ServiceProvider.GetRequiredService<IMeshExportService>();

            // Export to temp directory using file persister formats
            var tempDir = Path.Combine(Path.GetTempPath(), $"meshexport_{Guid.NewGuid():N}");
            try
            {
                var result = await exportService.ExportToDirectoryAsync(nodePath, tempDir);
                if (!result.Success)
                {
                    ShowDialog(ctx, "Export Failed", result.Error!);
                    return;
                }

                // ZIP the temp directory
                using var memoryStream = new MemoryStream();
                ZipFile.CreateFromDirectory(tempDir, memoryStream, CompressionLevel.Optimal, includeBaseDirectory: false);
                memoryStream.Position = 0;

                // Generate filename
                var nodeId = node?.Id ?? nodePath.Split('/').LastOrDefault() ?? "export";
                var fileName = $"{nodeId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip";

                // Save to content collection
                await collection.SaveFileAsync(exportPath, fileName, memoryStream);

                logger?.LogInformation("Exported {Count} nodes from {Path} to {Collection}/{ExportPath}/{FileName}",
                    result.NodesExported, nodePath, collectionName, exportPath, fileName);

                var resultDialog = Controls.Dialog(
                    Controls.Markdown(
                        $"**Export Complete**\n\n" +
                        $"Exported **{result.NodesExported}** node(s) to collection **{collectionName}** at `{exportPath}/{fileName}`."),
                    "Export Complete"
                ).WithSize("M").WithClosable(true);
                ctx.Host.UpdateArea(DialogControl.DialogArea, resultDialog);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Export failed for {Path}", nodePath);
            ShowDialog(ctx, "Export Failed", $"Export failed: {ex.Message}");
        }
    }

    private static void ShowDialog(UiActionContext ctx, string title, string message)
    {
        var dialog = Controls.Dialog(
            Controls.Markdown($"**{title}:**\n\n{message}"),
            title
        ).WithSize("M").WithClosable(true);
        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
    }

    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);
}
