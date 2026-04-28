using System.Collections.Immutable;
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

        return versionQuery.GetVersions(hubPath)
            .ToList()
            .Select(versions =>
        {
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
    /// Renders the diff view for a node. Supports two modes:
    ///   <list type="bullet">
    ///     <item><c>?from=X&amp;to=Y</c> — compare two historical versions.</item>
    ///     <item><c>?version=X</c> — compare a historical version to the current node.</item>
    ///   </list>
    /// Emits the diff once — the Monaco diff editor is expensive to re-create, so we
    /// avoid re-emitting on every node-stream tick.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> VersionDiff(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var versionQuery = host.Hub.ServiceProvider.GetService<IVersionQuery>();
        if (versionQuery == null)
        {
            return Observable.Return<UiControl?>(
                Controls.Html("<p>Version history is not available.</p>"));
        }

        var options = host.Hub.JsonSerializerOptions;
        var fromStr = host.GetQueryStringParamValue("from");
        var toStr = host.GetQueryStringParamValue("to");

        // Mode 1: from=X&to=Y — compare two historical versions.
        if (long.TryParse(fromStr, out var fromVersion) && long.TryParse(toStr, out var toVersion))
        {
            return Observable.FromAsync(async () =>
            {
                var fromNode = await versionQuery.GetVersionAsync(hubPath, fromVersion, options);
                var toNode = await versionQuery.GetVersionAsync(hubPath, toVersion, options);
                if (fromNode == null)
                    return (UiControl?)Controls.Html($"<p>Version {fromVersion} not found.</p>");
                if (toNode == null)
                    return (UiControl?)Controls.Html($"<p>Version {toVersion} not found.</p>");

                return (UiControl?)BuildDiffStack(host, hubPath, fromNode, toNode, options,
                    $"Version {fromVersion}", $"Version {toVersion}",
                    $"Comparing Version {fromVersion} to Version {toVersion}",
                    restoreVersion: fromVersion);
            });
        }

        // Mode 2: version=X — compare historical version to current.
        var versionStr = host.GetQueryStringParamValue("version");
        if (!long.TryParse(versionStr, out var targetVersion))
        {
            return Observable.Return<UiControl?>(
                Controls.Html("<p>Invalid version parameter. Use <code>?version=X</code> or <code>?from=X&to=Y</code>.</p>"));
        }

        // One-shot read of the current node via GetDataRequest — true request/response,
        // no live workspace subscription. Render once with the snapshot; diff editor
        // doesn't need to re-render on subsequent stream ticks.
        return host.Hub.GetMeshNode(hubPath)
            .SelectMany(async currentNode =>
            {
                if (currentNode == null)
                    return (UiControl?)Controls.Html($"<p>Node {hubPath} not found.</p>");

                var historicalNode = await versionQuery.GetVersionAsync(hubPath, targetVersion, options);

                if (historicalNode == null)
                    return (UiControl?)Controls.Html($"<p>Version {targetVersion} not found.</p>");

                return (UiControl?)BuildDiffStack(host, hubPath, historicalNode, currentNode, options,
                    $"Version {targetVersion}", "Current",
                    $"Comparing Version {targetVersion} to Current",
                    restoreVersion: targetVersion);
            });
    }

    private static UiControl BuildDiffStack(
        LayoutAreaHost host, string hubPath,
        MeshNode originalNode, MeshNode modifiedNode,
        JsonSerializerOptions options,
        string originalLabel, string modifiedLabel,
        string title, long restoreVersion)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        var backHref = MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.VersionsArea);
        stack = stack.WithView(
            Controls.Stack.WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 8px; margin-bottom: 16px;")
                .WithView(Controls.Button("Back to Versions")
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(backHref)));

        stack = stack.WithView(Controls.Html(
            $"<h2 style=\"margin: 0 0 16px 0;\">{System.Web.HttpUtility.HtmlEncode(title)}</h2>"));

        var originalContent = ExtractDiffContent(originalNode, options);
        var modifiedContent = ExtractDiffContent(modifiedNode, options);
        var language = IsMarkdownContent(originalNode) || IsMarkdownContent(modifiedNode)
            ? "markdown"
            : "json";

        stack = stack.WithView(new DiffEditorControl
        {
            OriginalContent = originalContent,
            ModifiedContent = modifiedContent,
            OriginalLabel = originalLabel,
            ModifiedLabel = modifiedLabel,
            Language = language,
            Height = "600px"
        });

        stack = stack.WithView(
            Controls.Stack.WithStyle("margin-top: 16px;")
                .WithView(Controls.Button($"Restore Version {restoreVersion}")
                    .WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.ArrowUndo())
                    .WithClickAction(ctx =>
                    {
                        ctx.Hub.Post(new RollbackNodeRequest(hubPath, restoreVersion));
                        return Task.CompletedTask;
                    })));

        return stack;
    }

    /// <summary>
    /// Handles RollbackNodeRequest by fetching the historical version and posting it as a DataChangeRequest.
    /// Sync handler — composes via <c>IObservable</c>; no <c>await</c>.
    /// </summary>
    internal static IMessageDelivery HandleRollbackNodeRequest(
        IMessageHub hub,
        IMessageDelivery<RollbackNodeRequest> request)
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

        Observable.FromAsync(ct => versionQuery.GetVersionAsync(msg.Path, msg.TargetVersion, options, ct))
            .Subscribe(historicalNode =>
            {
                if (historicalNode == null)
                {
                    hub.Post(new DataChangeResponse(hub.Version,
                        new ActivityLog("Rollback").Fail($"Version {msg.TargetVersion} not found for {msg.Path}")),
                        o => o.ResponseFor(request));
                    return;
                }

                // Post the historical node as an update (version 0 forces a new save)
                hub.Post(
                    new DataChangeRequest { ChangedBy = "rollback" }.WithUpdates(historicalNode with { Version = 0 }),
                    o => o.WithTarget(hub.Address));
            },
            ex => hub.Post(new DataChangeResponse(hub.Version,
                new ActivityLog("Rollback").Fail($"Rollback error: {ex.Message}")),
                o => o.ResponseFor(request)));

        return request.Processed();
    }

    /// <summary>
    /// Handles UndoActivityRequest by restoring all affected nodes to their pre-activity state.
    /// Sync handler — composes via <c>IObservable</c>; no <c>await</c>.
    /// Persistence allowed: handler runs on the affected node's owning hub.
    /// </summary>
    internal static IMessageDelivery HandleUndoActivityRequest(
        IMessageHub hub,
        IMessageDelivery<UndoActivityRequest> request)
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
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var activityNodePath = $"{hubPath}/_activity/{msg.ActivityLogId}";

        // Read the activity-log node via one-shot GetDataRequest — true request/response,
        // no SubscribeRequest+immediate-unsubscribe. Single-node-by-path content reads
        // MUST NOT use ObserveQuery (read-side index lags); see
        // Doc/Architecture/AsynchronousCalls.md "Never use QueryAsync to obtain a MeshNode".
        hub.GetMeshNode(activityNodePath, TimeSpan.FromSeconds(15))
            .SelectMany(activityNode =>
            {
                if (activityNode?.Content is not ActivityLog activityLog)
                {
                    hub.Post(new DataChangeResponse(hub.Version,
                        new ActivityLog("Undo").Fail($"Activity log {msg.ActivityLogId} not found")),
                        o => o.ResponseFor(request));
                    return Observable.Empty<IReadOnlyCollection<MeshNode>>();
                }

                if (activityLog.AffectedPaths.Count == 0)
                {
                    hub.Post(new DataChangeResponse(hub.Version,
                        new ActivityLog("Undo").Fail("No affected paths recorded for this activity")),
                        o => o.ResponseFor(request));
                    return Observable.Empty<IReadOnlyCollection<MeshNode>>();
                }

                // For each affected path, fetch the version just before StartVersion in parallel.
                // No await — each path's lookup is wrapped in Observable.FromAsync and merged.
                return activityLog.AffectedPaths
                    .ToObservable()
                    .SelectMany(path => Observable.FromAsync(ct =>
                        versionQuery.GetVersionBeforeAsync(path, activityLog.StartVersion, options, ct)))
                    .Where(node => node != null)
                    .Select(node => node! with { Version = 0 })
                    .Aggregate(
                        ImmutableList<MeshNode>.Empty,
                        (acc, n) => acc.Add(n))
                    .Select(list => (IReadOnlyCollection<MeshNode>)list);
            })
            .Subscribe(restoredNodes =>
            {
                if (restoredNodes.Count > 0)
                {
                    hub.Post(
                        new DataChangeRequest { ChangedBy = "undo" }.WithUpdates(restoredNodes.ToArray()),
                        o => o.WithTarget(hub.Address));
                }
            },
            ex => hub.Post(new DataChangeResponse(hub.Version,
                new ActivityLog("Undo").Fail($"Undo error: {ex.Message}")),
                o => o.ResponseFor(request)));

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
