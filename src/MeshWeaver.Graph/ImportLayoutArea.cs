using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for importing mesh nodes.
/// Shows a form with destination namespace picker, source type selector
/// (Mesh Node / File / Folder), and the appropriate source input.
/// </summary>
public static class ImportLayoutArea
{
    public static IObservable<UiControl?> ImportMeshNodes(LayoutAreaHost host, RenderingContext _)
    {
        var currentPath = host.Hub.Address.ToString();

        return Observable.FromAsync(async () =>
        {
            var canCreate = await PermissionHelper.CanCreateAsync(host.Hub, currentPath);
            if (!canCreate)
            {
                return (UiControl?)Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;")
                    .WithView(Controls.H2("Access Denied").WithStyle("margin: 0 0 16px 0;"))
                    .WithView(Controls.Html(
                        "<p style=\"color: var(--neutral-foreground-hint);\">You do not have permission to import nodes here.</p>"));
            }

            return (UiControl?)BuildImportForm(host, currentPath);
        });
    }

    private static UiControl BuildImportForm(LayoutAreaHost host, string currentPath)
    {
        var formId = $"import_form_{Guid.NewGuid().AsString()}";
        host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["namespace"] = currentPath,
            ["source"] = "meshNode",
            ["sourceNode"] = "",
            ["force"] = false
        });
        var dataContext = LayoutAreaReference.GetDataPointer(formId);

        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        stack = stack.WithView(Controls.H2("Import").WithStyle("margin: 0 0 24px 0;"));

        // Destination namespace picker
        stack = stack.WithView(new MeshNodePickerControl(new JsonPointerReference("namespace"))
        {
            Label = "Destination Namespace",
            Placeholder = "Root (leave empty for top-level)...",
            DataContext = dataContext
        }.WithQueries("context:create").WithMaxResults(15)
         .WithStyle("width: 100%; margin-bottom: 16px;"));

        // Source type radio group
        stack = stack.WithView(Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-bottom: 16px;")
            .WithView(Controls.Body("Source").WithStyle("font-weight: 600; margin-bottom: 4px;"))
            .WithView(new RadioGroupControl(
                new JsonPointerReference("source"),
                new Option<string>[]
                {
                    new("meshNode", "Copy from Mesh Node"),
                    new("file", "Upload File"),
                    new("folder", "Upload Folder (ZIP)")
                },
                nameof(String))
            {
                DataContext = dataContext
            }.WithOrientation(Orientation.Vertical)));

        // Conditional source section — reactive based on "source" field
        stack = stack.WithView<UiControl?>((h, __) =>
            h.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                .Select(data =>
                {
                    var sourceType = data?.GetValueOrDefault("source")?.ToString() ?? "meshNode";
                    var ns = data?.GetValueOrDefault("namespace")?.ToString() ?? currentPath;

                    return sourceType switch
                    {
                        "meshNode" => BuildMeshNodeSource(host, formId, dataContext, currentPath),
                        "file" => (UiControl?)new NodeImportControl { TargetPath = ns, Mode = "file" },
                        "folder" => (UiControl?)new NodeImportControl { TargetPath = ns, Mode = "folder" },
                        _ => null
                    };
                }));

        // Cancel button
        var cancelUrl = MeshNodeLayoutAreas.BuildContentUrl(currentPath, MeshNodeLayoutAreas.OverviewArea);
        stack = stack.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithNavigateToHref(cancelUrl)
            .WithStyle("margin-top: 24px;"));

        return stack;
    }

    /// <summary>
    /// Builds the "Copy from Mesh Node" source section:
    /// source node picker, force checkbox, and Import button.
    /// </summary>
    private static UiControl BuildMeshNodeSource(
        LayoutAreaHost host, string formId, string dataContext, string currentPath)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>();
        var stack = Controls.Stack.WithWidth("100%");

        // Source node picker
        stack = stack.WithView(new MeshNodePickerControl(new JsonPointerReference("sourceNode"))
        {
            Label = "Source Node",
            Placeholder = "Select a node to copy...",
            Required = true,
            DataContext = dataContext
        }.WithMaxResults(15).WithStyle("width: 100%; margin-bottom: 16px;"));

        // Force overwrite checkbox
        stack = stack.WithView(new CheckBoxControl(new JsonPointerReference("force"))
        {
            Label = "Force (overwrite existing nodes)",
            DataContext = dataContext
        }.WithStyle("margin-bottom: 16px;"));

        // Import button
        stack = stack.WithView(Controls.Button("Import")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.ArrowImport())
            .WithClickAction(async actx =>
            {
                var formValues = await actx.Host.Stream
                    .GetDataStream<Dictionary<string, object?>>(formId).FirstAsync();

                var targetNs = formValues.GetValueOrDefault("namespace")?.ToString()?.Trim() ?? "";
                var sourceNode = formValues.GetValueOrDefault("sourceNode")?.ToString()?.Trim();
                var force = formValues.GetValueOrDefault("force") is true or "True" or "true";

                if (string.IsNullOrWhiteSpace(sourceNode))
                {
                    ShowErrorDialog(actx, "Validation Error", "Please select a source node.");
                    return;
                }

                try
                {
                    var persistence = host.Hub.ServiceProvider.GetRequiredService<IPersistenceService>();
                    logger?.LogInformation(
                        "Copying node tree from {Source} to namespace {Target}, force={Force}",
                        sourceNode, targetNs, force);

                    var nodesCopied = await NodeCopyHelper.CopyNodeTreeAsync(
                        persistence, sourceNode, targetNs, force, logger);

                    logger?.LogInformation("Import complete: {Count} nodes copied", nodesCopied);

                    // Show success dialog, then navigate to destination
                    var successDialog = Controls.Dialog(
                        Controls.Markdown($"**Import Complete**\n\nCopied **{nodesCopied}** node(s) to `{(string.IsNullOrEmpty(targetNs) ? "root" : targetNs)}`."),
                        "Import Complete"
                    ).WithSize("M").WithClosable(true).WithCloseAction(ctx =>
                    {
                        var overviewUrl = string.IsNullOrEmpty(targetNs)
                            ? MeshNodeLayoutAreas.BuildContentUrl(sourceNode.Split('/').Last(), MeshNodeLayoutAreas.OverviewArea)
                            : MeshNodeLayoutAreas.BuildContentUrl(targetNs, MeshNodeLayoutAreas.OverviewArea);
                        actx.NavigateTo(overviewUrl);
                        return Task.CompletedTask;
                    });
                    actx.Host.UpdateArea(DialogControl.DialogArea, successDialog);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Import failed for {Source} -> {Target}", sourceNode, targetNs);
                    var errorMsg = ex.Message.Contains("Access denied") || ex.Message.Contains("Unauthorized")
                        ? "You do not have permission to import nodes here."
                        : $"Import failed: {ex.Message}";
                    ShowErrorDialog(actx, "Import Failed", errorMsg);
                }
            }));

        return stack;
    }

    private static void ShowErrorDialog(UiActionContext ctx, string title, string message)
    {
        var errorDialog = Controls.Dialog(
            Controls.Markdown($"**{title}:**\n\n{message}"),
            title
        ).WithSize("M").WithClosable(true);
        ctx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
    }
}
