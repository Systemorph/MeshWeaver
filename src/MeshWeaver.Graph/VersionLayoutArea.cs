using System.ComponentModel;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout areas for version history: list of versions and diff view.
/// </summary>
public static class VersionLayoutArea
{
    /// <summary>
    /// Returns the Versions menu item if the user has Read permission.
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Read))
            return null;
        return new("Versions", MeshNodeLayoutAreas.VersionsArea, Order: 55,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.VersionsArea));
    }
    /// <summary>
    /// Renders the Versions list showing all historical versions of the current node.
    /// Each row has version number, timestamp, and Compare/Restore buttons.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Versions(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var versionQuery = host.Hub.ServiceProvider.GetService<IVersionQuery>();

        if (versionQuery == null)
        {
            return Observable.Return<UiControl?>(
                Controls.Stack.WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host))
                    .WithView(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">Version history is not available.</p>")));
        }

        return Observable.FromAsync(async () =>
        {
            var versions = new List<MeshNodeVersion>();
            await foreach (var v in versionQuery.GetVersionsAsync(hubPath))
                versions.Add(v);

            var stack = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

            // Back button
            var backHref = MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.OverviewArea);
            stack = stack.WithView(
                Controls.Stack.WithOrientation(Orientation.Horizontal)
                    .WithStyle("align-items: center; gap: 8px; margin-bottom: 16px;")
                    .WithView(Controls.Button("Back")
                        .WithAppearance(Appearance.Lightweight)
                        .WithIconStart(FluentIcons.ArrowLeft())
                        .WithNavigateToHref(backHref)));

            stack = stack.WithView(Controls.Html("<h2 style=\"margin: 0 0 16px 0;\">Version History</h2>"));

            if (versions.Count == 0)
            {
                stack = stack.WithView(
                    Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">No version history available.</p>"));
                return (UiControl?)stack;
            }

            foreach (var version in versions)
            {
                var timeStr = version.LastModified.LocalDateTime.ToString("g");
                var changedBy = version.ChangedBy ?? "—";
                var name = version.Name ?? "";

                var compareHref = MeshNodeLayoutAreas.BuildUrl(
                    hubPath, MeshNodeLayoutAreas.VersionDiffArea, $"version={version.Version}");

                var row = Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("align-items: center; gap: 16px; padding: 12px 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 6px; margin-bottom: 8px;")
                    .WithView(Controls.Html(
                        $"<div style=\"min-width: 80px;\"><strong>v{version.Version}</strong></div>"))
                    .WithView(Controls.Html(
                        $"<div style=\"flex: 1; color: var(--neutral-foreground-hint);\">{System.Net.WebUtility.HtmlEncode(timeStr)}</div>"))
                    .WithView(Controls.Html(
                        $"<div style=\"min-width: 120px; color: var(--neutral-foreground-hint);\">{System.Net.WebUtility.HtmlEncode(changedBy)}</div>"))
                    .WithView(Controls.Button("Compare")
                        .WithAppearance(Appearance.Outline)
                        .WithNavigateToHref(compareHref));

                stack = stack.WithView(row);
            }

            return (UiControl?)stack;
        });
    }

    /// <summary>
    /// Renders the diff view comparing a historical version to the current version.
    /// Reads ?version= query parameter to determine which version to compare.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> VersionDiff(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var versionStr = host.GetQueryStringParamValue("version");

        if (!long.TryParse(versionStr, out var targetVersion))
        {
            return Observable.Return<UiControl?>(
                Controls.Html("<p>Invalid version parameter.</p>"));
        }

        var versionQuery = host.Hub.ServiceProvider.GetService<IVersionQuery>();
        if (versionQuery == null)
        {
            return Observable.Return<UiControl?>(
                Controls.Html("<p>Version history is not available.</p>"));
        }

        var nodeStream = host.Workspace.GetStream<MeshNode>()
            ?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.SelectMany(async nodes =>
        {
            var currentNode = nodes.FirstOrDefault(n => n.Path == hubPath);
            var options = host.Hub.JsonSerializerOptions;
            var historicalNode = await versionQuery.GetVersionAsync(hubPath, targetVersion, options);

            if (historicalNode == null)
            {
                return (UiControl?)Controls.Html($"<p>Version {targetVersion} not found.</p>");
            }

            var stack = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

            // Back button
            var backHref = MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.VersionsArea);
            stack = stack.WithView(
                Controls.Stack.WithOrientation(Orientation.Horizontal)
                    .WithStyle("align-items: center; gap: 8px; margin-bottom: 16px;")
                    .WithView(Controls.Button("Back to Versions")
                        .WithAppearance(Appearance.Lightweight)
                        .WithIconStart(FluentIcons.ArrowLeft())
                        .WithNavigateToHref(backHref)));

            stack = stack.WithView(Controls.Html(
                $"<h2 style=\"margin: 0 0 16px 0;\">Comparing Version {targetVersion} to Current</h2>"));

            // Determine content type and extract text for diff
            var originalContent = ExtractDiffContent(historicalNode, options);
            var modifiedContent = ExtractDiffContent(currentNode, options);
            var language = IsMarkdownContent(historicalNode) ? "markdown" : "json";

            var diffControl = new DiffEditorControl
            {
                OriginalContent = originalContent,
                ModifiedContent = modifiedContent,
                OriginalLabel = $"Version {targetVersion}",
                ModifiedLabel = "Current",
                Language = language,
                Height = "500px"
            };

            stack = stack.WithView(diffControl);

            // Restore button
            stack = stack.WithView(
                Controls.Stack.WithStyle("margin-top: 16px;")
                    .WithView(Controls.Button($"Restore Version {targetVersion}")
                        .WithAppearance(Appearance.Accent)
                        .WithIconStart(FluentIcons.ArrowUndo())
                        .WithClickAction(ctx =>
                        {
                            ctx.Hub.Post(new RollbackNodeRequest(hubPath, targetVersion));
                            return Task.CompletedTask;
                        })));

            return (UiControl?)stack;
        });
    }

    /// <summary>
    /// Handles RollbackNodeRequest by fetching the historical version and posting it as a DataChangeRequest.
    /// </summary>
    internal static async Task<IMessageDelivery> HandleRollbackNodeRequest(
        IMessageHub hub,
        IMessageDelivery<RollbackNodeRequest> request,
        CancellationToken ct)
    {
        var versionQuery = hub.ServiceProvider.GetService<IVersionQuery>();
        if (versionQuery == null)
        {
            hub.Post(new DataChangeResponse(hub.Version,
                new ActivityLog("Rollback").Fail("Version history not available")),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        var msg = request.Message;
        var options = hub.JsonSerializerOptions;
        var historicalNode = await versionQuery.GetVersionAsync(msg.Path, msg.TargetVersion, options, ct);

        if (historicalNode == null)
        {
            hub.Post(new DataChangeResponse(hub.Version,
                new ActivityLog("Rollback").Fail($"Version {msg.TargetVersion} not found for {msg.Path}")),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        // Post the historical node as an update (version 0 forces a new save)
        hub.Post(
            new DataChangeRequest { ChangedBy = "rollback" }.WithUpdates(historicalNode with { Version = 0 }),
            o => o.WithTarget(hub.Address));

        return request.Processed();
    }

    /// <summary>
    /// Handles UndoActivityRequest by restoring all affected nodes to their pre-activity state.
    /// </summary>
    internal static async Task<IMessageDelivery> HandleUndoActivityRequest(
        IMessageHub hub,
        IMessageDelivery<UndoActivityRequest> request,
        CancellationToken ct)
    {
        var versionQuery = hub.ServiceProvider.GetService<IVersionQuery>();
        if (versionQuery == null)
        {
            hub.Post(new DataChangeResponse(hub.Version,
                new ActivityLog("Undo").Fail("Version history not available")),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        var msg = request.Message;
        var hubPath = hub.Address.ToString();
        var options = hub.JsonSerializerOptions;

        // Find the activity log node by known path pattern
        var persistence = hub.ServiceProvider.GetRequiredService<IMeshStorage>();
        var activityNodePath = $"{hubPath}/_activity/{msg.ActivityLogId}";
        var activityNode = await persistence.GetNodeAsync(activityNodePath, ct);

        if (activityNode?.Content is not ActivityLog activityLog)
        {
            hub.Post(new DataChangeResponse(hub.Version,
                new ActivityLog("Undo").Fail($"Activity log {msg.ActivityLogId} not found")),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        if (activityLog.AffectedPaths.Count == 0)
        {
            hub.Post(new DataChangeResponse(hub.Version,
                new ActivityLog("Undo").Fail("No affected paths recorded for this activity")),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        // For each affected path, find the version just before StartVersion
        var restoredNodes = new List<MeshNode>();
        foreach (var path in activityLog.AffectedPaths)
        {
            var beforeVersion = await versionQuery.GetVersionBeforeAsync(
                path, activityLog.StartVersion, options, ct);
            if (beforeVersion != null)
                restoredNodes.Add(beforeVersion with { Version = 0 });
        }

        if (restoredNodes.Count > 0)
        {
            hub.Post(
                new DataChangeRequest { ChangedBy = "undo" }.WithUpdates(restoredNodes.ToArray()),
                o => o.WithTarget(hub.Address));
        }

        return request.Processed();
    }

    /// <summary>
    /// Extracts content for diff display. Uses markdown for markdown content, JSON for everything else.
    /// </summary>
    private static string ExtractDiffContent(MeshNode? node, JsonSerializerOptions options)
    {
        if (node == null)
            return "";

        // Try markdown first
        var markdown = MarkdownOverviewLayoutArea.GetMarkdownContent(node);
        if (!string.IsNullOrEmpty(markdown))
            return markdown;

        // Fall back to indented JSON of the whole node
        var indentedOptions = new JsonSerializerOptions(options) { WriteIndented = true };
        return JsonSerializer.Serialize(node, indentedOptions);
    }

    /// <summary>
    /// Checks if a node contains markdown content.
    /// </summary>
    private static bool IsMarkdownContent(MeshNode? node)
    {
        if (node == null) return false;
        return !string.IsNullOrEmpty(MarkdownOverviewLayoutArea.GetMarkdownContent(node));
    }
}
