using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
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
    public const string CollaborationWorkspacePath = "Collaboration/Workspace";

    /// <summary>Data-stream id holding the chosen FROM version for this node's compare picker.</summary>
    public static string FromStreamId(string hubPath) => $"compareFrom_{hubPath.Replace("/", "_")}";

    /// <summary>Data-stream id holding the chosen TO version for this node's compare picker.</summary>
    public static string ToStreamId(string hubPath) => $"compareTo_{hubPath.Replace("/", "_")}";

    /// <summary>
    /// Handles RollbackNodeRequest by fetching the historical version and posting it as a DataChangeRequest.
    /// Sync handler — composes via <c>IObservable</c>; no <c>await</c>.
    /// </summary>
    public static IMessageDelivery HandleRollbackNodeRequest(
        IMessageHub hub,
        IMessageDelivery<RollbackNodeRequest> request)
    {
        // 🚨 Ask whether history is RETAINED, not whether the service resolved. The null check
        // alone was unreachable: `NoOpVersionQuery` is registered unconditionally
        // (`PersistenceExtensions`, TryAddSingleton), so this service is never null on any
        // deployment — and on every DATABASE-backed one it is the no-op, which answers "no
        // version found" to a question it never records an answer to. So this honest refusal
        // existed and could not fire, and the caller was told a data-shaped miss about a
        // configuration fact (MeshWeaver#3264).
        var versionQuery = hub.ServiceProvider.GetService<IVersionQuery>();
        if (versionQuery is null or { RetainsHistory: false })
        {
            hub.Post(new DataChangeResponse(hub.Version,
                new ActivityLog("Rollback").Fail(
                    "Version history is not retained on this deployment, so there is nothing to "
                    + "restore from. This is a configuration fact, not a property of this node.")),
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
    public static IMessageDelivery HandleUndoActivityRequest(
        IMessageHub hub,
        IMessageDelivery<UndoActivityRequest> request)
    {
        // 🚨 Ask whether history is RETAINED, not whether the service resolved. The null check
        // alone was unreachable: `NoOpVersionQuery` is registered unconditionally
        // (`PersistenceExtensions`, TryAddSingleton), so this service is never null on any
        // deployment — and on every DATABASE-backed one it is the no-op, which answers "no
        // version found" to a question it never records an answer to. So this honest refusal
        // existed and could not fire, and the caller was told a data-shaped miss about a
        // configuration fact (MeshWeaver#3264).
        var versionQuery = hub.ServiceProvider.GetService<IVersionQuery>();
        if (versionQuery is null or { RetainsHistory: false })
        {
            hub.Post(new DataChangeResponse(hub.Version,
                new ActivityLog("Undo").Fail(
                    "Version history is not retained on this deployment, so there is nothing to "
                    + "restore from. This is a configuration fact, not a property of this node.")),
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
                // No await — GetVersionBefore is already reactive, so each path's lookup is
                // merged straight in via SelectMany (no Task bridge, no Observable.FromAsync).
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
    public static readonly ImmutableArray<string> TextProperties = ["content", "text", "body"];

    public static string ExtractDiffContent(MeshNode? node, JsonSerializerOptions options)
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
    public static bool IsMarkdownContent(MeshNode? node, JsonSerializerOptions options)
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
