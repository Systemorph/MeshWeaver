using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout areas for MeshDataSource nodes.
/// Provides Thumbnail (with enable/disable, search toggle, install button),
/// Overview, and Copy/Install dialog.
/// </summary>
[Browsable(false)]
public static class MeshDataSourceLayoutAreas
{
    /// <summary>
    /// Registers the MeshDataSource views on the hub configuration.
    /// </summary>
    public static MessageHubConfiguration AddMeshDataSourceViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(MeshNodeLayoutAreas.OverviewArea)
                .WithView(MeshNodeLayoutAreas.OverviewArea, Overview)
                .WithView(MeshNodeLayoutAreas.ThumbnailArea, Thumbnail)
                .WithView(MeshNodeLayoutAreas.CreateNodeArea, CreateLayoutArea.Create)
                .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    /// <summary>
    /// Thumbnail area for data source nodes.
    /// Shows name, description, enable/disable and search toggles, and install button.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        return host.StreamView<MeshNode>(
            (nodes, h) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return BuildThumbnail(h, node, hubPath);
            },
            hubPath);
    }

    private static UiControl BuildThumbnail(LayoutAreaHost host, MeshNode? node, string hubPath)
    {
        var config = GetConfiguration(node);
        var dataId = $"ds_thumb_{hubPath.Replace("/", "_")}";
        host.UpdateData(dataId, new Dictionary<string, object?>
        {
            ["enabled"] = config?.Enabled ?? true,
            ["includeInSearch"] = config?.IncludeInSearch ?? true
        });
        var dataContext = LayoutAreaReference.GetDataPointer(dataId);

        var stack = Controls.Stack.WithWidth("100%")
            .WithStyle("padding: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; gap: 8px;");

        // Header: icon + name
        var header = Controls.Stack.WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 8px;");
        header = header.WithView(Controls.Html(
            $"<div style=\"font-size: 1.1rem; font-weight: 600;\">{Esc(node?.Name ?? hubPath)}</div>"));
        if (config?.ProviderType != null)
        {
            header = header.WithView(Controls.Html(
                $"<span style=\"font-size: 0.75rem; padding: 2px 8px; background: var(--neutral-layer-2); border-radius: 4px;\">{Esc(config.ProviderType)}</span>"));
        }
        stack = stack.WithView(header);

        // Description
        if (!string.IsNullOrEmpty(config?.Description))
        {
            stack = stack.WithView(Controls.Html(
                $"<p style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin: 0;\">{Esc(config.Description)}</p>"));
        }

        // Installed-to note
        if (!string.IsNullOrEmpty(config?.InstalledTo))
        {
            stack = stack.WithView(Controls.Html(
                $"<div style=\"padding: 8px 12px; background: var(--warning-fill-rest); border-radius: 6px; font-size: 0.85rem;\">" +
                $"Installed to <strong>{Esc(config.InstalledTo)}</strong>. Data is served from the destination source." +
                (config.LastSyncedAt.HasValue ? $" Last synced: {config.LastSyncedAt.Value:yyyy-MM-dd HH:mm}" : "") +
                "</div>"));
        }

        // Toggles row
        var toggleRow = Controls.Stack.WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 16px; align-items: center; margin-top: 4px;");

        toggleRow = toggleRow.WithView(new SwitchControl(new JsonPointerReference("enabled"))
        {
            Label = "Enabled",
            DataContext = dataContext
        }.WithCheckedMessage("On").WithUncheckedMessage("Off"));

        toggleRow = toggleRow.WithView(new SwitchControl(new JsonPointerReference("includeInSearch"))
        {
            Label = "Include in Search",
            DataContext = dataContext
        }.WithCheckedMessage("Yes").WithUncheckedMessage("No"));

        stack = stack.WithView(toggleRow);

        // Action buttons row
        var buttonRow = Controls.Stack.WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-top: 8px;");

        buttonRow = buttonRow.WithView(Controls.Button("Copy / Install")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                var dialog = BuildCopyInstallDialog(ctx.Host, node, config);
                ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
                return Task.CompletedTask;
            }));

        buttonRow = buttonRow.WithView(Controls.Button("Export")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(ctx =>
            {
                var dialog = ExportLayoutArea.BuildExportDialog(ctx.Host, node, hubPath);
                ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
                return Task.CompletedTask;
            }));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    /// <summary>
    /// Overview area showing full data source configuration and status.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        return host.StreamView<MeshNode>(
            (nodes, h) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return BuildOverview(h, node, hubPath);
            },
            hubPath);
    }

    private static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node, string hubPath)
    {
        var config = GetConfiguration(node);
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px; gap: 16px;");

        stack = stack.WithView(Controls.H2(node?.Name ?? "Data Source").WithStyle("margin: 0;"));

        if (!string.IsNullOrEmpty(config?.Description))
        {
            stack = stack.WithView(Controls.Html(
                $"<p style=\"color: var(--neutral-foreground-hint);\">{Esc(config.Description)}</p>"));
        }

        // Status section
        var statusSection = Controls.Stack.WithWidth("100%")
            .WithStyle("padding: 16px; background: var(--neutral-layer-2); border-radius: 8px; gap: 8px;");
        statusSection = statusSection.WithView(Controls.Html("<h3 style=\"margin: 0;\">Status</h3>"));

        var statusItems = new List<string>
        {
            $"Provider: <strong>{Esc(config?.ProviderType ?? "Unknown")}</strong>",
            $"Enabled: <strong>{(config?.Enabled == true ? "Yes" : "No")}</strong>",
            $"Search: <strong>{(config?.IncludeInSearch == true ? "Included" : "Excluded")}</strong>"
        };

        if (config?.StorageConfig?.BasePath != null)
            statusItems.Add($"Path: <code>{Esc(config.StorageConfig.BasePath)}</code>");

        if (!string.IsNullOrEmpty(config?.InstalledTo))
            statusItems.Add($"Installed to: <strong>{Esc(config.InstalledTo)}</strong>");

        if (config?.LastSyncedAt.HasValue == true)
            statusItems.Add($"Last synced: {config.LastSyncedAt.Value:yyyy-MM-dd HH:mm}");

        foreach (var item in statusItems)
        {
            statusSection = statusSection.WithView(Controls.Html(
                $"<div style=\"font-size: 0.9rem;\">{item}</div>"));
        }
        stack = stack.WithView(statusSection);

        // Action buttons
        var buttonRow = Controls.Stack.WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-top: 8px;");

        buttonRow = buttonRow.WithView(Controls.Button("Copy / Install")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                var dialog = BuildCopyInstallDialog(ctx.Host, node, config);
                ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
                return Task.CompletedTask;
            }));

        buttonRow = buttonRow.WithView(Controls.Button("Export")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(ctx =>
            {
                var dialog = ExportLayoutArea.BuildExportDialog(ctx.Host, node, hubPath);
                ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
                return Task.CompletedTask;
            }));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    /// <summary>
    /// Builds the Copy/Install dialog.
    /// Core operation: copy node tree from Source to Target with path mapping.
    /// Install = Copy + RemoveMissing + disable source afterward.
    /// </summary>
    internal static UiControl BuildCopyInstallDialog(
        LayoutAreaHost host, MeshNode? sourceNode, MeshDataSourceConfiguration? config)
    {
        var formId = $"copy_install_{Guid.NewGuid().AsString()}";
        host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["sourceType"] = "dataSource",
            ["mode"] = "copy",
            ["targetNamespace"] = "",
            ["force"] = true
        });
        var dataContext = LayoutAreaReference.GetDataPointer(formId);

        var content = Controls.Stack.WithWidth("100%").WithStyle("gap: 16px;");

        // Source type radio
        content = content.WithView(Controls.Stack.WithWidth("100%")
            .WithView(Controls.Body("Source").WithStyle("font-weight: 600; margin-bottom: 4px;"))
            .WithView(new RadioGroupControl(
                new JsonPointerReference("sourceType"),
                new Option<string>[]
                {
                    new("dataSource", "Data Source"),
                    new("file", "Upload File"),
                    new("folder", "Upload Folder (ZIP)")
                },
                nameof(String))
            {
                DataContext = dataContext
            }.WithOrientation(Orientation.Vertical)));

        // Conditional source section
        content = content.WithView<UiControl?>((h, __) =>
            h.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                .Select(data =>
                {
                    var sourceType = data?.GetValueOrDefault("sourceType")?.ToString() ?? "dataSource";
                    var ns = data?.GetValueOrDefault("targetNamespace")?.ToString() ?? "";

                    return sourceType switch
                    {
                        "dataSource" => (UiControl?)Controls.Html(
                            $"<div style=\"padding: 8px 12px; background: var(--neutral-layer-2); border-radius: 6px; font-size: 0.85rem;\">" +
                            $"Source: <strong>{Esc(sourceNode?.Name ?? "Unknown")}</strong>" +
                            (config?.StorageConfig?.BasePath != null ? $" ({Esc(config.StorageConfig.BasePath)})" : "") +
                            "</div>"),
                        "file" => (UiControl?)new NodeImportControl { TargetPath = ns, Mode = "file" },
                        "folder" => (UiControl?)new NodeImportControl { TargetPath = ns, Mode = "folder" },
                        _ => null
                    };
                }));

        // Mode radio: Copy vs Install
        content = content.WithView(Controls.Stack.WithWidth("100%")
            .WithView(Controls.Body("Mode").WithStyle("font-weight: 600; margin-bottom: 4px;"))
            .WithView(new RadioGroupControl(
                new JsonPointerReference("mode"),
                new Option<string>[]
                {
                    new("copy", "Copy (add/update only)"),
                    new("install", "Install (sync — also removes deleted nodes)")
                },
                nameof(String))
            {
                DataContext = dataContext
            }.WithOrientation(Orientation.Vertical)));

        // Target namespace picker
        content = content.WithView(new MeshNodePickerControl(new JsonPointerReference("targetNamespace"))
        {
            Label = "Target Namespace",
            Placeholder = "Root (leave empty for top-level)...",
            DataContext = dataContext
        }.WithQueries("context:create").WithMaxResults(15)
         .WithStyle("width: 100%;"));

        // Force overwrite checkbox
        content = content.WithView(new CheckBoxControl(new JsonPointerReference("force"))
        {
            Label = "Force (overwrite existing nodes)",
            DataContext = dataContext
        });

        // Action button
        content = content.WithView(Controls.Button("Execute")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(async ctx =>
            {
                await ExecuteCopyInstall(ctx, host, formId, sourceNode, config);
            }));

        var dialog = Controls.Dialog(content, $"Copy / Install: {sourceNode?.Name ?? "Data Source"}")
            .WithSize("L")
            .WithClosable(true);

        return dialog;
    }

    private static async Task ExecuteCopyInstall(
        UiActionContext ctx,
        LayoutAreaHost host,
        string formId,
        MeshNode? sourceNode,
        MeshDataSourceConfiguration? config)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>();

        var formValues = await ctx.Host.Stream
            .GetDataStream<Dictionary<string, object?>>(formId).FirstAsync();

        var sourceType = formValues?.GetValueOrDefault("sourceType")?.ToString() ?? "dataSource";
        var mode = formValues?.GetValueOrDefault("mode")?.ToString() ?? "copy";
        var targetNamespace = formValues?.GetValueOrDefault("targetNamespace")?.ToString()?.Trim() ?? "";
        var force = formValues?.GetValueOrDefault("force") is true or "True" or "true";
        var isInstall = mode == "install";

        // Only handle dataSource mode here; file/folder is handled by NodeImportControl
        if (sourceType != "dataSource")
        {
            ShowDialog(ctx, "Info", "File and folder uploads are handled by the upload control above.");
            return;
        }

        if (config?.StorageConfig?.BasePath == null)
        {
            ShowDialog(ctx, "Error", "Source data source has no storage configuration or base path.");
            return;
        }

        try
        {
            var importService = host.Hub.ServiceProvider.GetRequiredService<IMeshImportService>();

            logger?.LogInformation(
                "Copy/Install from {Source} to namespace {Target}, mode={Mode}, force={Force}",
                sourceNode?.Name, targetNamespace, mode, force);

            var result = await importService.ImportNodesAsync(
                config.StorageConfig.BasePath,
                targetRootPath: string.IsNullOrEmpty(targetNamespace) ? null : targetNamespace,
                force: force,
                removeMissing: isInstall);

            logger?.LogInformation("Copy/Install complete: {Nodes} nodes, {Partitions} partitions",
                result.NodesImported, result.PartitionsImported);

            // On Install mode, mark source as disabled
            if (isInstall && sourceNode != null)
            {
                var persistence = host.Hub.ServiceProvider.GetRequiredService<IMeshStorage>();
                var updatedConfig = config with
                {
                    Enabled = false,
                    InstalledTo = string.IsNullOrEmpty(targetNamespace) ? "(root)" : targetNamespace,
                    LastSyncedAt = DateTimeOffset.UtcNow
                };
                var updatedNode = sourceNode with { Content = updatedConfig };
                await persistence.SaveNodeAsync(updatedNode);
            }

            var resultDialog = Controls.Dialog(
                Controls.Markdown(
                    $"**{(isInstall ? "Install" : "Copy")} Complete**\n\n" +
                    $"- Nodes: **{result.NodesImported}** imported, **{result.NodesSkipped}** skipped" +
                    (isInstall ? $", **{result.NodesRemoved}** removed" : "") +
                    $"\n- Partitions: **{result.PartitionsImported}** imported" +
                    $"\n- Duration: {result.Elapsed.TotalSeconds:F1}s"),
                $"{(isInstall ? "Install" : "Copy")} Complete"
            ).WithSize("M").WithClosable(true);
            ctx.Host.UpdateArea(DialogControl.DialogArea, resultDialog);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Copy/Install failed");
            ShowDialog(ctx, "Error",
                ex.Message.Contains("Access denied") || ex.Message.Contains("Unauthorized")
                    ? "You do not have permission to perform this operation."
                    : $"Operation failed: {ex.Message}");
        }
    }

    private static MeshDataSourceConfiguration? GetConfiguration(MeshNode? node)
    {
        if (node?.Content is MeshDataSourceConfiguration config)
            return config;
        return null;
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
