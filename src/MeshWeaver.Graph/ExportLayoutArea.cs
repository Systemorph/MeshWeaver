using System.ComponentModel;
using System.IO.Compression;
using System.Reactive.Linq;
using System.Text.Json;
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
        if (!perms.HasFlag(Permission.Read))
            return null;
        var label = string.IsNullOrEmpty(nodeName) ? "Export" : $"Export {nodeName}";
        return new(label, MeshNodeLayoutAreas.ExportArea, Order: 26,
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
            $"<p style=\"color: var(--neutral-foreground-hint);\">Export <strong>{Esc(node?.Name ?? nodePath)}</strong> and its subtree as a ZIP archive.</p>"));

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

            var storageAdapter = host.Hub.ServiceProvider.GetRequiredService<IStorageAdapter>();
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };

            // Build ZIP in memory
            using var memoryStream = new MemoryStream();
            using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var nodeCount = await PackNodeTreeAsync(archive, storageAdapter, nodePath, nodePath, jsonOptions, logger);
                logger?.LogInformation("Exported {Count} nodes from {Path}", nodeCount, nodePath);
            }

            memoryStream.Position = 0;

            // Generate filename
            var nodeId = node?.Id ?? nodePath.Split('/').LastOrDefault() ?? "export";
            var fileName = $"{nodeId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip";

            // Save to content collection
            await collection.SaveFileAsync(exportPath, fileName, memoryStream);

            var resultDialog = Controls.Dialog(
                Controls.Markdown(
                    $"**Export Complete**\n\n" +
                    $"Saved to collection **{collectionName}** at `{exportPath}/{fileName}`."),
                "Export Complete"
            ).WithSize("M").WithClosable(true);
            ctx.Host.UpdateArea(DialogControl.DialogArea, resultDialog);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Export failed for {Path}", nodePath);
            ShowDialog(ctx, "Export Failed", $"Export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Recursively packs a node subtree into a ZipArchive.
    /// Follows the same traversal pattern as StorageImporter.ImportRecursivelyAsync.
    /// </summary>
    private static async Task<int> PackNodeTreeAsync(
        ZipArchive archive,
        IStorageAdapter source,
        string? parentPath,
        string rootPath,
        JsonSerializerOptions options,
        ILogger? logger,
        CancellationToken ct = default)
    {
        var nodeCount = 0;
        var (nodePaths, directoryPaths) = await source.ListChildPathsAsync(parentPath, ct);

        foreach (var nodePath in nodePaths)
        {
            try
            {
                var node = await source.ReadAsync(nodePath, options, ct);
                if (node != null)
                {
                    // Calculate relative path from root
                    var relativePath = GetRelativePath(nodePath, rootPath);
                    var entryName = $"{relativePath}.json";

                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await JsonSerializer.SerializeAsync(entryStream, node, options, ct);
                    nodeCount++;

                    // Export partition data
                    var subPaths = await source.ListPartitionSubPathsAsync(nodePath, ct);
                    foreach (var subPath in subPaths)
                    {
                        try
                        {
                            var objects = new List<object>();
                            await foreach (var obj in source.GetPartitionObjectsAsync(nodePath, subPath, options, ct))
                            {
                                objects.Add(obj);
                            }

                            if (objects.Count > 0)
                            {
                                var partitionEntryName = $"{relativePath}/{subPath}.json";
                                var partitionEntry = archive.CreateEntry(partitionEntryName, CompressionLevel.Optimal);
                                await using var partStream = partitionEntry.Open();
                                await JsonSerializer.SerializeAsync(partStream, objects, options, ct);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning(ex, "Failed to export partition {Path}/{SubPath}", nodePath, subPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to export node {Path}", nodePath);
            }

            // Recurse into children
            nodeCount += await PackNodeTreeAsync(archive, source, nodePath, rootPath, options, logger, ct);
        }

        // Recurse into directories that aren't nodes
        foreach (var dirPath in directoryPaths)
        {
            nodeCount += await PackNodeTreeAsync(archive, source, dirPath, rootPath, options, logger, ct);
        }

        return nodeCount;
    }

    private static string GetRelativePath(string nodePath, string rootPath)
    {
        if (string.IsNullOrEmpty(rootPath) || !nodePath.StartsWith(rootPath))
            return nodePath;

        var relative = nodePath[rootPath.Length..].TrimStart('/');
        return string.IsNullOrEmpty(relative) ? nodePath.Split('/').LastOrDefault() ?? nodePath : relative;
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
