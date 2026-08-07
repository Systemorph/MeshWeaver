using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
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
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.VersionsArea))
            { LabelKey = "menu.versions" };
    }
    /// <summary>
    /// Renders the Versions list showing all historical versions of the current node.
    /// Each row has version number, timestamp, and Compare/Restore buttons.
    /// </summary>
    /// <summary>
    /// The well-known path of the central Collaboration plugin's review workspace. When it exists
    /// on the mesh, the Versions page DELEGATES to its TrackChanges area — the whole track-changes
    /// experience then lives in a git-synced plugin and evolves without core image rolls. Core
    /// keeps only this thin hook plus the built-in fallback (also reachable via ?builtin=1).
    /// </summary>
    internal const string CollaborationWorkspacePath = "Collaboration/Workspace";

    [Browsable(false)]
    public static IObservable<UiControl?> Versions(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var versionQuery = host.Hub.ServiceProvider.GetService<IVersionQuery>();
        var access = host.Hub.ServiceProvider.GetService<AccessService>();

        if (versionQuery == null)
        {
            return Observable.Return<UiControl?>(
                Controls.Stack.WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host))
                    .WithView(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">Version history is not available.</p>")));
        }

        var mesh = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (host.GetQueryStringParamValue("builtin") != "1" && mesh is not null)
        {
            return HasCollaborationWorkspace(mesh)
                .Select(hasWorkspace => hasWorkspace
                    ? Observable.Return<UiControl?>(DelegatedVersions(host, hubPath))
                    : BuiltInVersions(host, hubPath, versionQuery, access))
                .Switch();
        }

        return BuiltInVersions(host, hubPath, versionQuery, access);
    }

    /// <summary>Embeds the Collaboration workspace's TrackChanges area for this node, with a
    /// lightweight link back to the built-in list.</summary>
    private static UiControl DelegatedVersions(LayoutAreaHost host, string hubPath) =>
        Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host))
            .WithView(Controls.LayoutArea(CollaborationWorkspacePath, "TrackChanges", hubPath)
                .WithShowProgress(false))
            .WithView(Controls.Html(
                $"<p style=\"margin-top: 8px;\"><a href=\"{MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.VersionsArea, "builtin=1")}\" " +
                "style=\"color: var(--neutral-foreground-hint); font-size: .8rem;\">Use the built-in version list</a></p>"));

    /// <summary>One bounded existence probe over the query index (no per-node hub activation —
    /// probing a MISSING node's hub would cost the full activation timeout).</summary>
    private static IObservable<bool> HasCollaborationWorkspace(IMeshService mesh) =>
        mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{CollaborationWorkspacePath}"))
            .Scan(false, (found, change) =>
                found || change.Items.Any(n => n.Path == CollaborationWorkspacePath))
            .StartWith(false)
            .Throttle(TimeSpan.FromMilliseconds(800))
            .Take(1);

    private static IObservable<UiControl?> BuiltInVersions(
        LayoutAreaHost host, string hubPath, IVersionQuery versionQuery, AccessService? access)
    {
        // The chosen baseline rides a data stream (as a string — data streams are reference-typed):
        // "Set baseline" marks a version, and every other row then offers "Compare to v{baseline}"
        // (?from/?to), so any two versions can be compared — not just version-vs-current.
        var baselineId = $"versionBaseline_{hubPath.Replace("/", "_")}";
        var baseline = host.GetDataStream<string>(baselineId)
            .Select(s => long.TryParse(s, out var parsed) ? parsed : 0L)
            .StartWith(0L)
            .DistinctUntilChanged();

        return versionQuery.GetVersions(hubPath)
            .ToList()
            .CombineLatest(baseline, (versions, baselineVersion) =>
        {
            var stack = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

            // Back button
            var backHref = MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.OverviewArea);
            stack = stack.WithView(
                Controls.Stack.WithOrientation(Orientation.Horizontal)
                    .WithStyle("align-items: center; gap: 8px; margin-bottom: 16px;")
                    .WithView(Controls.Button(host.Localize("common.back"))
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

            stack = stack.WithView(Controls.Html(
                baselineVersion > 0
                    ? $"<p style=\"color: var(--neutral-foreground-hint); margin: 0 0 12px 0;\">Baseline: <strong>v{baselineVersion}</strong> — pick any other version to compare against it.</p>"
                    : "<p style=\"color: var(--neutral-foreground-hint); margin: 0 0 12px 0;\">Compare a version with the current document, or set one as the baseline to compare two versions.</p>"));

            foreach (var version in versions)
            {
                var timeStr = access.ToDisplayTime(version.LastModified).ToString("g");
                var changedBy = version.ChangedBy ?? "—";
                var name = version.Name ?? "";

                var compareHref = MeshNodeLayoutAreas.BuildUrl(
                    hubPath, MeshNodeLayoutAreas.VersionDiffArea, $"version={version.Version}");

                var isBaseline = version.Version == baselineVersion;
                var row = Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("align-items: center; gap: 16px; padding: 12px 16px; border: 1px solid " +
                               (isBaseline ? "var(--accent-fill-rest)" : "var(--neutral-stroke-rest)") +
                               "; border-radius: 6px; margin-bottom: 8px;")
                    .WithView(Controls.Html(
                        $"<div style=\"min-width: 80px;\"><strong>v{version.Version}</strong></div>"))
                    .WithView(Controls.Html(
                        $"<div style=\"flex: 1; color: var(--neutral-foreground-hint);\">{System.Net.WebUtility.HtmlEncode(timeStr)}</div>"))
                    .WithView(Controls.Html(
                        $"<div style=\"min-width: 120px; color: var(--neutral-foreground-hint);\">{System.Net.WebUtility.HtmlEncode(changedBy)}</div>"))
                    .WithView(Controls.Button(host.Localize("ui.compare"))
                        .WithAppearance(Appearance.Outline)
                        .WithNavigateToHref(compareHref));

                var thisVersion = version.Version;
                row = row.WithView(Controls.Button(isBaseline ? "Baseline ✓" : "Set baseline")
                    .WithAppearance(isBaseline ? Appearance.Accent : Appearance.Lightweight)
                    .WithClickAction(ctx =>
                    {
                        ctx.Host.UpdateData(baselineId, isBaseline ? "" : thisVersion.ToString());
                        return Task.CompletedTask;
                    }));

                if (baselineVersion > 0 && !isBaseline)
                {
                    // from = the older of the pair, to = the newer — the diff reads forward in time.
                    var fromVersion = Math.Min(baselineVersion, thisVersion);
                    var toVersion = Math.Max(baselineVersion, thisVersion);
                    row = row.WithView(Controls.Button($"Compare to v{baselineVersion}")
                        .WithAppearance(Appearance.Outline)
                        .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(
                            hubPath, MeshNodeLayoutAreas.VersionDiffArea,
                            $"from={fromVersion}&to={toVersion}")));
                }

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
            return versionQuery.GetVersion(hubPath, fromVersion, options)
                .Zip(versionQuery.GetVersion(hubPath, toVersion, options),
                    (fromNode, toNode) => (fromNode, toNode))
                .Select(pair =>
                {
                    var (fromNode, toNode) = pair;
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

        var versionStr = host.GetQueryStringParamValue("version");

        // Mode 3: NO parameters — show the changes since the LAST version. Clicking into the diff
        // without picking anything should immediately show what changed most recently, not an
        // "invalid parameter" notice.
        if (string.IsNullOrEmpty(versionStr))
        {
            return host.Hub.GetMeshNode(hubPath)
                .SelectMany(currentNode =>
                {
                    if (currentNode == null)
                        return Observable.Return<UiControl?>(Controls.Html($"<p>Node {hubPath} not found.</p>"));
                    return versionQuery.GetVersionBefore(hubPath, currentNode.Version, options)
                        .Select(previousNode =>
                        {
                            if (previousNode == null)
                                return (UiControl?)Controls.Html(
                                    "<p style=\"color: var(--neutral-foreground-hint);\">No earlier version to compare — this is the first recorded version.</p>");
                            return (UiControl?)BuildDiffStack(host, hubPath, previousNode, currentNode, options,
                                $"Version {previousNode.Version}", "Current",
                                $"Changes since v{previousNode.Version} (last version)",
                                restoreVersion: previousNode.Version);
                        });
                });
        }

        // Mode 2: version=X — compare historical version to current.
        if (!long.TryParse(versionStr, out var targetVersion))
        {
            return Observable.Return<UiControl?>(
                Controls.Html("<p>Invalid version parameter. Use <code>?version=X</code> or <code>?from=X&to=Y</code>.</p>"));
        }

        // One-shot read of the current node via GetDataRequest — true request/response,
        // no live workspace subscription. Render once with the snapshot; diff editor
        // doesn't need to re-render on subsequent stream ticks.
        return host.Hub.GetMeshNode(hubPath)
            .SelectMany(currentNode =>
            {
                if (currentNode == null)
                    return Observable.Return<UiControl?>(Controls.Html($"<p>Node {hubPath} not found.</p>"));

                return versionQuery.GetVersion(hubPath, targetVersion, options)
                    .Select(historicalNode =>
                    {
                        if (historicalNode == null)
                            return (UiControl?)Controls.Html($"<p>Version {targetVersion} not found.</p>");

                        return (UiControl?)BuildDiffStack(host, hubPath, historicalNode, currentNode, options,
                            $"Version {targetVersion}", "Current",
                            $"Comparing Version {targetVersion} to Current",
                            restoreVersion: targetVersion);
                    });
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
                .WithView(Controls.Button(host.Localize("ui.backToVersions"))
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(backHref)));

        stack = stack.WithView(Controls.Html(
            $"<h2 style=\"margin: 0 0 16px 0;\">{System.Web.HttpUtility.HtmlEncode(title)}</h2>"));

        var originalContent = ExtractDiffContent(originalNode, options);
        var modifiedContent = ExtractDiffContent(modifiedNode, options);
        var language = IsMarkdownContent(originalNode, options) || IsMarkdownContent(modifiedNode, options)
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

        versionQuery.GetVersion(msg.Path, msg.TargetVersion, options)
            .Subscribe(historicalNode =>
            {
                if (historicalNode == null)
                {
                    hub.Post(new DataChangeResponse(hub.Version,
                        new ActivityLog("Rollback").Fail($"Version {msg.TargetVersion} not found for {msg.Path}")),
                        o => o.ResponseFor(request));
                    return;
                }

                // 🚨 Restore through GetMeshNodeStream(path).Update — the ONE mutation API — NOT a
                // raw DataChangeRequest post.
                //
                // The old shape posted the historical node with `Version = 0` ("forces a new save")
                // straight at the hub, bypassing the owner's version mint. That raw node reached
                // MonotonicWriteGuardStorageAdapter, which REFUSES a backward write — and rightly
                // so: it cannot tell a rollback from the stale-snapshot corruption it exists to
                // stop ("do not relax this guard"). The refusal returns the STORED node rather than
                // an error, and the post was fire-and-forget, so the rollback silently did nothing:
                // the node never reverted, and any caller polling for the restored content waited
                // until its harness killed it (VersionHistoryTest.RollbackNode_RestoresHistoricalState,
                // a CI-only hang — the guard's fast path lets the write through while its
                // high-water mark is cold, so it only bites once earlier writes have warmed it).
                //
                // Update stamps Version = NextVersion(Math.Max(current.Version, updated.Version)),
                // so restoring HISTORICAL CONTENT becomes an ordinary FORWARD write: the node's
                // content goes back, its revision counter keeps climbing, and the guard is
                // untouched. A rollback is a new revision that happens to carry old content — it
                // was never a backward write.
                hub.GetMeshNodeStream(msg.Path)
                    .Update(_ => historicalNode with { Version = 0 })
                    .Subscribe(
                        _ => hub.Post(new DataChangeResponse(hub.Version, new ActivityLog("Rollback")),
                            o => o.ResponseFor(request)),
                        updateEx => hub.Post(new DataChangeResponse(hub.Version,
                            new ActivityLog("Rollback").Fail($"Rollback write failed: {updateEx.Message}")),
                            o => o.ResponseFor(request)));
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
        // MUST NOT use Query (read-side index lags); see
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
                    .SelectMany(path =>
                        versionQuery.GetVersionBefore(path, activityLog.StartVersion, options))
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
    /// <summary>
    /// The content-shape text probe order for the diff: dynamic node types carry their prose in
    /// differently-named string fields (a SocialMedia/Post uses <c>text</c>; markdown-shaped
    /// content uses <c>content</c>; some types use <c>body</c>).
    /// </summary>
    internal static readonly ImmutableArray<string> TextProperties = ["content", "text", "body"];

    internal static string ExtractDiffContent(MeshNode? node, JsonSerializerOptions options)
    {
        if (node == null)
            return "";

        // Try markdown first
        var markdown = MarkdownOverviewLayoutArea.GetMarkdownContent(node);
        if (!string.IsNullOrEmpty(markdown))
            return markdown;

        // Shape-tolerant text probe on the CONTENT: a dynamic type's content arrives TYPED on its
        // own hub (a runtime-compiled object — not MarkdownContent, not a JsonElement), and its
        // prose may live in a field GetMarkdownContent doesn't know (a SocialMedia/Post's `text`).
        // Coerce to an element via the CONCRETE runtime type (never the object overload — that
        // adopts foreign types into the registry as a read side effect) and probe.
        var element = ContentElement(node.Content, options);
        if (element is { ValueKind: JsonValueKind.Object })
        {
            foreach (var property in TextProperties)
            {
                if (element.Value.TryGetProperty(property, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(value.GetString()))
                    return value.GetString()!;
            }
        }

        // Fall back to the CONTENT as indented JSON — diffing the whole node envelope buries the
        // actual change under version/lastModified noise.
        var indentedOptions = new JsonSerializerOptions(options) { WriteIndented = true };
        return element is null
            ? ""
            : JsonSerializer.Serialize(element.Value, indentedOptions);
    }

    /// <summary>
    /// Checks if a node's diff content is prose (markdown or a text-bearing content field) rather
    /// than a JSON fallback — drives the diff editor's language.
    /// </summary>
    internal static bool IsMarkdownContent(MeshNode? node, JsonSerializerOptions options)
    {
        if (node == null) return false;
        if (!string.IsNullOrEmpty(MarkdownOverviewLayoutArea.GetMarkdownContent(node)))
            return true;
        var element = ContentElement(node.Content, options);
        return element is { ValueKind: JsonValueKind.Object }
            && TextProperties.Any(p =>
                element.Value.TryGetProperty(p, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(value.GetString()));
    }

    /// <summary>Coerces content of any shape (typed record, dynamic object, JsonElement, JsonNode)
    /// into a JsonElement; null when there is no content or it cannot serialize.</summary>
    private static JsonElement? ContentElement(object? content, JsonSerializerOptions options)
    {
        switch (content)
        {
            case null:
                return null;
            case JsonElement je:
                return je;
            case System.Text.Json.Nodes.JsonNode jn:
                return JsonSerializer.Deserialize<JsonElement>(jn, options);
            default:
                try
                {
                    return JsonSerializer.SerializeToElement(content, content.GetType(), options);
                }
                catch (Exception)
                {
                    return null;
                }
        }
    }
}
