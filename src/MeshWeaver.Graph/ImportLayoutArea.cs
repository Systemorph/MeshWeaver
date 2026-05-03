using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for importing mesh nodes.
/// Shows a form with destination namespace picker, source type selector
/// (Mesh Node / File / Folder), and the appropriate source input.
/// </summary>
[Browsable(false)]
public static class ImportLayoutArea
{
    /// <summary>
    /// Returns the Import menu item if the user has Create permission.
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Create))
            return null;
        return new("Import", MeshNodeLayoutAreas.ImportMeshNodesArea,
            RequiredPermission: Permission.Create, Order: 1,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.ImportMeshNodesArea));
    }
    /// <summary>
    /// Layout area for importing mesh nodes.
    /// Shows a form with destination namespace picker, source type selector
    /// (Mesh Node / File / Folder), and the appropriate source input.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> ImportMeshNodes(LayoutAreaHost host, RenderingContext _)
    {
        var currentPath = host.Hub.Address.ToString();

        return PermissionHelper.CanCreate(host.Hub, currentPath)
            .Select(canCreate => canCreate
                ? (UiControl?)BuildImportForm(host, currentPath)
                : (UiControl?)Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;")
                    .WithView(Controls.H2("Access Denied").WithStyle("margin: 0 0 16px 0;"))
                    .WithView(Controls.Html(
                        "<p style=\"color: var(--neutral-foreground-hint);\">You do not have permission to import nodes here.</p>")));
    }

    private static UiControl BuildImportForm(LayoutAreaHost host, string currentPath)
    {
        var formId = $"import_form_{Guid.NewGuid().AsString()}";
        host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["namespace"] = currentPath,
            ["source"] = "meshNode",
            ["sourceNode"] = "",
            ["force"] = false,
            // Remote-instance tab fields — empty defaults; revealed when source=="remote".
            ["remoteUrl"] = "",
            ["remoteToken"] = "",
            ["remoteSourcePath"] = currentPath,
            ["remoteTargetPath"] = "",
            ["direction"] = "push",
            ["dryRun"] = true,
            ["removeMissing"] = false,
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
                    new("zip", "Upload ZIP"),
                    new("remote", "Mirror from / to another instance")
                },
                nameof(String))
            {
                DataContext = dataContext
            }.WithOrientation(Orientation.Vertical)));

        // Conditional source section — reactive based on "source" field only.
        // DistinctUntilChanged on the source type string prevents spurious re-renders
        // when other form fields (namespace, sourceNode) change.
        stack = stack.WithView<UiControl?>((h, __) =>
            h.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                .Select(data => (
                    source: data?.GetValueOrDefault("source")?.ToString() ?? "meshNode",
                    ns: data?.GetValueOrDefault("namespace")?.ToString() ?? currentPath
                ))
                .DistinctUntilChanged(x => x.source)
                .Select(x => x.source switch
                {
                    "meshNode" => BuildMeshNodeSource(host, formId, dataContext, currentPath),
                    "file" => (UiControl?)new NodeImportControl { TargetPath = x.ns, Mode = "file" },
                    "zip" => (UiControl?)new NodeImportControl { TargetPath = x.ns, Mode = "zip" },
                    "remote" => BuildRemoteSource(host, formId, dataContext),
                    _ => null
                }));

        // Cancel button
        var cancelUrl = MeshNodeLayoutAreas.BuildUrl(currentPath, MeshNodeLayoutAreas.OverviewArea);
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
            .WithClickAction(actx =>
            {
                actx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                    .Take(1)
                    .Subscribe(formValues =>
                    {
                        var targetNs = formValues.GetValueOrDefault("namespace")?.ToString()?.Trim() ?? "";
                        var sourceNode = formValues.GetValueOrDefault("sourceNode")?.ToString()?.Trim();
                        var force = formValues.GetValueOrDefault("force") is true or "True" or "true";

                        if (string.IsNullOrWhiteSpace(sourceNode))
                        {
                            ShowErrorDialog(actx, "Validation Error", "Please select a source node.");
                            return;
                        }

                        var meshQuery = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
                        var nodeFactory = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
                        logger?.LogInformation(
                            "Copying node tree from {Source} to namespace {Target}, force={Force}",
                            sourceNode, targetNs, force);

                        NodeCopyHelper.CopyNodeTree(
                                meshQuery, nodeFactory, host.Hub, sourceNode, targetNs, force, logger)
                            .Subscribe(
                                nodesCopied =>
                                {
                                    logger?.LogInformation("Import complete: {Count} nodes copied", nodesCopied);

                                    var successDialog = Controls.Dialog(
                                        Controls.Markdown($"**Import Complete**\n\nCopied **{nodesCopied}** node(s) to `{(string.IsNullOrEmpty(targetNs) ? "root" : targetNs)}`."),
                                        "Import Complete"
                                    ).WithSize("M").WithClosable(true).WithCloseAction(ctx =>
                                    {
                                        var overviewUrl = string.IsNullOrEmpty(targetNs)
                                            ? MeshNodeLayoutAreas.BuildUrl(sourceNode.Split('/').Last(), MeshNodeLayoutAreas.OverviewArea)
                                            : MeshNodeLayoutAreas.BuildUrl(targetNs, MeshNodeLayoutAreas.OverviewArea);
                                        actx.NavigateTo(overviewUrl);
                                        return Task.CompletedTask;
                                    });
                                    actx.Host.UpdateArea(DialogControl.DialogArea, successDialog);
                                },
                                ex =>
                                {
                                    logger?.LogError(ex, "Import failed for {Source} -> {Target}", sourceNode, targetNs);
                                    var errorMsg = ex.Message.Contains("Access denied") || ex.Message.Contains("Unauthorized")
                                        ? "You do not have permission to import nodes here."
                                        : $"Import failed: {ex.Message}";
                                    ShowErrorDialog(actx, "Import Failed", errorMsg);
                                });
                    });
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

    /// <summary>
    /// Builds the "Mirror from / to another instance" sub-form using standard
    /// <see cref="UiControl"/>s only (text fields, radio, checkboxes, button +
    /// dialog for the result). Click handler resolves
    /// <see cref="IMirrorOperations"/> via DI and runs Push/Pull. The remote
    /// portal must be reachable over HTTPS from THIS instance — for local↔prod
    /// that's always public direction (local pulls/pushes; prod cannot reach
    /// localhost).
    /// </summary>
    private static UiControl BuildRemoteSource(
        LayoutAreaHost host, string formId, string dataContext)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>();
        var stack = Controls.Stack.WithWidth("100%");

        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("remoteUrl"))
        {
            Label = "Remote portal URL",
            Placeholder = "https://memex.meshweaver.cloud",
            DataContext = dataContext,
        }.WithStyle("width: 100%; margin-bottom: 12px;"));

        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("remoteToken"))
        {
            Label = "API token (issued on the remote)",
            Placeholder = "mw_…",
            DataContext = dataContext,
        }.WithStyle("width: 100%; margin-bottom: 12px;"));

        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("remoteSourcePath"))
        {
            Label = "Source path",
            Placeholder = "rbuergi/Story",
            DataContext = dataContext,
        }.WithStyle("width: 100%; margin-bottom: 12px;"));

        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("remoteTargetPath"))
        {
            Label = "Target path (optional, defaults to source)",
            DataContext = dataContext,
        }.WithStyle("width: 100%; margin-bottom: 12px;"));

        stack = stack.WithView(Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-bottom: 12px;")
            .WithView(Controls.Body("Direction").WithStyle("font-weight: 600; margin-bottom: 4px;"))
            .WithView(new RadioGroupControl(
                new JsonPointerReference("direction"),
                new Option<string>[]
                {
                    new("push", "Push (this instance → remote)"),
                    new("pull", "Pull (remote → this instance)"),
                },
                nameof(String))
            {
                DataContext = dataContext
            }.WithOrientation(Orientation.Vertical)));

        stack = stack.WithView(new CheckBoxControl(new JsonPointerReference("dryRun"))
        {
            Label = "Dry-run (preview without writing)",
            DataContext = dataContext,
        }.WithStyle("margin-bottom: 8px;"));

        stack = stack.WithView(new CheckBoxControl(new JsonPointerReference("removeMissing"))
        {
            Label = "Delete target nodes that don't exist in the source (DESTRUCTIVE)",
            DataContext = dataContext,
        }.WithStyle("margin-bottom: 16px;"));

        stack = stack.WithView(Controls.Button("Run mirror")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.ArrowSync())
            .WithClickAction(actx =>
            {
                actx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                    .Take(1)
                    .Subscribe(form =>
                    {
                        string GetStr(string key) => form.GetValueOrDefault(key)?.ToString()?.Trim() ?? "";
                        bool GetBool(string key) =>
                            form.GetValueOrDefault(key) is true or "True" or "true";

                        var url = GetStr("remoteUrl");
                        var token = GetStr("remoteToken");
                        var srcPath = GetStr("remoteSourcePath");
                        var tgtPath = GetStr("remoteTargetPath");
                        var direction = GetStr("direction");
                        if (string.IsNullOrEmpty(direction)) direction = "push";

                        if (string.IsNullOrWhiteSpace(url)
                            || string.IsNullOrWhiteSpace(token)
                            || string.IsNullOrWhiteSpace(srcPath))
                        {
                            ShowErrorDialog(actx, "Missing fields",
                                "Remote URL, API token, and source path are all required.");
                            return;
                        }

                        var request = new MirrorRequest
                        {
                            RemoteBaseUrl = url,
                            RemoteToken = token,
                            SourcePath = srcPath,
                            TargetPath = string.IsNullOrEmpty(tgtPath) ? null : tgtPath,
                            Direction = direction == "pull" ? "Pull" : "Push",
                            DryRun = GetBool("dryRun"),
                            RemoveMissing = GetBool("removeMissing"),
                        };

                        // Standard request/response pattern: post the message at
                        // the mesh hub, observe the MirrorResult response. The
                        // handler is registered by AddMirrorHandler() on the mesh
                        // hub config — wired in by AddPersistence /
                        // AddFileSystemPersistence / etc.
                        host.Hub.Observe<MirrorResult>(request, o => o.WithTarget(new Address("mesh")))
                            .Subscribe(
                                d => actx.Host.UpdateArea(DialogControl.DialogArea,
                                    BuildMirrorResultDialog(d.Message)),
                                ex =>
                                {
                                    logger?.LogError(ex, "Mirror failed for {Source} {Direction} {Url}",
                                        srcPath, direction, url);
                                    ShowErrorDialog(actx, "Mirror failed", ex.Message);
                                });
                    });
            }));

        return stack;
    }

    private static UiControl BuildMirrorResultDialog(MirrorResult result)
    {
        string body = result.Status switch
        {
            "Ok" =>
                $"**{result.Direction} succeeded.**\n\n" +
                $"- Source: `{result.SourcePath}`\n" +
                $"- Target: `{result.TargetPath}`\n" +
                $"- Nodes imported: **{result.NodesImported}**\n" +
                $"- Nodes skipped: {result.NodesSkipped}\n" +
                $"- Nodes removed: {result.NodesRemoved}\n" +
                $"- Time: {result.ElapsedMs / 1000.0:F1}s",
            "DryRun" =>
                $"**{result.Direction} — dry-run preview ({result.NodesScanned} nodes).**\n\n" +
                $"- Source: `{result.SourcePath}`\n" +
                $"- Target: `{result.TargetPath}`\n\n" +
                (result.Paths.Count > 0
                    ? "Paths:\n" + string.Join("\n", result.Paths.Select(p => $"- `{p}`"))
                    : "_(no nodes match — verify the source path)_") +
                "\n\nUntick **Dry-run** and rerun to perform the operation.",
            _ =>
                $"**{result.Direction} failed.**\n\n{result.Error}",
        };

        return Controls.Dialog(
            Controls.Markdown(body),
            $"{result.Direction}: {result.Status}"
        ).WithSize("M").WithClosable(true);
    }
}
