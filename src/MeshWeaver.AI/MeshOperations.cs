using System.Collections.Immutable;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Markdown;
using MeshWeaver.Layout;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Shared mesh operations for AI agents and MCP tools.
///
/// **Every public method returns <see cref="IObservable{T}"/>, never <see cref="Task{T}"/>.**
/// This is deliberate — the mesh is an actor-hub system and `await` on hub-backed work
/// deadlocks. Callers subscribe to drive work (<c>.Subscribe(onNext, onError)</c>) or
/// bridge at an external boundary (<c>.FirstAsync().ToTask()</c>) — never inside hub
/// flow. See CLAUDE.md "NOTHING ASYNC EVER".
/// </summary>
public class MeshOperations
{
    private readonly IMessageHub hub;
    private readonly ILogger<MeshOperations> logger;
    private readonly IMeshService mesh;

    /// <summary>
    /// Callback invoked when a node is created, updated, or patched.
    /// Provides path and version before/after for tracking document changes per thread message.
    /// </summary>
    public Action<NodeChangeEntry>? OnNodeChange { get; set; }

    /// <summary>
    /// Creates the operations facade over a message hub, resolving the logger and
    /// <c>IMeshService</c> from the hub's service provider.
    /// </summary>
    /// <param name="hub">The message hub whose services and address back every mesh operation.</param>
    public MeshOperations(IMessageHub hub)
    {
        this.hub = hub;
        this.logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshOperations>>();
        this.mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
    }

    /// <summary>
    /// Looks up the cached compilation error for the owning NodeType of <paramref name="node"/>.
    /// - If <paramref name="node"/> is a NodeType definition (Content is
    ///   <see cref="Graph.Configuration.NodeTypeDefinition"/> OR <c>NodeType</c>
    ///   field equals the meta-type marker <see cref="MeshNode.NodeTypePath"/>),
    ///   checks its own path.
    /// - Otherwise checks the NodeType's path.
    /// Returns <c>null</c> if no error is recorded.
    /// </summary>
    private IObservable<string?> LookupCompilationError(MeshNode node)
    {
        // NodeType==MeshNode.NodeTypePath catches the case where Content arrived
        // as a JsonElement (per-node hub didn't have NodeTypeDefinition in its
        // TypeRegistry, so polymorphic deserialisation fell back) — without
        // this check, we'd look up the meta-NodeType "NodeType" and miss the
        // actual broken-type error cached against the node's own path.
        var isNodeTypeDef = node.Content is Graph.Configuration.NodeTypeDefinition
            || string.Equals(node.NodeType, MeshNode.NodeTypePath, StringComparison.Ordinal);
        var nodeTypePath = isNodeTypeDef ? node.Path : node.NodeType;
        if (string.IsNullOrEmpty(nodeTypePath))
            return Observable.Return<string?>(null);

        // Fast path: the input node IS the settled NodeType MeshNode. Pre-settle
        // states (Pending/Compiling/Unknown) fall through to the stream so we
        // wait for the CompileWatcher's write-back rather than report a stale
        // null error.
        if (isNodeTypeDef
            && node.Content is Graph.Configuration.NodeTypeDefinition ownDef
            && IsSettled(ownDef))
            return Observable.Return<string?>(ownDef.CompilationError);

        // Slow path: subscribe to the NodeType's live stream, wait for a
        // settled CompilationStatus emission, then read the CompilationError.
        return hub.GetWorkspace().GetMeshNodeStream(nodeTypePath)
            // JsonElement-tolerant settle check + error read: a degraded NodeType node
            // (Content stayed a JsonElement) still satisfies the wait and yields its real
            // error, instead of hanging to the 5s timeout then reporting a null error.
            .Where(n => IsSettled(ReadCompilationStatusFromNode(n)))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(5))
            .Select(ReadCompilationError)
            .Catch<string?, Exception>(_ => Observable.Return<string?>(null));
    }

    /// <summary>
    /// Resolves @ prefix and normalises agent-emitted formatting noise.
    /// Models / autocomplete frequently wrap spaced filenames in quotes ("foo bar.docx",
    /// 'foo bar.docx'), put quotes around different segments, or include surrounding
    /// whitespace. None of those characters are legal mesh-path content, so we strip
    /// them regardless of position. Examples:
    ///   @graph/org1                                  → graph/org1
    ///   "@content/My File.md"                        → content/My File.md
    ///   @/Org/content/"My File.docx"                 → /Org/content/My File.docx
    ///   @/Org/"content/My File.docx"                 → /Org/content/My File.docx
    ///   @"/Org/content/My File.docx"                 → /Org/content/My File.docx
    ///   @/Org/content/'My File.docx'                 → /Org/content/My File.docx
    ///   "   @/Org/content/My File.docx   "           → /Org/content/My File.docx
    /// </summary>
    public static string ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // Strip surrounding/inner whitespace and quote characters in one pass.
        path = path.Trim();
        if (path.IndexOfAny(['"', '\'']) >= 0)
            path = path.Replace("\"", string.Empty).Replace("'", string.Empty);

        if (path.StartsWith("@"))
            return path[1..];
        return path;
    }

    /// <summary>
    /// Resolves a path relative to the current chat context.
    /// Absolute paths (starting with @/ or /) are returned as-is.
    /// Relative paths (e.g., @content:file.docx, @MyChild) are prepended with the chat's context path.
    /// Call this before <see cref="ResolvePath"/> whenever a tool accepts a user-supplied path
    /// — otherwise relative paths or bare names get shipped to the mesh unchanged and fail to route.
    /// </summary>
    public static string ResolveContextPath(IAgentChat chat, string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // Strip surrounding quotes (autocomplete wraps spaced paths in quotes)
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            path = path[1..^1];

        var raw = path.StartsWith("@") ? path[1..] : path;

        // Absolute path — starts with /
        if (raw.StartsWith("/"))
            return "@" + raw[1..]; // strip the leading / and re-add @

        // Already looks absolute (contains a colon with address before it, like OrgA/content:file)
        var colonIndex = raw.IndexOf(':');
        if (colonIndex > 0)
        {
            var beforeColon = raw[..colonIndex];
            if (beforeColon.Contains('/'))
                return path; // already absolute
        }
        else if (raw.Contains('/'))
        {
            // No colon, has slashes — check if it starts with a UCR prefix (content/file.md)
            var firstSlash = raw.IndexOf('/');
            if (firstSlash > 0)
            {
                var firstSegment = raw[..firstSlash];
                if (Data.UcrPrefixResolver.PrefixToAreaMap.ContainsKey(firstSegment))
                {
                    // Relative unified path like "content/My Report.md" — prepend context
                    var contextPath = chat.Context?.Context;
                    if (!string.IsNullOrEmpty(contextPath))
                        return $"@{contextPath}/{raw}";
                    return path;
                }
            }

            // Multi-segment path like "OrgA/Doc" — likely absolute already
            return path;
        }

        // Relative path — prepend context
        var contextPath2 = chat.Context?.Context;
        if (string.IsNullOrEmpty(contextPath2))
            return path; // no context, return as-is

        // For unified refs like "content:file.docx", prepend context as address
        if (colonIndex > 0)
            return $"@{contextPath2}/{raw}";

        // For simple names like "MyChild", prepend context
        return $"@{contextPath2}/{raw}";
    }

    /// <summary>
    /// Reads a node (or, for a <c>path/*</c> query, its direct children) and returns it as a
    /// JSON string. Resolves <c>@</c>/quote noise and Unified-Path prefixes first; surfaces a
    /// recorded NodeType compilation error alongside the node, or an <c>"Error: …"</c>/<c>"Not found: …"</c> string.
    /// </summary>
    /// <param name="path">The node path to read, or a <c>path/*</c> form to list children.</param>
    /// <returns>A cold observable emitting the JSON payload or a descriptive error string.</returns>
    public IObservable<string> Get(string path)
    {
        logger.LogInformation("Get called with path={Path}", path);

        if (string.IsNullOrWhiteSpace(path))
            return Observable.Return("Error: path is required.");

        var resolvedPath = ResolvePath(path);
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return Observable.Return("Error: path is required.");

        // Handle children query (path/*) — Query emits a QueryResultChange whose
        // Initial change contains every matching child in a single batch. Take(1) completes
        // the stream as soon as the first snapshot arrives; no await, no FromAsync bridge.
        if (resolvedPath.EndsWith("/*"))
        {
            var parentPath = resolvedPath[..^2];
            return mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{parentPath}"))
                .Take(1)
                .Select(change =>
                {
                    var list = change.Items
                        .Select(node => (object)new { node.Path, node.Name, node.NodeType, node.Icon })
                        .ToImmutableList();
                    return JsonSerializer.Serialize(list, hub.JsonSerializerOptions);
                })
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex, "Error getting data at path {Path}", resolvedPath);
                    return Observable.Return($"Error: {ex.Message}");
                });
        }

        // MCP-documented AREA-READ route `{path}/area/{Name[/Id]}` (McpMeshPlugin.Get docs).
        // Routed through the same one-shot client-hub subscription RenderArea uses
        // (GetRemoteStream + materialised-frame wait + Finally-dispose), so the payload is
        // the SETTLED control tree — never the base-frame shell a raw first emission
        // carries. Before this route existed, the path fell through to FetchNode on the
        // full "{path}/area/Name" string and always returned "Not found: …".
        if (TryParseAreaRoute(resolvedPath, out var areaNodePath, out var routeArea, out var routeAreaId))
            return GetAreaRoute(resolvedPath, areaNodePath, routeArea, routeAreaId);

        // Single-node content read via GetDataRequest + MeshNodeReference + RegisterCallback.
        // See Doc/Architecture/CqrsAndContentAccess.md — queries are for sets only.
        return TryResolveUnifiedPath(resolvedPath)
            .SelectMany(unified =>
            {
                // A unified (UCR) interpretation that ERRORS may be shadowing a real node whose
                // own path contains a UCR keyword as an interior segment: Document nodes live at
                // {collection}/_Documents/{slug}, and the collection segment ("content") is a UCR
                // keyword — the unified read hijacks the path and fails with "Content collection
                // '_Documents' not found". The node namespace is authoritative: on a unified
                // error, fall back to the node read and surface the unified error only when no
                // node exists at the full path either.
                var unifiedError = unified is not null
                    && unified.StartsWith("Error:", StringComparison.Ordinal) ? unified : null;
                if (unified is not null && unifiedError is null)
                    return Observable.Return(unified);

                return FetchNode(resolvedPath).SelectMany(node =>
                {
                    if (node is null)
                        return unifiedError is not null
                            ? Observable.Return(unifiedError)
                            : GetWithBrokenNodeTypeFallback(resolvedPath);
                    return LookupCompilationError(node)
                        .Select(compileError => compileError != null
                            ? JsonSerializer.Serialize(
                                new { node, compilationError = compileError },
                                hub.JsonSerializerOptions)
                            : JsonSerializer.Serialize(node, hub.JsonSerializerOptions));
                });
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Error getting data at path {Path}", resolvedPath);
                return Observable.Return($"Error: {ex.Message}");
            });
    }

    /// <summary>
    /// Fallback when <see cref="FetchNode"/> returned <c>null</c> — typically a
    /// broken-NodeType path whose per-node hub couldn't activate (compilation
    /// failed) and whose <see cref="GetDataRequest"/> timed out after 10s.
    ///
    /// <para>If the NodeType MeshNode carries a recorded
    /// <see cref="Graph.Configuration.NodeTypeDefinition.CompilationError"/>,
    /// the catalog still has the node's stored definition
    /// even though its hub is broken. We read it via a one-shot
    /// <c>Query</c> snapshot as the documented exception to
    /// "queries are for sets only" (see <c>Doc/Architecture/CqrsAndContentAccess.md</c>):
    /// the live content is unreachable, the catalog snapshot is the best we
    /// have, and the wrapped response surfaces the compile error so callers
    /// (code-authoring agents, MCP, UI overlays) can fix the source instead of seeing a
    /// generic "Not found".</para>
    /// </summary>
    private IObservable<string> GetWithBrokenNodeTypeFallback(string resolvedPath)
    {
        // Read the NodeType MeshNode directly — the snapshot carries the
        // CompilationError if compilation has failed at least once.
        return hub.GetWorkspace().GetMeshNodeStream(resolvedPath)
            .Where(n => n is not null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .SelectMany(node =>
            {
                var compileError = node.ContentAs<Graph.Configuration.NodeTypeDefinition>(hub.JsonSerializerOptions)?.CompilationError;
                if (string.IsNullOrEmpty(compileError))
                    return Observable.Return($"Not found: {resolvedPath}");

                // Live Query — first emission carries the snapshot; the catalog
                // is the source of truth here (the per-node hub is broken by
                // definition, so live content is unreachable).
                return mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{resolvedPath}"))
                    .Select(c => c.Items.FirstOrDefault())
                    .Select(qn => qn is null
                        ? $"Not found: {resolvedPath}"
                        : JsonSerializer.Serialize(
                            new { node = qn, compilationError = compileError },
                            hub.JsonSerializerOptions))
                    .Catch((Exception ex) =>
                    {
                        logger.LogWarning(ex,
                            "Catalog fallback for broken NodeType at {Path} failed", resolvedPath);
                        return Observable.Return(JsonSerializer.Serialize(
                            new { compilationError = compileError, error = "Catalog read failed: " + ex.Message },
                            hub.JsonSerializerOptions));
                    });
            });
    }

    /// <summary>
    /// Renders a layout area of the node at <paramref name="path"/> and returns the first
    /// fully-materialised <c>{areas, data}</c> frame as raw JSON — byte-identical to the Full
    /// <c>DataChangedEvent</c> snapshot a live gRPC/SignalR client folds (same hub serializer
    /// options: <c>$type</c> discriminators, JSON-encoded <c>InstanceCollection</c> keys), so an
    /// SSR consumer can seed its client-side area source without translation.
    ///
    /// <para>Uses the SAME server-side primitive the Blazor portal binds a remote area with
    /// (<c>GetRemoteStream&lt;JsonElement, LayoutAreaReference&gt;</c> — see
    /// <c>LayoutAreaView.BindStream</c>), waits until the requested area's control has
    /// materialised (following the default-area <c>areas[""]</c> NamedArea indirection the base
    /// frame statically seeds), takes that single frame, and DISPOSES the stream via
    /// <c>Finally</c> — the subscription never outlives the call on any path: completion,
    /// fault, timeout, or caller unsubscribe (HTTP client abort).</para>
    ///
    /// <para>Error contract: <c>"Not found: …"</c> when the path resolves to nothing,
    /// <c>"Error: …"</c> for failures surfaced by the subscribe (an RLS denial faults the
    /// remote stream with "Access denied" — see <c>HubSubscriptionSecurityTest</c>). A timeout
    /// faults the observable with <see cref="TimeoutException"/> so transports can map it
    /// distinctly (REST maps it to a 504-style JSON error instead of the sentinel).</para>
    /// </summary>
    /// <param name="path">Node path, or URL-shaped <c>{path}/{area}/{id}</c> — the unmatched
    /// remainder fills area/id when they are not passed explicitly (same split as <c>AreaPage</c>).</param>
    /// <param name="area">Layout area name; null/empty renders the node's DEFAULT area (the
    /// frame then carries the <c>areas[""]</c> indirection to the resolved area).</param>
    /// <param name="id">Optional area instance id.</param>
    /// <param name="timeoutSeconds">Budget covering path resolution + the first materialised frame.</param>
    /// <returns>A cold observable emitting the wire JSON frame or a descriptive error string;
    /// faults with <see cref="TimeoutException"/> when the budget elapses.</returns>
    public IObservable<string> RenderArea(string path, string? area = null, string? id = null, int timeoutSeconds = 30)
    {
        logger.LogInformation("RenderArea called with path={Path} area={Area} id={Id}", path, area, id);

        if (string.IsNullOrWhiteSpace(path))
            return Observable.Return("Error: path is required.");

        var resolvedPath = ResolvePath(path).Trim('/');
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return Observable.Return("Error: path is required.");

        var pathResolver = hub.ServiceProvider.GetRequiredService<IPathResolver>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return Observable.Defer(() =>
        {
            // Capture the CALLER's ambient identity at subscribe time (the transport request
            // thread / test context — same precedence the sync-stream uses). The remote-stream
            // creation below happens inside a reactive continuation on a hub scheduler where the
            // AsyncLocal context is wiped or hub-shaped; without re-applying the captured user
            // there, the SubscribeRequest would fall back to the System identity and BYPASS the
            // owner's RLS read gate — a denied caller would receive the rendered content.
            var caller = accessService?.Context ?? accessService?.CircuitContext;
            return pathResolver.ResolvePath(resolvedPath)
                .Take(1)
                .SelectMany(resolution => RenderResolvedArea(resolution, resolvedPath, area, id, caller));
        })
            .Timeout(TimeSpan.FromSeconds(timeoutSeconds))
            .Catch((Exception ex) =>
            {
                // A timeout stays a FAULT (transports map it to a gateway timeout); everything
                // else surfaces through the standard "Error: …" sentinel contract.
                if (ex is TimeoutException)
                    return Observable.Throw<string>(ex);
                logger.LogWarning(ex, "RenderArea failed for {Path}", resolvedPath);
                return Observable.Return($"Error: {ex.Message}");
            });
    }

    /// <summary>
    /// Second half of <see cref="RenderArea"/>: builds the <see cref="LayoutAreaReference"/> from
    /// the resolution + explicit arguments and opens the one-shot area stream under the captured
    /// caller identity.
    /// </summary>
    private IObservable<string> RenderResolvedArea(
        AddressResolution? resolution,
        string resolvedPath,
        string? area,
        string? id,
        AccessContext? caller)
    {
        if (resolution is null)
            return Observable.Return($"Not found: {resolvedPath}");

        // Path resolution falls back to the CLOSEST ANCESTOR, leaving unmatched segments
        // as the remainder. When the caller passed area/id explicitly, a non-empty
        // remainder means the NODE path itself did not resolve (e.g. "TestData/garbage"
        // resolved to "TestData") — surface Not found rather than silently rendering the
        // ancestor's area (the routing-fallback hazard FetchNode guards against too).
        if (!string.IsNullOrEmpty(resolution.Remainder)
            && (!string.IsNullOrEmpty(area) || !string.IsNullOrEmpty(id)))
            return Observable.Return($"Not found: {resolvedPath}");

        // Otherwise a URL-shaped path carries "{area}/{id}" as the remainder — the same
        // split the Blazor AreaPage applies (an unknown segment renders the framework's
        // visible "Area not found" control, exactly like the portal URL would).
        var (remainderArea, remainderId) = SplitAreaRemainder(resolution.Remainder);
        var effectiveArea = string.IsNullOrEmpty(area) ? remainderArea : area;
        var effectiveId = string.IsNullOrEmpty(id) ? remainderId : id;
        var reference = new LayoutAreaReference(
            string.IsNullOrEmpty(effectiveArea) ? null : effectiveArea)
        {
            Id = effectiveId ?? ""
        };

        // Defer so the stream opens on Subscribe (cold — no work without a subscriber)
        // and Finally guarantees disposal on every terminal path.
        return Observable.Defer(() =>
        {
            // This factory runs inside a reactive continuation where the ambient AsyncLocal
            // identity is NOT the caller's. GetRemoteStream captures the ambient AccessContext
            // at stream creation (it stamps the SubscribeRequest's identity), so re-apply the
            // caller captured at the RenderArea boundary for the creation scope — otherwise the
            // subscribe would fall back to System and bypass the owner's RLS read gate.
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            using var callerScope = caller is not null
                ? accessService?.SwitchAccessContext(caller)
                : null;
            var stream = hub.GetWorkspace()
                .GetRemoteStream<JsonElement, LayoutAreaReference>(
                    (Address)resolution.Prefix, reference);
            if (stream is null)
                return Observable.Return(
                    $"Error: could not open a layout-area stream for {resolvedPath}.");
            return stream
                .Where(ci => IsAreaMaterialized(ci.Value, reference.Area))
                .Take(1)
                .Select(ci => ci.Value.GetRawText())
                .Finally(stream.Dispose);
        });
    }

    /// <summary>
    /// Parses the MCP-documented area-read route <c>{nodePath}/area/{Name[/Id]}</c>.
    /// The marker is the LAST exact <c>area</c> segment (ordinal, the documented casing) so
    /// a node path that itself contains an <c>area</c> segment still yields the deepest —
    /// i.e. correct — node-path/area split. The legacy colon form <c>{path}/area:Name</c> is
    /// deliberately NOT parsed here: it keeps flowing through <see cref="TryResolveUnifiedPath"/>
    /// unchanged (it supports UCR-special forms like <c>area:content:file</c> that map to
    /// <c>$Content</c> areas downstream).
    /// </summary>
    private static bool TryParseAreaRoute(
        string resolvedPath, out string nodePath, out string? area, out string? id)
    {
        nodePath = string.Empty;
        area = null;
        id = null;

        if (resolvedPath.Contains(':'))
            return false; // legacy colon form — handled by TryResolveUnifiedPath

        var segments = resolvedPath.Split('/');
        for (var i = segments.Length - 1; i > 0; i--)
        {
            if (!string.Equals(segments[i], "area", StringComparison.Ordinal))
                continue;

            nodePath = string.Join('/', segments.Take(i));
            if (string.IsNullOrEmpty(nodePath.Trim('/')))
                return false; // no node path before the marker — not the area route

            var rest = segments.Skip(i + 1).ToImmutableList();
            area = rest.Count > 0 && rest[0].Length > 0 ? rest[0] : null;
            var restId = rest.Count > 1 ? string.Join('/', rest.Skip(1)).Trim('/') : string.Empty;
            id = restId.Length > 0 ? restId : null;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Serves the MCP <c>get {path}/area/{Name}</c> route: renders through the settled
    /// <see cref="RenderArea"/> pipeline, mapping a fault (incl. the timeout) onto the Get
    /// contract's <c>"Error: …"</c> string. When the area interpretation fails, a REAL node
    /// at the FULL path is authoritative and wins (the same interior-keyword-shadowing rule
    /// the UCR <c>content</c> fallback applies — see <c>DocumentNodeGetTest</c>); the area
    /// error surfaces only when no such node exists.
    /// </summary>
    private IObservable<string> GetAreaRoute(
        string fullPath, string nodePath, string? area, string? id) =>
        RenderArea(nodePath, area, id)
            .Catch((Exception ex) => Observable.Return($"Error: {ex.Message}"))
            .SelectMany(result =>
            {
                if (!result.StartsWith("Not found:", StringComparison.Ordinal)
                    && !result.StartsWith("Error:", StringComparison.Ordinal))
                    return Observable.Return(result);
                return FetchNode(fullPath).Select(node => node is null
                    ? result
                    : JsonSerializer.Serialize(node, hub.JsonSerializerOptions));
            });

    /// <summary>
    /// Splits a path-resolution remainder into <c>(area, id)</c> — the same
    /// <c>{area}/{id}</c> split and <c>%9Y</c> decode the Blazor <c>AreaPage</c> applies.
    /// </summary>
    private static (string? Area, string? Id) SplitAreaRemainder(string? remainder)
    {
        if (string.IsNullOrEmpty(remainder))
            return (null, null);
        var slashIndex = remainder.IndexOf('/');
        var (rawArea, rawId) = slashIndex >= 0
            ? (remainder[..slashIndex], remainder[(slashIndex + 1)..])
            : (remainder, (string?)null);
        return (
            (string?)WorkspaceReference.Decode(rawArea),
            rawId is null ? null : (string?)WorkspaceReference.Decode(rawId));
    }

    /// <summary>
    /// True when the frame carries the requested area's rendered control. The base frame a
    /// layout-area subscription emits first is a shell (progress marker + — for default-area
    /// subscriptions — the statically-seeded <c>areas[""]</c> NamedArea indirection, see
    /// <c>LayoutAreaHost.BuildInitialization</c>); first-paint fidelity means waiting for the
    /// frame where the resolved area's own control has landed. <c>InstanceCollection</c> keys
    /// ride JSON-encoded on the wire (<c>"Overview"</c> → property <c>"\"Overview\""</c>).
    /// </summary>
    private static bool IsAreaMaterialized(JsonElement store, string? requestedArea)
    {
        if (store.ValueKind != JsonValueKind.Object
            || !store.TryGetProperty(LayoutAreaReference.Areas, out var areas)
            || areas.ValueKind != JsonValueKind.Object)
            return false;

        var rootKey = requestedArea ?? string.Empty;
        if (!TryGetWireInstance(areas, rootKey, out var control))
            return false;

        // Default-area subscription: areas[""] points at the resolved area — require the
        // resolved area's control too, otherwise the SSR seed would render an empty indirection.
        if (rootKey.Length == 0
            && control.ValueKind == JsonValueKind.Object
            && (control.TryGetProperty("area", out var resolved)
                || control.TryGetProperty("Area", out resolved))
            && resolved.ValueKind == JsonValueKind.String
            && resolved.GetString() is { Length: > 0 } resolvedArea)
            return TryGetWireInstance(areas, resolvedArea, out _);

        return true;
    }

    /// <summary>Reads one instance from a wire <c>InstanceCollection</c> object (JSON-encoded keys).</summary>
    private static bool TryGetWireInstance(JsonElement collection, string key, out JsonElement value)
    {
        if (collection.TryGetProperty(JsonSerializer.Serialize(key), out value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            return true;
        value = default;
        return false;
    }

    /// <summary>
    /// One-shot read of the MeshNode at <paramref name="resolvedPath"/> via the
    /// owning per-node hub's <c>MeshNodeReference</c> reducer — the authoritative
    /// source of truth, no catalog lag. <c>GetDataRequest</c> activates the cold
    /// per-node hub on receipt; the response carries the live MeshNode.
    /// Returns <c>null</c> on timeout or routing failure (node does not exist /
    /// hub couldn't be activated). See <c>Doc/Architecture/CqrsAndContentAccess.md</c>.
    /// </summary>
    private IObservable<MeshNode?> FetchNode(string resolvedPath, int timeoutSeconds = 10) =>
        Observable.Create<MeshNode?>(observer =>
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var emitted = 0;
            // 🚨 Capture the inner hub.Observe subscription so disposal tears
            // down the hub-level callback. Without this, a CTS timeout or
            // outer-subscriber early dispose leaves the GetDataRequest's
            // pending-callback entry in the hub's responseSubjects dict until
            // the framework's RequestTimeout (~30s). The test base's
            // Quiescing-budget watchdog flags it as a leak — the exact
            // failure signature behind FullCrudWorkflow_CreateGetUpdateDelete's
            // CI flake (`GetDataRequest@ACME/CrudTest_…(17001ms)` pending).
            // Matches the GetMeshNode shape in MeshNodeStreamExtensions.cs.
            IDisposable? innerSubscription = null;

            void EmitOnce(MeshNode? node)
            {
                if (Interlocked.Exchange(ref emitted, 1) != 0) return;
                observer.OnNext(node);
                observer.OnCompleted();
            }

            cts.Token.Register(() => EmitOnce(null));

            try
            {
                // 🚨 Read the LIVE node via GetMeshNodeStream (the shared in-memory cache
                // mirror), NOT a one-shot GetDataRequest. A GetDataRequest activates a cold
                // per-node hub that loads from PERSISTENCE — stale when a recent write's
                // debounced save hasn't flushed: the in-memory mirror already holds the
                // update, persistence does not. Paired with the read-your-writes wait in
                // Patch/Update (which freshens this same mirror to the written version), a
                // read immediately after a write sees the fresh value. GetMeshNodeStream also
                // activates a cold hub on subscribe. See Doc/Architecture/CqrsAndContentAccess.md.
                // The CTS timeout above maps a never-emitting (non-existent) node to null.
                innerSubscription = hub.GetWorkspace().GetMeshNodeStream(resolvedPath)
                    // Routing-fallback safety: a path with no per-node hub can route to the
                    // closest ancestor, which returns ITS node. Filter by exact path so
                    // callers (Patch / Update / Delete) never operate on an ancestor.
                    .Where(n => n is not null
                        && string.Equals(n.Path, resolvedPath, StringComparison.OrdinalIgnoreCase))
                    .Take(1)
                    .Subscribe(
                        node => EmitOnce(node),
                        ex =>
                        {
                            // DeliveryFailure or other error — node not found / unreachable.
                            logger.LogDebug(ex, "FetchNode read failed for {Path}", resolvedPath);
                            EmitOnce(null);
                        });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FetchNode read setup failed for {Path}", resolvedPath);
                EmitOnce(null);
            }

            return Disposable.Create(() =>
            {
                innerSubscription?.Dispose();
                cts.Dispose();
            });
        });

    /// <summary>
    /// Read-your-writes barrier: waits (bounded) for the live mesh-node mirror at
    /// <paramref name="path"/> to advance PAST <paramref name="versionBefore"/> — the
    /// version observed before the write. The owning hub stamps a fresh, higher Version
    /// when it applies a change, so a mirror version strictly greater than the pre-write
    /// value means the reconciled update has propagated and a subsequent read will see it.
    /// Best-effort: on timeout it emits <c>null</c> so the caller falls back to the
    /// optimistic node rather than failing the write. Subscribes to the cache mirror's
    /// READ stream (never the per-path Update queue), so it cannot deadlock — unlike the
    /// removed in-queue echo-wait.
    /// </summary>
    private IObservable<MeshNode?> WaitForReadYourWrites(string path, long versionBefore) =>
        hub.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null && n.Version > versionBefore)
            .Take(1)
            .Select(n => (MeshNode?)n)
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null));

    /// <summary>
    /// Writes a full <see cref="MeshNode"/> to the node's own hub via
    /// <see cref="DataChangeRequest"/>. The target hub's data-change handler applies
    /// the update to its workspace (ticking the <c>MeshNodeReference</c> stream so
    /// subsequent <see cref="GetDataRequest"/> sees the new value) and persists via
    /// its data source. Emits the saved node on success.
    /// </summary>
    private IObservable<MeshNode> UpdateViaDataChange(MeshNode node, int timeoutSeconds = 10) =>
        Observable.Create<MeshNode>(observer =>
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var completed = 0;

            void Fail(Exception ex)
            {
                if (Interlocked.Exchange(ref completed, 1) != 0) return;
                observer.OnError(ex);
            }

            void Emit(MeshNode n)
            {
                if (Interlocked.Exchange(ref completed, 1) != 0) return;
                observer.OnNext(n);
                observer.OnCompleted();
            }

            // 🚨 Capture the inner Subscribe so disposal removes the
            // hub-level pending callback. See FetchNode for the failure
            // mode this avoids (test-base Quiescing leak detection trips
            // on the orphaned callback entry).
            IDisposable? innerSubscription = null;

            try
            {
                var delivery = hub.Post(
                    DataChangeRequest.Update([node]),
                    o => o.WithTarget(new Address(node.Path)))!;

                innerSubscription = hub.Observe(delivery)
                    .Subscribe(
                        d =>
                        {
                            if (d.Message is DataChangeResponse resp)
                            {
                                if (resp.Status == DataChangeStatus.Committed)
                                    Emit(node with { Version = resp.Version });
                                else
                                    Fail(new InvalidOperationException(
                                        $"DataChangeRequest rejected for {node.Path}: {resp.Log?.Status}"));
                            }
                            else
                            {
                                Fail(new InvalidOperationException(
                                    $"Unexpected response {d.Message?.GetType().Name} for DataChangeRequest at {node.Path}"));
                            }
                        },
                        ex => Fail(new InvalidOperationException(
                            ex.Message ?? $"Delivery failed to {node.Path}", ex)));

                cts.Token.Register(() => Fail(new TimeoutException(
                    $"DataChangeRequest for {node.Path} did not complete within {timeoutSeconds}s.")));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "UpdateViaDataChange: Post/RegisterCallback failed for {Path}", node.Path);
                Fail(ex);
            }

            return () =>
            {
                innerSubscription?.Dispose();
                cts.Dispose();
            };
        });

    /// <summary>
    /// Tries to resolve a path as a Unified Path with prefix (schema/, model/, data/, content/).
    /// Supports both legacy colon format (address/prefix:path) and new slash format (address/prefix/path).
    /// Parses the path to find the prefix, splits into address and remainder,
    /// then routes data request to the resolved address.
    /// Emits <c>null</c> if the path is not a Unified Path; emits a JSON / error string otherwise.
    /// </summary>
    private IObservable<string?> TryResolveUnifiedPath(string resolvedPath)
    {
        string? addressPart = null;
        string? remainder = null;

        // Try legacy colon format first: address/prefix:path
        var colonIndex = resolvedPath.IndexOf(':');
        if (colonIndex > 0)
        {
            var slashBeforeColon = resolvedPath.LastIndexOf('/', colonIndex);
            if (slashBeforeColon >= 0)
            {
                addressPart = resolvedPath[..slashBeforeColon];
                remainder = resolvedPath[(slashBeforeColon + 1)..];
            }
        }

        // Try new slash format: address/prefix/path where prefix is a known UCR keyword.
        // `layoutAreas` is the MCP-documented listing route (`{path}/layoutAreas/`): it is
        // not in the UCR prefix map (it maps to no $-area), but the per-node hub answers
        // GetDataRequest(UnifiedReference("layoutAreas…")) directly via the layout plugin's
        // HandleLayoutAreasRequest — so route it like any unified segment. Without this the
        // path fell through to FetchNode("{path}/layoutAreas") → always "Not found: …".
        if (addressPart == null)
        {
            var segments = resolvedPath.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                if (UcrPrefixResolver.PrefixToAreaMap.ContainsKey(segments[i])
                    || string.Equals(segments[i], "layoutAreas", StringComparison.Ordinal))
                {
                    if (i > 0)
                    {
                        addressPart = string.Join("/", segments.Take(i));
                        remainder = string.Join("/", segments.Skip(i));
                    }
                    else
                    {
                        addressPart = null;
                        remainder = resolvedPath;
                    }
                    break;
                }
            }
        }

        if (remainder == null)
            return Observable.Return<string?>(null);

        var reference = new UnifiedReference(remainder);
        var address = !string.IsNullOrEmpty(addressPart) ? new Address(addressPart) : hub.Address;

        logger.LogInformation("Resolving Unified Path: address={Address}, remainder={Remainder}",
            addressPart, remainder);

        // Fire the GetDataRequest and receive the response via RegisterCallback.
        // Observable.Create wraps the post/register pair so the caller can compose it
        // into the Get pipeline without ever awaiting — the callback completes the
        // observable from a non-hub thread. The outer Timeout enforces an upper
        // bound on the response wait so missing hubs (e.g. a UCR path whose
        // address segment doesn't have a running per-node hub) surface as an
        // "Error: …" string instead of hanging the whole Get pipeline.
        return Observable.Create<string?>(observer =>
        {
            // 🚨 Capture inner Subscribe for proper teardown. Same leak class
            // as FetchNode — outer Timeout cancellation tore down the
            // observer's chain but left the hub-level callback registered.
            IDisposable? innerSubscription = null;
            try
            {
                var delivery = hub.Post(
                    new GetDataRequest(reference),
                    o => o.WithTarget(address))!;

                innerSubscription = hub.Observe(delivery)
                    .Subscribe(
                        d =>
                        {
                            try
                            {
                                if (d.Message is GetDataResponse responseMsg)
                                {
                                    if (responseMsg.Error != null)
                                        observer.OnNext($"Error: {responseMsg.Error}");
                                    else
                                        observer.OnNext(JsonSerializer.Serialize(responseMsg.Data, hub.JsonSerializerOptions));
                                }
                                else
                                {
                                    observer.OnNext($"Error: Unexpected response type {d.Message?.GetType().Name} for {remainder} at {addressPart}");
                                }
                                observer.OnCompleted();
                            }
                            catch (Exception ex)
                            {
                                observer.OnError(ex);
                            }
                        },
                        ex =>
                        {
                            observer.OnNext($"Error: {ex.Message ?? "Delivery failed to " + addressPart}");
                            observer.OnCompleted();
                        });
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
            }

            return () => innerSubscription?.Dispose();
        })
        .Timeout(TimeSpan.FromSeconds(10))
        .Catch((TimeoutException _) =>
            Observable.Return<string?>($"Error: Timeout resolving '{remainder}' at {addressPart}"));
    }

    /// <summary>
    /// Runs a mesh query and returns a JSON envelope
    /// <c>{count, limit, truncated, results:[{path,name,nodeType,version,lastModified}]}</c>.
    /// When <paramref name="basePath"/> is set the query is scoped to that namespace; <c>truncated</c> flags that
    /// more matches exist than were returned.
    /// </summary>
    /// <param name="query">The GitHub-style query string (e.g. <c>nodeType:Agent name:*sales*</c>).</param>
    /// <param name="basePath">Optional namespace to scope the search to; <c>null</c> searches everywhere.</param>
    /// <param name="limit">Maximum number of results, clamped to 1..200 (default 50).</param>
    /// <returns>A cold observable emitting the JSON results envelope or an <c>"Error: …"</c> string.</returns>
    public IObservable<string> Search(string query, string? basePath = null, int limit = 50)
    {
        logger.LogInformation("Search called with query={Query}, basePath={BasePath}, limit={Limit}", query, basePath, limit);

        limit = Math.Clamp(limit, 1, 200);

        var resolvedBase = basePath != null ? ResolvePath(basePath) : null;
        string fullQuery;
        if (string.IsNullOrEmpty(resolvedBase))
        {
            fullQuery = query;
        }
        else
        {
            var cleanQuery = query.Replace("namespace:", "").Trim();
            fullQuery = $"namespace:{resolvedBase} {cleanQuery}".Trim();
        }

        // Snapshot semantics: Take(1) on Query gives us the Initial change
        // containing every match for this query in one batch — no async enumeration,
        // no FromAsync bridge.
        return mesh.Query<MeshNode>(new MeshQueryRequest { Query = fullQuery, Limit = limit })
            .Take(1)
            .Select(change =>
            {
                // Version + LastModified ride along so remote consumers (the instance-sync
                // pull sweep) can detect changed nodes from the listing alone.
                var list = change.Items
                    .Select(node => (object)new { node.Path, node.Name, node.NodeType, node.Version, node.LastModified })
                    .ToImmutableList();
                // Envelope instead of a bare array so truncation is VISIBLE: a result
                // set that silently stops at the limit reads as "that's everything"
                // and the agent under-reports. Composed explicitly via JsonObject —
                // the hub serializer options drop empty collections, which would strip
                // the 'results' key from a zero-hit response and break consumers.
                var truncated = list.Count >= limit;
                var payload = new JsonObject
                {
                    ["count"] = list.Count,
                    ["limit"] = limit,
                    ["truncated"] = truncated,
                    ["results"] = JsonSerializer.SerializeToNode(list, hub.JsonSerializerOptions) ?? new JsonArray(),
                };
                if (truncated)
                    payload["hint"] =
                        "Result set hit the limit — there may be more matches. Narrow the query (namespace:/nodeType:/name:) or raise 'limit' (max 200).";
                return payload.ToJsonString();
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Error searching with query {Query}", query);
                return Observable.Return($"Error: {ex.Message}");
            });
    }

    /// <summary>
    /// Full-node query for remote GUI shells — the transport twin of the Blazor shell's
    /// <c>IMeshService.Query&lt;MeshNode&gt;</c> reads (search-bar suggestions, the notification
    /// bell). Unlike <see cref="Search"/> (which projects to path/name/nodeType for agents), this
    /// returns the matched nodes with every shell field, icon and content, serialized with the hub
    /// options (<c>$type</c> discriminators), so the shell can render suggestion icons and
    /// notification state without a per-row Get round-trip. Snapshot semantics: <c>Take(1)</c>
    /// yields the Initial batch, exactly like <see cref="Search"/>.
    /// </summary>
    /// <param name="query">Mesh query string (same syntax as <see cref="Search"/>).</param>
    /// <param name="limit">Maximum rows (clamped 1–200).</param>
    /// <returns>A cold observable emitting <c>{"count":N,"results":[MeshNode…]}</c> or an
    /// <c>"Error: …"</c> sentinel.</returns>
    public IObservable<string> QueryNodes(string query, int limit = 50)
    {
        // Debug, not Information: this verb backs per-keystroke browser UX (search suggestions,
        // the notification bell) — Information would ship a line per keystroke to Loki.
        logger.LogDebug("QueryNodes called with query={Query}, limit={Limit}", query, limit);

        limit = Math.Clamp(limit, 1, 200);

        return mesh.Query<MeshNode>(new MeshQueryRequest { Query = query, Limit = limit })
            .Take(1)
            .Select(change =>
            {
                var items = change.Items.Take(limit).ToImmutableList();
                var payload = new JsonObject
                {
                    ["count"] = items.Count,
                    ["results"] = JsonSerializer.SerializeToNode(items, hub.JsonSerializerOptions) ?? new JsonArray(),
                };
                return payload.ToJsonString();
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Error querying nodes with query {Query}", query);
                return Observable.Return($"Error: {ex.Message}");
            });
    }

    /// <summary>
    /// Resolves a URL-shaped path into its node address and layout-area remainder — the transport
    /// twin of the Blazor GUI's <c>IPathResolver.ResolveNavigationPath</c> (the same split
    /// <c>ApplicationPage</c> applies to <c>/{path}/{area}/{id}</c> URLs). A remote shell calls this
    /// once per navigation so its live area subscription targets the actual node hub instead of the
    /// raw URL path.
    /// </summary>
    /// <param name="path">The URL path to resolve (UCR <c>@/…</c> accepted).</param>
    /// <returns>A cold observable emitting <c>{"prefix":"…","remainder":"…"}</c>, a
    /// <c>"Not found: …"</c> sentinel when nothing matches, or <c>"Error: …"</c>.</returns>
    public IObservable<string> Resolve(string path)
    {
        // Debug, not Information: called once per GUI navigation — too hot for Loki.
        logger.LogDebug("Resolve called with path={Path}", path);

        if (string.IsNullOrWhiteSpace(path))
            return Observable.Return("Error: path is required.");

        var resolvedPath = ResolvePath(path).Trim('/');
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return Observable.Return("Error: path is required.");

        var pathResolver = hub.ServiceProvider.GetRequiredService<IPathResolver>();
        return pathResolver.ResolveNavigationPath(resolvedPath)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Select(resolution => resolution is null
                ? $"Not found: {resolvedPath}"
                : new JsonObject
                {
                    ["prefix"] = resolution.Prefix,
                    ["remainder"] = resolution.Remainder,
                }.ToJsonString())
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Resolve failed for {Path}", resolvedPath);
                return Observable.Return($"Error: {ex.Message}");
            });
    }

    /// <summary>
    /// Deserialises a single MeshNode from JSON (repairing common LLM JSON defects), validates its
    /// identity and content schema, then creates it in the mesh.
    /// </summary>
    /// <param name="node">The JSON MeshNode to create (requires id, name, nodeType, namespace).</param>
    /// <returns>A cold observable emitting <c>"Created: {path}"</c> or a descriptive error string.</returns>
    public IObservable<string> Create(string node)
    {
        logger.LogInformation("Create called");

        return Observable.Defer(() =>
        {
            MeshNode? meshNode;
            try
            {
                var sanitized = RepairJson(node);
                meshNode = JsonSerializer.Deserialize<MeshNode>(sanitized, hub.JsonSerializerOptions);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Create: invalid JSON, length={Length}", node.Length);
                return Observable.Return(
                    $"Invalid JSON: {ex.Message}. Tip: ensure all quotes and special characters in markdown content are properly escaped for JSON strings.");
            }

            if (meshNode == null)
                return Observable.Return("Invalid node: deserialized to null.");

            meshNode = SanitizeNodeId(meshNode);
            meshNode = NormalizeNamespace(meshNode);

            var identityError = ValidateNodeIdentity(meshNode, "create");
            if (identityError != null)
                return Observable.Return(identityError);

            // Validate content against schema when content is provided.
            var validationObs = meshNode.Content != null
                ? ValidateContentWithSchema(meshNode)
                : Observable.Return<string?>(null);

            return validationObs.SelectMany(validationError =>
                validationError != null
                    ? Observable.Return(validationError)
                    : mesh.CreateNode(meshNode)
                .Select(created =>
                {
                    OnNodeChange?.Invoke(new NodeChangeEntry
                    {
                        Path = created.Path,
                        Operation = "Created",
                        VersionBefore = null,
                        VersionAfter = created.Version,
                        NodeType = created.NodeType,
                        NodeName = created.Name
                    });
                    return $"Created: {created.Path}";
                })
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex, "Error creating node");
                    return Observable.Return($"Error creating node: {ex.Message}");
                }));
        });
    }

    /// <summary>
    /// Full-replacement update of one or more existing nodes from a JSON array. Each node is validated and
    /// written independently (results combine in input order); a read-your-writes barrier ensures a
    /// following read sees the reconciled state.
    /// </summary>
    /// <param name="nodes">A JSON array of complete MeshNode objects to write.</param>
    /// <returns>A cold observable emitting a newline-joined per-node result/error summary.</returns>
    public IObservable<string> Update(string nodes)
    {
        logger.LogInformation("Update called");

        return Observable.Defer(() =>
        {
            List<MeshNode>? nodeList;
            try
            {
                var sanitized = RepairJson(nodes);
                nodeList = JsonSerializer.Deserialize<List<MeshNode>>(sanitized, hub.JsonSerializerOptions);
            }
            catch (JsonException ex)
            {
                return Observable.Return($"Invalid JSON: {ex.Message}");
            }

            if (nodeList == null || nodeList.Count == 0)
                return Observable.Return("No nodes provided.");

            // Validate each node up-front and spawn per-node UpdateNode observables for the rest.
            // Per-node outputs combine in input order via Concat so the caller sees a deterministic
            // result string even for batches.
            var perNode = ImmutableList<IObservable<string>>.Empty;
            foreach (var rawNode in nodeList)
            {
                if (rawNode == null)
                {
                    perNode = perNode.Add(Observable.Return(
                        "Error: array contained a null entry. Each array element must be a complete MeshNode JSON object."));
                    continue;
                }

                var meshNode = NormalizeNamespace(SanitizeNodeId(rawNode));

                var identityError = ValidateNodeIdentity(meshNode, "update");
                if (identityError != null)
                {
                    perNode = perNode.Add(Observable.Return(identityError));
                    continue;
                }

                if (meshNode.Content == null)
                {
                    perNode = perNode.Add(BuildNullContentError(meshNode.Path, meshNode.NodeType!));
                    continue;
                }

                var versionBefore = meshNode.Version;
                var currentPath = meshNode.Path;
                var nodeForCapture = meshNode;
                perNode = perNode.Add(
                    ValidateContentWithSchema(nodeForCapture).SelectMany(validationError =>
                        validationError != null
                            ? Observable.Return(validationError)
                            : mesh.UpdateNode(nodeForCapture)
                                // Read-your-writes barrier (see Patch): wait for the live
                                // mirror to advance PAST the optimistic version before
                                // returning, so a follow-up Get sees the reconciled update.
                                .SelectMany(updated => WaitForReadYourWrites(currentPath, updated.Version)
                                    .Select(confirmed =>
                                    {
                                        var after = confirmed ?? updated;
                                        OnNodeChange?.Invoke(new NodeChangeEntry
                                        {
                                            Path = after.Path,
                                            Operation = "Updated",
                                            VersionBefore = versionBefore,
                                            VersionAfter = after.Version,
                                            NodeType = after.NodeType,
                                            NodeName = after.Name
                                        });
                                        return $"Updated: {after.Path}";
                                    }))
                                .Catch((Exception ex) =>
                                    Observable.Return($"Error updating {currentPath}: {ex.Message}"))));
            }

            return perNode
                .ToObservable()
                .Concat()
                .ToList()
                .Select(lines => string.Join("\n", lines))
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex, "Error updating nodes");
                    return Observable.Return($"Error: {ex.Message}");
                });
        });
    }

    /// <summary>
    /// Pretty-prints a MeshNode for inclusion in diff output. Indented JSON keeps each
    /// field on its own line so the unified diff shows field-level changes rather than
    /// one massive minified line.
    /// </summary>
    private string SerialisePretty(MeshNode node) =>
        JsonSerializer.Serialize(node, new JsonSerializerOptions(hub.JsonSerializerOptions) { WriteIndented = true });

    /// <summary>
    /// Partial update of a single node: only the keys present in <paramref name="fields"/> change, with
    /// <c>content</c> deep-merged per RFC 7396 (omitted keys preserved, a null member deletes that key).
    /// Merged content is schema-validated before the write.
    /// </summary>
    /// <param name="path">The exact path of the node to patch.</param>
    /// <param name="fields">A JSON object holding only the fields to change.</param>
    /// <returns>A cold observable emitting <c>"Patched: {path}"</c> (with version delta) or a descriptive error string.</returns>
    public IObservable<string> Patch(string path, string fields)
    {
        logger.LogInformation("Patch called for path={Path}", path);

        if (string.IsNullOrWhiteSpace(path))
            return Observable.Return("Error: path is required.");

        return Observable.Defer(() =>
        {
            var resolvedPath = ResolvePath(path);
            if (string.IsNullOrWhiteSpace(resolvedPath))
                return Observable.Return("Error: path is required.");

            // Validate fields is a JSON object client-side. The actual merge happens
            // on the node hub via PatchDataRequest — no need to fetch the existing
            // MeshNode here, the hub applies the delta to its own workspace state.
            JsonObject? jsonObj;
            try
            {
                var sanitized = RepairJson(fields);
                jsonObj = JsonNode.Parse(sanitized) as JsonObject;
            }
            catch (JsonException ex)
            {
                return Observable.Return($"Invalid JSON: {ex.Message}");
            }

            if (jsonObj == null)
                return Observable.Return("Error: fields must be a JSON object");

            if (jsonObj.ContainsKey("name") && string.IsNullOrWhiteSpace(jsonObj["name"]?.ToString()))
                return Observable.Return(
                    $"Error: cannot patch {resolvedPath}: 'name' is empty. " +
                    "Provide a non-empty human-readable display name, or omit the 'name' key.");

            // Read-merge-write via DataChangeRequest. FetchNode returns null when the
            // path doesn't resolve (now with path-match verification so we don't
            // accidentally patch an ancestor hub).
            return FetchNode(resolvedPath).SelectMany(existing =>
            {
                if (existing == null)
                    return Observable.Return(
                        $"Error: node not found at {resolvedPath}. The path must be the node's exact 'path' property " +
                        "(never its display name). Locate it with Search (e.g. Search('name:\"…the name…\"')) and " +
                        "retry with the 'path' value from the match; to create a new node instead, use Create.");

                // Content-specific rejections carry the expected schema so agents
                // can recover on the next call without guessing.
                if (jsonObj.ContainsKey("content") && jsonObj["content"] is null)
                    return BuildNullContentError(existing.Path, existing.NodeType!);

                // 🚨 Deep-merge content (RFC 7396), never wholesale-replace it. A bare
                // `jsonObj["content"]` carries ONLY the keys the caller sent, so
                // deserialising it straight into MeshNode.Content would DROP every
                // existing content field the caller omitted — the 2026-06-13 logo patch
                // ({"content":{"logo":…}}) clobbered name/description/body exactly this
                // way. Merge the delta onto the existing content, serialised via the FULL
                // node so the polymorphic `$type` discriminator is present, so omitted
                // keys are preserved and only the provided keys change. A null member
                // deletes that key; arrays/scalars replace wholesale (RFC 7396).
                if (jsonObj["content"] is JsonObject contentPatch)
                {
                    var existingNodeJson = JsonSerializer.SerializeToNode(existing, hub.JsonSerializerOptions) as JsonObject;
                    if (existingNodeJson?["content"] is JsonObject existingContent)
                        jsonObj["content"] = MergePatch(existingContent, contentPatch);
                }

                var partial = jsonObj.Deserialize<MeshNode>(hub.JsonSerializerOptions)
                    ?? new MeshNode(existing.Id, existing.Namespace);

                var merged = existing with
                {
                    Name = jsonObj.ContainsKey("name") ? partial.Name : existing.Name,
                    Icon = jsonObj.ContainsKey("icon") ? partial.Icon : existing.Icon,
                    Category = jsonObj.ContainsKey("category") ? partial.Category : existing.Category,
                    Order = jsonObj.ContainsKey("order") ? partial.Order : existing.Order,
                    Content = jsonObj.ContainsKey("content") ? partial.Content : existing.Content,
                    PreRenderedHtml = jsonObj.ContainsKey("preRenderedHtml") ? partial.PreRenderedHtml : existing.PreRenderedHtml,
                };

                // Validate merged content against the NodeType's schema when the
                // caller touched content. Surface the schema in the error so an
                // agent can fix its payload on the retry.
                var validationObs = (jsonObj.ContainsKey("content") && !string.IsNullOrEmpty(merged.NodeType) && merged.Content != null)
                    ? ValidateContentWithSchema(merged)
                    : Observable.Return<string?>(null);

                var versionBefore = existing.Version;
                return validationObs.SelectMany(validationError =>
                    validationError != null
                        ? Observable.Return(validationError)
                        : mesh.UpdateNode(merged)
                            // 🚨 Read-your-writes barrier. UpdateNode emits OPTIMISTICALLY —
                            // the owner applies asynchronously and stamps a fresh, higher
                            // Version on apply, so the emitted `updated` still carries the
                            // pre-apply version. Without the wait, the immediately-following
                            // Get races the propagation and reads the stale value. Wait
                            // (bounded) for the live mirror to advance PAST versionBefore so
                            // the reconciled update is observable before we return.
                            .SelectMany(updated => WaitForReadYourWrites(resolvedPath, versionBefore)
                                .Select(confirmed =>
                                {
                                    var after = confirmed ?? updated;
                                    OnNodeChange?.Invoke(new NodeChangeEntry
                                    {
                                        Path = after.Path,
                                        Operation = "Updated",
                                        VersionBefore = versionBefore,
                                        VersionAfter = after.Version,
                                        NodeType = after.NodeType,
                                        NodeName = after.Name
                                    });
                                    var versionText = after.Version > versionBefore
                                        ? $" (v{versionBefore} → v{after.Version})"
                                        : "";
                                    return $"Patched: {after.Path}{versionText}";
                                }))
                            .Catch((Exception ex) =>
                                Observable.Return($"Error patching {merged.Path}: {ex.Message}")));
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Error patching node at {Path}", path);
                return Observable.Return($"Error: {ex.Message}");
            });
        });
    }

    /// <summary>
    /// Anchored text edit on a node's primary text content (Markdown body or Code source).
    /// Replaces an exact substring, so the agent supplies just the snippet to change plus
    /// enough surrounding context to make it unique — instead of re-emitting the whole
    /// document through Patch (token cost + truncation corruption on long files).
    /// Same read-your-writes semantics as Patch. Every failure mode returns a descriptive
    /// error telling the agent how to recover.
    /// </summary>
    public IObservable<string> EditContent(string path, string oldText, string newText, bool replaceAll = false)
    {
        logger.LogInformation("EditContent called for path={Path}", path);

        if (string.IsNullOrWhiteSpace(path))
            return Observable.Return("Error: path is required.");
        if (string.IsNullOrEmpty(oldText))
            return Observable.Return(
                "Error: oldText is required — Get the node and copy the exact text to replace, including whitespace.");
        if (oldText == newText)
            return Observable.Return("Error: oldText and newText are identical — nothing to change.");

        return Observable.Defer(() =>
        {
            var resolvedPath = ResolvePath(path);
            return FetchNode(resolvedPath).SelectMany(existing =>
            {
                if (existing == null)
                    return Observable.Return(
                        $"Error: node not found at {resolvedPath}. The path must be the node's exact 'path' property — " +
                        "locate it with Search and retry with the 'path' value from the match.");

                var text = existing.Content switch
                {
                    MarkdownContent md => md.Content,
                    CodeConfiguration code => code.Code,
                    string s => s,
                    _ => null
                };

                if (text == null)
                    return Observable.Return(
                        $"Error: cannot edit {resolvedPath}: its content is " +
                        $"{existing.Content?.GetType().Name ?? "empty"}, not editable text. EditContent works on " +
                        "Markdown and Code nodes; for structured content use Patch with the full 'content' object.");

                var count = CountOccurrences(text, oldText);
                if (count == 0)
                    return Observable.Return(
                        $"Error: the text to replace was not found in {resolvedPath}. Get the node and copy the " +
                        "exact text — including whitespace and line breaks — then retry. " +
                        $"(Current content is {text.Length} chars.)");
                if (count > 1 && !replaceAll)
                    return Observable.Return(
                        $"Error: the text to replace occurs {count} times in {resolvedPath}. Include more " +
                        "surrounding context to make the match unique, or set replaceAll=true to change every occurrence.");

                var newFull = text.Replace(oldText, newText, StringComparison.Ordinal);
                var merged = existing.Content switch
                {
                    MarkdownContent md => WithRerenderedMarkdown(existing, md, newFull),
                    CodeConfiguration code => existing with { Content = code with { Code = newFull } },
                    _ => existing with { Content = newFull },
                };

                var versionBefore = existing.Version;
                return mesh.UpdateNode(merged)
                    // Same read-your-writes barrier as Patch — see comment there.
                    .SelectMany(updated => WaitForReadYourWrites(resolvedPath, versionBefore)
                        .Select(confirmed =>
                        {
                            var after = confirmed ?? updated;
                            OnNodeChange?.Invoke(new NodeChangeEntry
                            {
                                Path = after.Path,
                                Operation = "Updated",
                                VersionBefore = versionBefore,
                                VersionAfter = after.Version,
                                NodeType = after.NodeType,
                                NodeName = after.Name
                            });
                            var plural = count == 1 ? "" : "s";
                            return $"Edited: {after.Path} ({count} replacement{plural})";
                        }))
                    .Catch((Exception ex) =>
                        Observable.Return($"Error editing {resolvedPath}: {ex.Message}"));
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Error editing node at {Path}", path);
                return Observable.Return($"Error: {ex.Message}");
            });
        });
    }

    /// <summary>
    /// Rebuilds the derived markdown artefacts (prerendered HTML, code submissions) after a
    /// text edit, preserving the record's other fields (authors, tags, thumbnail, abstract).
    /// Without this, the portal would keep rendering the stale pre-edit HTML.
    /// </summary>
    private static MeshNode WithRerenderedMarkdown(MeshNode node, MarkdownContent md, string newText)
    {
        var parsed = MarkdownContent.Parse(newText, node.Namespace, node.Path);
        return node with
        {
            Content = md with
            {
                Content = newText,
                PrerenderedHtml = parsed.PrerenderedHtml,
                CodeSubmissions = parsed.CodeSubmissions
            },
            PreRenderedHtml = parsed.PrerenderedHtml,
        };
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    /// <summary>
    /// Posts a <see cref="PatchDataRequest"/> to the node's hub with the raw JSON
    /// delta. The hub applies the JSON merge patch to its own <c>MeshNodeReference</c>
    /// workspace stream and returns <see cref="PatchDataResponse"/>. Emits the
    /// committed version on success; OnError on failure/timeout.
    /// </summary>
    private IObservable<long> PatchViaDataRequest(string resolvedPath, string rawPatch, int timeoutSeconds = 10) =>
        Observable.Create<long>(observer =>
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var completed = 0;

            void Fail(Exception ex)
            {
                if (Interlocked.Exchange(ref completed, 1) != 0) return;
                observer.OnError(ex);
            }

            void Emit(long version)
            {
                if (Interlocked.Exchange(ref completed, 1) != 0) return;
                observer.OnNext(version);
                observer.OnCompleted();
            }

            // 🚨 Capture inner Subscribe for proper teardown.
            IDisposable? innerSubscription = null;

            try
            {
                var delivery = hub.Post(
                    new PatchDataRequest(new MeshNodeReference(), new RawJson(rawPatch)),
                    o => o.WithTarget(new Address(resolvedPath)))!;

                innerSubscription = hub.Observe(delivery)
                    .Subscribe(
                        d =>
                        {
                            if (d.Message is PatchDataResponse resp)
                            {
                                if (resp.Success)
                                    Emit(resp.Version);
                                else
                                    Fail(new InvalidOperationException(resp.Error ?? "Patch rejected"));
                            }
                            else
                                Fail(new InvalidOperationException(
                                    $"Unexpected response {d.Message?.GetType().Name} for PatchDataRequest at {resolvedPath}"));
                        },
                        ex => Fail(new InvalidOperationException(ex.Message ?? "Delivery failed", ex)));

                cts.Token.Register(() => Fail(new TimeoutException(
                    $"PatchDataRequest for {resolvedPath} did not complete within {timeoutSeconds}s.")));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "PatchViaDataRequest: Post/RegisterCallback failed for {Path}", resolvedPath);
                Fail(ex);
            }

            return () =>
            {
                innerSubscription?.Dispose();
                cts.Dispose();
            };
        });

    /// <summary>
    /// Up-front identity validation shared by Create and Update. Returns a descriptive,
    /// actionable error string when the node is missing 'id', 'nodeType', or 'name', or when
    /// the namespace is malformed — or null when the node is sound enough to attempt the write.
    /// Validating BEFORE posting to the mesh turns a routed-to-nowhere grain call (opaque
    /// timeout, silent no-op) into an immediate, specific answer the agent can act on.
    /// </summary>
    private static string? ValidateNodeIdentity(MeshNode node, string operation)
    {
        if (string.IsNullOrWhiteSpace(node.Id))
            return $"Error: cannot {operation}: 'id' is not set. The id is the node's own slug — the final path segment, " +
                   "no slashes (e.g. \"PricingTool\"). Put the parent path in 'namespace' (e.g. \"ACME/Projects\"); " +
                   "the node's path is derived as {namespace}/{id}.";

        if (string.IsNullOrWhiteSpace(node.NodeType))
            return $"Error: cannot {operation} '{node.Path}': 'nodeType' is not set. Every node must declare a nodeType — " +
                   "it is the path of the type definition that gives the node its shape, views, and behaviour " +
                   "(e.g. \"Markdown\", \"Code\", \"Organization\"). Discover available types with " +
                   "Search('nodeType:NodeType') and retry with nodeType set." +
                   (operation == "update"
                       ? " If you only meant to change a few fields, use Patch instead — it preserves all fields you don't mention."
                       : "");

        if (string.IsNullOrWhiteSpace(node.Name))
            return $"Error: cannot {operation} '{node.Path}': 'name' is not set. Provide a non-empty, human-readable " +
                   "display name — it is shown as the node's title in the navigator and page heading.";

        var ns = node.Namespace;
        if (!string.IsNullOrEmpty(ns) && (ns.EndsWith('/') || ns.Contains("//")))
            return $"Error: cannot {operation} '{node.Id}': namespace '{ns}' is malformed. The namespace is the parent " +
                   "path, segments separated by single slashes, no trailing slash (e.g. \"ACME/Projects\", \"User/rbuergi\").";

        return null;
    }

    /// <summary>
    /// Normalizes agent-emitted namespace noise the same way <see cref="ResolvePath"/> does for
    /// path arguments: strips a leading '@' / '/' and surrounding whitespace. Models routinely
    /// copy the namespace out of an absolute reference ("@/ACME/Projects") — that intent is
    /// unambiguous, so fix it instead of failing the write.
    /// </summary>
    private static MeshNode NormalizeNamespace(MeshNode node)
    {
        var ns = node.Namespace;
        if (string.IsNullOrEmpty(ns))
            return node;

        var normalized = ResolvePath(ns).TrimStart('/');
        return normalized == ns ? node : node with { Namespace = normalized };
    }

    /// <summary>
    /// Sanitizes a MeshNode's Id: if the Id contains slashes, splits it into proper Id + Namespace.
    /// This prevents duplicate rows in the DB (the DB has a CHECK constraint blocking slashes in id).
    /// </summary>
    private MeshNode SanitizeNodeId(MeshNode node)
    {
        if (string.IsNullOrEmpty(node.Id) || !node.Id.Contains('/'))
            return node;

        var lastSlash = node.Id.LastIndexOf('/');
        var ns = node.Id[..lastSlash];
        var id = node.Id[(lastSlash + 1)..];

        if (!string.IsNullOrEmpty(node.Namespace))
            ns = $"{node.Namespace}/{ns}";

        logger.LogWarning("SanitizeNodeId: Fixed slash in id. Was id='{OldId}', now id='{NewId}' namespace='{Namespace}'",
            node.Id, id, ns);

        return node with { Id = id, Namespace = ns };
    }

    /// <summary>
    /// RFC 7396 JSON Merge Patch: recursively merges <paramref name="patch"/> onto
    /// <paramref name="target"/>. Object members merge recursively; a <c>null</c>
    /// member deletes that key; any non-object (scalar or array) replaces wholesale.
    /// Returns a fresh, detached node — neither argument is mutated, so callers can
    /// keep using <paramref name="target"/>/<paramref name="patch"/> afterwards.
    /// </summary>
    internal static JsonNode? MergePatch(JsonNode? target, JsonNode? patch)
    {
        // Non-object patch (scalar, array, or null literal) replaces the target.
        if (patch is not JsonObject patchObj)
            return patch?.DeepClone();

        var result = target is JsonObject targetObj
            ? (JsonObject)targetObj.DeepClone()
            : new JsonObject();

        foreach (var (key, value) in patchObj)
        {
            if (value is null)
                result.Remove(key);            // RFC 7396: null deletes the member
            else
                result[key] = MergePatch(result[key], value);  // recurse; result[key] is null when absent
        }

        return result;
    }

    /// <summary>
    /// Attempts to repair common JSON issues from LLM output:
    /// - Truncated strings (unclosed quotes/braces)
    /// - Unescaped control characters inside strings
    /// </summary>
    private static string RepairJson(string json)
    {
        if (string.IsNullOrEmpty(json))
            return json;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return json;
        }
        catch (JsonException) { }

        for (var i = json.Length - 1; i > 0; i--)
        {
            if (json[i] is '}' or ']')
            {
                var candidate = json[..(i + 1)];
                try
                {
                    using var doc = JsonDocument.Parse(candidate);
                    return candidate;
                }
                catch (JsonException) { }
            }
        }

        return json;
    }

    /// <summary>
    /// Writes raw bytes into a content collection on the node addressed by <paramref name="path"/>.
    /// Transport-agnostic: callers (MCP base64, REST multipart, CLI HTTP) decode at the boundary
    /// and hand off the <paramref name="bytes"/> here.
    ///
    /// <para>Path shape: <c>{nodePath}/{collection}/{filePath}</c> — e.g. <c>Systemorph/content/logo.png</c>
    /// or <c>Doc/Architecture/content/diagrams/flow.svg</c>. The collection must exist on the node
    /// and be <c>IsEditable = true</c>.</para>
    ///
    /// <para>Returns a JSON string <c>{"status":"Uploaded","path":"…","bytes":N}</c> on success, or an
    /// <c>"Error: …"</c> string on any validation/resolution failure (mirrors the other tool methods).</para>
    /// </summary>
    public IObservable<string> Upload(string path, byte[] bytes)
    {
        logger.LogInformation("Upload path={Path} bytes={Bytes}", path, bytes?.Length ?? 0);

        if (string.IsNullOrWhiteSpace(path))
            return Observable.Return("Error: path is required.");
        if (bytes is null || bytes.Length == 0)
            return Observable.Return("Error: content is required.");

        var resolvedPath = ResolvePath(path).TrimStart('/');
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return Observable.Return("Error: path is required.");

        var pathResolver = hub.ServiceProvider.GetRequiredService<IPathResolver>();
        return pathResolver.ResolvePath(resolvedPath).SelectMany(resolution =>
        {
            if (resolution == null)
                return Observable.Return($"Error: no matching node for path '{resolvedPath}'");
            if (string.IsNullOrEmpty(resolution.Remainder))
                return Observable.Return("Error: path must include '{collection}/{filePath}' after the node path (e.g. 'Systemorph/content/logo.png').");

            var remainderParts = resolution.Remainder.Split('/');
            if (remainderParts.Length < 2)
                return Observable.Return($"Error: expected '{{collection}}/{{filePath}}' in remainder '{resolution.Remainder}'.");

            var collectionName = remainderParts[0];
            var filePath = string.Join("/", remainderParts.Skip(1));
            if (string.IsNullOrEmpty(Path.GetFileName(filePath)))
                return Observable.Return($"Error: missing filename in path '{filePath}'.");

            var targetAddress = (Address)resolution.Prefix;
            var qualifiedCollectionName = $"{resolution.Prefix}/{collectionName}";

            // Ask the owning node hub for its collection config — exact same mechanism
            // the static GET endpoint uses (see BlazorHostingExtensions.ResolveStatic).
            // 🚨 .Timeout is mandatory: HandleCollectionConfigRequest is registered ONLY by
            // AddContentCollections(). A target node hub WITHOUT it never answers this
            // GetDataRequest, so an un-timed Take(1) hangs FOREVER — and since the MCP/REST
            // boundary does ops.Upload(...).FirstAsync().ToTask(), that wedges the calling
            // request (the 2026-06-14 atioz upload wedge). On timeout the TimeoutException
            // falls through to the .Catch below and surfaces as a clean "Error: …" string.
            return hub.Observe(
                new GetDataRequest(new ContentCollectionReference([collectionName])),
                o => o.WithTarget(targetAddress))
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Select(collectionResponse =>
                {
                    // Deserialize via the hub's JSON options so the naming policy (camelCase)
                    // and all fields — including IsEditable — round-trip correctly. The
                    // ContentCollectionConfig bools default to false (matching bool's
                    // type-default) so writable / visible callsites must set them
                    // explicitly; that keeps WhenWritingDefault from silently dropping
                    // meaningful state across the wire.
                    IReadOnlyCollection<ContentCollectionConfig>? configs = collectionResponse?.Message switch
                    {
                        GetDataResponse { Data: JsonElement je } =>
                            JsonSerializer.Deserialize<ContentCollectionConfig[]>(je, hub.JsonSerializerOptions),
                        GetDataResponse { Data: IReadOnlyCollection<ContentCollectionConfig> direct } => direct,
                        _ => null
                    };
                    var sourceConfig = configs?.FirstOrDefault(c => c.Name == collectionName);
                    if (sourceConfig == null) return (ContentCollectionConfig?)null;
                    return sourceConfig with { Name = qualifiedCollectionName, Address = targetAddress };
                })
                .SelectMany(collectionConfig =>
                {
                    if (collectionConfig == null)
                        return Observable.Return($"Error: collection '{collectionName}' not found on '{resolution.Prefix}'.");
                    if (!collectionConfig.IsEditable)
                        return Observable.Return($"Error: collection '{collectionName}' on '{resolution.Prefix}' is read-only.");

                    var contentService = hub.ServiceProvider.GetService<IContentService>();
                    if (contentService == null)
                        return Observable.Return("Error: content service not configured on the hub.");

                    contentService.AddConfiguration(collectionConfig);
                    var ioPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded;
                    return ioPool.Run(async ct =>
                    {
                        var collection = await contentService.GetCollectionAsync(qualifiedCollectionName, ct).ConfigureAwait(false);
                        if (collection == null)
                            return $"Error: failed to initialize collection '{qualifiedCollectionName}'.";

                        var dir = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "";
                        var fileName = Path.GetFileName(filePath);
                        using var ms = new MemoryStream(bytes);
                        await collection.SaveFileAsync(dir, fileName, ms).ConfigureAwait(false);

                        // Post-upload seam: notify registered observers (e.g. the content-indexing
                        // pipeline) AFTER the save succeeds. Fire-and-forget — each observer starts its
                        // own off-band work (an Activity), so the upload response returns immediately
                        // and indexing never runs inline on this pooled continuation. No-op when no
                        // observer is registered; ContentCollections itself takes no indexing/AI/pg dep.
                        hub.RaiseContentUploaded(qualifiedCollectionName, filePath);

                        return JsonSerializer.Serialize(new
                        {
                            status = "Uploaded",
                            path = $"{resolution.Prefix}/{collectionName}/{filePath}",
                            bytes = bytes.Length,
                        }, hub.JsonSerializerOptions);
                    });
                });
        })
        .Catch((Exception ex) =>
        {
            logger.LogWarning(ex, "Upload failed for {Path}", path);
            var message = ex is TimeoutException
                ? $"the node for '{path}' did not respond to the content-collection lookup in time — " +
                  "confirm the path resolves to a content-enabled node (one configured with AddContentCollections)."
                : ex.Message;
            return Observable.Return($"Error: {message}");
        });
    }

    /// <summary>
    /// Lists a content collection directory — the read half of the file browser. Path shape is
    /// <c>{node}/{collection}[/{dir}]</c> (e.g. <c>Systemorph/content</c> or
    /// <c>Systemorph/content/images</c>). Mirrors <see cref="Upload"/>'s resolve → collection-config →
    /// IoPool flow, then enumerates <c>GetCollectionItems</c>. Returns JSON
    /// <c>{ collection, path, editable, items: [{ kind, name, path, itemCount?, lastModified? }] }</c>
    /// or a descriptive <c>Error: …</c> string.
    /// </summary>
    public IObservable<string> ContentList(string path)
    {
        logger.LogInformation("ContentList path={Path}", path);
        if (string.IsNullOrWhiteSpace(path))
            return Observable.Return("Error: path is required.");
        var resolvedPath = ResolvePath(path).TrimStart('/');
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return Observable.Return("Error: path is required.");

        var pathResolver = hub.ServiceProvider.GetRequiredService<IPathResolver>();
        return pathResolver.ResolvePath(resolvedPath).SelectMany(resolution =>
        {
            if (resolution == null)
                return Observable.Return($"Error: no matching node for path '{resolvedPath}'");
            if (string.IsNullOrEmpty(resolution.Remainder))
                return Observable.Return("Error: path must include '{collection}[/{dir}]' after the node path (e.g. 'Systemorph/content').");

            var remainderParts = resolution.Remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var collectionName = remainderParts[0];
            var dirParts = remainderParts.Skip(1).ToArray();
            // The FileSystem provider resolves the listing dir with an unguarded Path.Combine(basePath, dir),
            // so a '..' (or a backslash-embedded) segment would traverse OUTSIDE the collection scope. Reject
            // traversal at the boundary — the listing contract is strictly "within this collection".
            if (dirParts.Any(p => p is "." or ".." || p.Contains('\\') || p.Contains("..")))
                return Observable.Return("Error: the directory path must not contain '.', '..', or backslash segments.");
            var dir = string.Join("/", dirParts);
            var targetAddress = (Address)resolution.Prefix;
            var qualifiedCollectionName = $"{resolution.Prefix}/{collectionName}";

            // Same collection-config lookup the static GET endpoint + Upload use — .Timeout is
            // mandatory (a node hub without AddContentCollections never answers this).
            return hub.Observe(
                new GetDataRequest(new ContentCollectionReference([collectionName])),
                o => o.WithTarget(targetAddress))
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Select(collectionResponse =>
                {
                    IReadOnlyCollection<ContentCollectionConfig>? configs = collectionResponse?.Message switch
                    {
                        GetDataResponse { Data: JsonElement je } =>
                            JsonSerializer.Deserialize<ContentCollectionConfig[]>(je, hub.JsonSerializerOptions),
                        GetDataResponse { Data: IReadOnlyCollection<ContentCollectionConfig> direct } => direct,
                        _ => null
                    };
                    var sourceConfig = configs?.FirstOrDefault(c => c.Name == collectionName);
                    if (sourceConfig == null) return (ContentCollectionConfig?)null;
                    return sourceConfig with { Name = qualifiedCollectionName, Address = targetAddress };
                })
                .SelectMany(collectionConfig =>
                {
                    if (collectionConfig == null)
                        return Observable.Return($"Error: collection '{collectionName}' not found on '{resolution.Prefix}'.");

                    var contentService = hub.ServiceProvider.GetService<IContentService>();
                    if (contentService == null)
                        return Observable.Return("Error: content service not configured on the hub.");

                    contentService.AddConfiguration(collectionConfig);
                    var ioPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded;
                    return ioPool.Run(async ct =>
                    {
                        var collection = await contentService.GetCollectionAsync(qualifiedCollectionName, ct).ConfigureAwait(false);
                        if (collection == null)
                            return $"Error: failed to initialize collection '{qualifiedCollectionName}'.";

                        var items = new List<object>();
                        await foreach (var item in collection.GetCollectionItems(dir, ct).ConfigureAwait(false))
                            items.Add(item switch
                            {
                                FolderItem f => (object)new { kind = "folder", name = f.Name, path = f.Path, itemCount = f.ItemCount },
                                FileItem fi => new { kind = "file", name = fi.Name, path = fi.Path, lastModified = fi.LastModified },
                                _ => new { kind = "unknown", name = item.Name, path = item.Path },
                            });

                        return JsonSerializer.Serialize(new
                        {
                            collection = qualifiedCollectionName,
                            path = dir,
                            editable = collectionConfig.IsEditable,
                            items,
                        }, hub.JsonSerializerOptions);
                    });
                });
        })
        .Catch((Exception ex) =>
        {
            logger.LogWarning(ex, "ContentList failed for {Path}", path);
            var message = ex is TimeoutException
                ? "the node did not respond to the content-collection lookup in time — confirm the path resolves to a content-enabled node."
                : ex.Message;
            return Observable.Return($"Error: {message}");
        });
    }

    /// <summary>
    /// Deletes one or more nodes (and their descendants) given a JSON array of path strings.
    /// </summary>
    /// <param name="paths">A JSON array of node paths to delete.</param>
    /// <returns>A cold observable emitting the per-path deletion result or a descriptive error string.</returns>
    public IObservable<string> Delete(string paths)
    {
        logger.LogInformation("Delete called");

        return Observable.Defer(() =>
        {
            List<string>? pathList;
            try
            {
                pathList = JsonSerializer.Deserialize<List<string>>(paths, hub.JsonSerializerOptions);
            }
            catch (JsonException ex)
            {
                return Observable.Return($"Invalid JSON: {ex.Message}");
            }

            if (pathList == null || pathList.Count == 0)
                return Observable.Return("No paths provided.");

            var perPath = ImmutableList<IObservable<string>>.Empty;
            foreach (var rawPath in pathList)
            {
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    perPath = perPath.Add(Observable.Return("Error deleting: empty path"));
                    continue;
                }

                string resolvedPath;
                try
                {
                    resolvedPath = ResolvePath(rawPath);
                }
                catch (Exception ex)
                {
                    perPath = perPath.Add(Observable.Return($"Error deleting '{rawPath}': {ex.Message}"));
                    continue;
                }

                var capturedPath = resolvedPath;
                perPath = perPath.Add(
                    mesh.DeleteNode(capturedPath)
                        .Select(_ => $"Deleted: {capturedPath}")
                        .Catch((Exception ex) =>
                        {
                            logger.LogWarning(ex, "Error deleting {Path}", capturedPath);
                            return Observable.Return($"Error deleting {capturedPath}: {ex.Message}");
                        }));
            }

            return perPath
                .ToObservable()
                .Concat()
                .ToList()
                .Select(lines => string.Join("\n", lines));
        });
    }

    /// <summary>
    /// Builds the standard "content is null" rejection message for Update/Patch,
    /// embedding the JSON schema for the node's content type when available so the
    /// agent can fill content correctly on the next call. Reactive: schema lookup
    /// may need a workspace round-trip for dynamic NodeTypes.
    /// </summary>
    internal IObservable<string> BuildNullContentError(string path, string nodeType)
    {
        var msg = $"Error: cannot write {path}: 'content' is null. " +
                  "Fetch the node first with Get, modify the returned content in-place, " +
                  "and resend the complete node. Never send null content.";
        return GetContentSchema(nodeType)
            .Select(schema => schema != null
                ? msg + $" Expected content schema for NodeType '{nodeType}': {schema}"
                : msg);
    }

    /// <summary>
    /// Runs schema validation for <paramref name="meshNode"/> and, when invalid,
    /// appends the expected JSON schema to the error so the agent can recover.
    /// Emits null when content is valid (or when no schema is available).
    /// </summary>
    internal IObservable<string?> ValidateContentWithSchema(MeshNode meshNode)
    {
        return ValidateContentAgainstSchema(meshNode)
            .SelectMany(validationError =>
            {
                if (validationError == null)
                    return Observable.Return<string?>(null);
                if (string.IsNullOrEmpty(meshNode.NodeType))
                    return Observable.Return<string?>(validationError);
                return GetContentSchema(meshNode.NodeType!)
                    .Select(schema => schema != null
                        ? validationError + $" Expected content schema for NodeType '{meshNode.NodeType}': {schema}"
                        : validationError);
            });
    }

    /// <summary>
    /// Resolves the HubConfiguration delegate for <paramref name="nodeType"/>:
    /// fast path — static NodeType registered via <c>AddMeshNodes</c> in
    /// <c>meshConfiguration.Nodes</c>; slow path — read the NodeType MeshNode
    /// via <c>workspace.GetMeshNodeStream</c> and recover the delegate from the
    /// already-cached DLL via
    /// <see cref="IMeshNodeCompilationService.GetConfigurationsFromExistingAssembly"/>.
    /// Single emission; emits null when neither path can produce a delegate.
    /// </summary>
    private IObservable<Func<MessageHubConfiguration, MessageHubConfiguration>?>
        ResolveHubConfigForSchema(string nodeType)
    {
        var staticNode = hub.ServiceProvider.FindStaticNode(nodeType);
        if (staticNode is { HubConfiguration: { } cfg })
        {
            return Observable.Return<Func<MessageHubConfiguration, MessageHubConfiguration>?>(cfg);
        }

        var compilationService = hub.ServiceProvider.GetService<IMeshNodeCompilationService>();
        if (compilationService == null)
            return Observable.Return<Func<MessageHubConfiguration, MessageHubConfiguration>?>(null);

        return hub.GetWorkspace().GetMeshNodeStream(nodeType)
            .Where(n => n?.Content is NodeTypeDefinition def
                        && def.CompilationStatus == CompilationStatus.Ok
                        && !string.IsNullOrEmpty(def.LatestAssemblyCollection)
                        && !string.IsNullOrEmpty(def.LatestAssemblyPath))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(5))
            .SelectMany(node =>
            {
                var def = (NodeTypeDefinition)node!.Content!;
                var version = def.LastCompiledVersion ?? node.Version;
                var store = string.Equals(def.LatestAssemblyCollection, FrameworkAssemblyStore.CollectionName, StringComparison.Ordinal)
                    ? (IAssemblyStore)FrameworkAssemblyStore.Instance
                    : hub.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;
                return store.TryGetAssemblyPath(node.Path, version)
                    .SelectMany(localPath => string.IsNullOrEmpty(localPath)
                        ? Observable.Return<NodeCompilationResult?>(null)
                        : compilationService.GetConfigurationsFromExistingAssembly(localPath!, nodeType).Take(1));
            })
            .Select(result =>
            {
                var matching = result?.NodeTypeConfigurations
                    .FirstOrDefault(c => string.Equals(c.NodeType, nodeType, StringComparison.OrdinalIgnoreCase))
                    ?? result?.NodeTypeConfigurations.FirstOrDefault();
                return matching?.HubConfiguration;
            })
            .Catch<Func<MessageHubConfiguration, MessageHubConfiguration>?, Exception>(_ =>
                Observable.Return<Func<MessageHubConfiguration, MessageHubConfiguration>?>(null));
    }

    /// <summary>
    /// Returns the JSON schema string for the content type registered against
    /// <paramref name="nodeType"/>, or null if no schema can be derived.
    /// </summary>
    internal IObservable<string?> GetContentSchema(string nodeType)
    {
        return ResolveHubConfigForSchema(nodeType)
            .Select(hubConfig =>
            {
                if (hubConfig == null) return null;
                try
                {
                    var tempAddress = new Address($"_schema_lookup/{Guid.NewGuid():N}");
                    var tempHub = hub.GetHostedHub(tempAddress, hubConfig);
                    if (tempHub == null) return null;
                    try
                    {
                        var typeRegistry = tempHub.ServiceProvider.GetService<ITypeRegistry>();
                        if (typeRegistry == null || !typeRegistry.TryGetType(nodeType, out var typeDefinition))
                            return null;
                        // Generate the schema with tempHub's options, NOT the parent hub's.
                        // The compiled content type (and its nested types) is registered in
                        // tempHub's TypeRegistry under its clean short name. The parent hub's
                        // PolymorphicTypeInfoResolver is bound to the parent's registry, which
                        // does not own the type — so GetOrAddType would fall back to
                        // TypeRegistry.FormatType and leak the fully-qualified, capitalized CLR
                        // FullName into every $type reference. Use the type-owning hub's options
                        // so the schema references resolve to the registered name.
                        var schemaNode = tempHub.JsonSerializerOptions.GetJsonSchemaAsNode(typeDefinition!.Type);
                        return (string?)schemaNode.ToJsonString();
                    }
                    finally
                    {
                        tempHub.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Schema retrieval skipped for NodeType {NodeType}", nodeType);
                    return null;
                }
            });
    }

    /// <summary>
    /// Validates node content against the content type for its NodeType.
    /// Creates a temporary hub with the NodeType's configuration to find the
    /// registered content type, then attempts to deserialize the content into that type.
    /// Emits an error message if invalid, or null if valid/no schema available.
    /// </summary>
    internal IObservable<string?> ValidateContentAgainstSchema(MeshNode meshNode)
    {
        if (string.IsNullOrEmpty(meshNode.NodeType))
            return Observable.Return<string?>(null);

        return ResolveHubConfigForSchema(meshNode.NodeType!)
            .Select(hubConfig =>
            {
                if (hubConfig == null) return null;
                try
                {
                    var tempAddress = new Address($"_schema_validation/{Guid.NewGuid():N}");
                    var tempHub = hub.GetHostedHub(tempAddress, hubConfig);
                    if (tempHub == null) return null;
                    try
                    {
                        var typeRegistry = tempHub.ServiceProvider.GetService<ITypeRegistry>();
                        if (typeRegistry == null || !typeRegistry.TryGetType(meshNode.NodeType!, out var typeDefinition))
                            return null;

                        var contentType = typeDefinition!.Type;
                        var contentJson = JsonSerializer.Serialize(meshNode.Content, hub.JsonSerializerOptions);
                        try
                        {
                            var deserialized = JsonSerializer.Deserialize(contentJson, contentType, hub.JsonSerializerOptions);
                            return (string?)(deserialized == null
                                ? $"Error: Content is null after deserialization for NodeType '{meshNode.NodeType}'."
                                : null);
                        }
                        catch (JsonException ex)
                        {
                            return (string?)$"Error: Content does not match the schema for NodeType '{meshNode.NodeType}'. {ex.Message}";
                        }
                    }
                    finally
                    {
                        tempHub.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Schema validation skipped for NodeType {NodeType}", meshNode.NodeType);
                    return null;
                }
            });
    }

    /// <summary>
    /// Moves a node and its descendants to a new path. Posts <see cref="MoveNodeRequest"/>
    /// and subscribes via <c>RegisterCallback</c> — no <c>AwaitResponse</c>, no <c>await</c>
    /// on the hub scheduler.
    /// </summary>
    public IObservable<string> Move(string sourcePath, string targetPath)
    {
        logger.LogInformation("Move called: {Source} -> {Target}", sourcePath, targetPath);

        if (string.IsNullOrWhiteSpace(sourcePath))
            return Observable.Return("Error: sourcePath is required.");
        if (string.IsNullOrWhiteSpace(targetPath))
            return Observable.Return("Error: targetPath is required.");

        var resolvedSource = ResolvePath(sourcePath);
        var resolvedTarget = ResolvePath(targetPath);

        if (resolvedSource == resolvedTarget)
            return Observable.Return($"Error: target path is the same as source ({resolvedSource}).");

        return Observable.Create<string>(observer =>
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // 🚨 Capture inner Subscribe for proper teardown.
            IDisposable? innerSubscription = null;
            try
            {
                var delivery = hub.Post(
                    new MoveNodeRequest(resolvedSource, resolvedTarget),
                    o => o.WithTarget(new Address(resolvedSource)))!;

                innerSubscription = hub.Observe(delivery)
                    .Subscribe(
                        d =>
                        {
                            try
                            {
                                if (d.Message is MoveNodeResponse msg)
                                {
                                    if (msg.Success)
                                        observer.OnNext($"Moved: {resolvedSource} -> {resolvedTarget}");
                                    else
                                        observer.OnNext(
                                            $"Error moving {resolvedSource} -> {resolvedTarget}: {msg.Error ?? "unknown error"}"
                                            + (msg.RejectionReason is { } r ? $" ({r})" : ""));
                                }
                                else
                                {
                                    observer.OnNext(
                                        $"Error moving {resolvedSource} -> {resolvedTarget}: unexpected response {d.Message?.GetType().Name}");
                                }
                                observer.OnCompleted();
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Error moving {Source} -> {Target}", resolvedSource, resolvedTarget);
                                observer.OnNext($"Error: {ex.Message}");
                                observer.OnCompleted();
                            }
                        },
                        ex =>
                        {
                            observer.OnNext(
                                $"Error moving {resolvedSource} -> {resolvedTarget}: {ex.Message ?? "delivery failed"}");
                            observer.OnCompleted();
                        });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error moving {Source} -> {Target}", resolvedSource, resolvedTarget);
                observer.OnNext($"Error: {ex.Message}");
                observer.OnCompleted();
            }

            return () =>
            {
                innerSubscription?.Dispose();
                cts.Dispose();
            };
        });
    }

    /// <summary>
    /// Copies a node and all its descendants to a target namespace. Delegates to
    /// <see cref="NodeCopyHelper.CopyNodeTree"/> — fully reactive pipeline (Query +
    /// MeshNodeReference streams + CreateNode observables chained sequentially).
    /// </summary>
    public IObservable<string> Copy(string sourcePath, string targetNamespace, bool force = false)
    {
        logger.LogInformation("Copy called: {Source} -> {Target}, force={Force}", sourcePath, targetNamespace, force);

        if (string.IsNullOrWhiteSpace(sourcePath))
            return Observable.Return("Error: sourcePath is required.");
        if (string.IsNullOrWhiteSpace(targetNamespace))
            return Observable.Return("Error: targetNamespace is required.");

        var resolvedSource = ResolvePath(sourcePath);
        var resolvedTarget = ResolvePath(targetNamespace);

        return NodeCopyHelper.CopyNodeTree(mesh, mesh, hub, resolvedSource, resolvedTarget, force, logger)
            .Select(copied => $"Copied {copied} node(s): {resolvedSource} -> {resolvedTarget}")
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Error copying {Source} -> {Target}", resolvedSource, resolvedTarget);
                return Observable.Return($"Error: {ex.Message}");
            });
    }

    /// <summary>
    /// Recycles the hub at <paramref name="path"/> by posting a
    /// <see cref="DisposeRequest"/>. The next access re-initialises the hub — which
    /// means a fresh NodeType compile and fresh data loads. Useful after fixing a
    /// broken NodeType or when something is stuck in an inconsistent cached state.
    /// Returns a JSON <c>{status, path}</c> envelope. The caller should wait ~100ms
    /// before re-accessing so the grain teardown completes.
    /// </summary>
    public IObservable<string> Recycle(string path)
    {
        logger.LogInformation("Recycle called with path={Path}", path);

        if (string.IsNullOrWhiteSpace(path))
            return Observable.Return(JsonSerializer.Serialize(
                new { status = "Error", message = "path is required" },
                hub.JsonSerializerOptions));

        var resolvedPath = ResolvePath(path);
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return Observable.Return(JsonSerializer.Serialize(
                new { status = "Error", message = "path is required" },
                hub.JsonSerializerOptions));

        // Permission gate: recycling disposes the node's hub (DisposeRequest) and forces
        // re-initialization — operationally a write on that node. Require Update on the
        // target so a read-only caller can't bounce other partitions' hubs. With no RLS
        // wired, the default evaluator grants All and behavior is unchanged. Fail closed
        // on evaluator errors/timeouts.
        return hub.CheckPermission(resolvedPath, MeshWeaver.Mesh.Security.Permission.Update)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Recycle: permission check failed for {Path}", resolvedPath);
                return Observable.Return(false);
            })
            .SelectMany(allowed => allowed
                ? RecycleCore(resolvedPath)
                : Observable.Return(JsonSerializer.Serialize(
                    new
                    {
                        status = "Error",
                        path = resolvedPath,
                        message = "Recycle requires Update permission on the target node — it disposes the node's hub and forces re-initialization. Ask someone with write access to the node (or a platform admin) to do it."
                    },
                    hub.JsonSerializerOptions)));
    }

    private IObservable<string> RecycleCore(string resolvedPath)
    {
        try
        {
            // Trigger a fresh compile by flipping CompilationStatus = Pending on
            // the NodeType MeshNode. The per-NodeType hub's CompileWatcher (see
            // NodeTypeCompilationHelpers.InstallCompileWatcher) picks up the
            // Pending flip and runs Roslyn — the MeshNode IS the cache, so we
            // don't need a side cache to invalidate.
            hub.GetWorkspace().GetMeshNodeStream(resolvedPath).Update(curr =>
                    curr.Content is Graph.Configuration.NodeTypeDefinition def
                        ? curr with { Content = def with { CompilationStatus = CompilationStatus.Pending } }
                        : curr)
                .Subscribe(
                    _ => { },
                    ex => logger.LogWarning(ex,
                        "Recycle: failed to flip CompilationStatus=Pending for {Path}", resolvedPath));

            var changeFeed = hub.ServiceProvider.GetService<IMeshChangeFeed>();
            if (changeFeed != null)
            {
                var segments = resolvedPath.Split('/');
                var id = segments.Length > 0 ? segments[^1] : resolvedPath;
                var ns = segments.Length > 1 ? string.Join("/", segments[..^1]) : "";
                changeFeed.Publish(new MeshChangeEvent(
                    Namespace: ns,
                    Id: id,
                    Path: resolvedPath,
                    Kind: MeshChangeKind.Updated,
                    NodeType: MeshNode.NodeTypePath,
                    Version: 0,
                    Timestamp: DateTimeOffset.UtcNow));
            }

            hub.Post(new DisposeRequest(), o => o.WithTarget(new Address(resolvedPath)));
            return Observable.Return(JsonSerializer.Serialize(
                new
                {
                    status = "Recycled",
                    path = resolvedPath,
                    message = "DisposeRequest posted + cache invalidation broadcast via MeshChangeFeed. Wait ~100ms before the next access."
                },
                hub.JsonSerializerOptions));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error recycling {Path}", resolvedPath);
            return Observable.Return(JsonSerializer.Serialize(
                new { status = "Error", path = resolvedPath, message = ex.Message },
                hub.JsonSerializerOptions));
        }
    }

    /// <summary>
    /// Returns compilation diagnostics for a NodeType or an instance of one.
    /// </summary>
    public IObservable<string> GetDiagnostics(string path)
    {
        logger.LogInformation("GetDiagnostics called with path={Path}", path);

        if (string.IsNullOrWhiteSpace(path))
            return Observable.Return(JsonSerializer.Serialize(
                new { status = "Error", message = "path is required" },
                hub.JsonSerializerOptions));

        var resolvedPath = ResolvePath(path);
        var meshConfig = hub.ServiceProvider.GetService<MeshConfiguration>();

        // Diagnostics are read directly off the NodeType MeshNode — the
        // owner-driven status/error/timestamps live on NodeTypeDefinition,
        // populated by the per-NodeType hub's CompileWatcher.
        return FetchNode(resolvedPath).SelectMany(node =>
            {
                // Match LookupCompilationError: node.Content arrives as JsonElement
                // when the per-node hub doesn't have NodeTypeDefinition in its
                // TypeRegistry, so check NodeType==MeshNode.NodeTypePath as fallback.
                var isNodeTypeDef = node?.Content is Graph.Configuration.NodeTypeDefinition
                    || (node is not null && string.Equals(node.NodeType, MeshNode.NodeTypePath, StringComparison.Ordinal));
                var nodeTypePath = isNodeTypeDef ? node!.Path : node?.NodeType;

                if (string.IsNullOrEmpty(nodeTypePath))
                    return Observable.Return(JsonSerializer.Serialize(
                        new { status = "Unknown", message = $"Not found: {resolvedPath}" },
                        hub.JsonSerializerOptions));

                // Fast path: the input node already IS the NodeType MeshNode
                // AND its compile has settled (Ok/Error). Compiling/Pending/Unknown
                // states fall through to the stream so we wait for the
                // CompileWatcher's write-back instead of returning a stale snapshot.
                if (isNodeTypeDef
                    && node!.Content is Graph.Configuration.NodeTypeDefinition ownDef
                    && IsSettled(ownDef))
                    return Observable.Return(FormatDiagnosticsFromDef(ownDef, nodeTypePath));

                // Static fast path: NodeType registered via AddMeshNodes — there is
                // no per-NodeType hub or persisted MeshNode, so the runtime status
                // is implicit Ok (its HubConfiguration is bundled with the framework).
                if (hub.ServiceProvider.FindStaticNode(nodeTypePath) is not null)
                    return Observable.Return(FormatDiagnostics(
                        CompilationStatus.Ok, nodeTypePath,
                        error: null, startedAt: null, lastCompiledAt: null,
                        hub.JsonSerializerOptions));

                // Slow path: subscribe to the NodeType's live stream and wait for
                // the CompileWatcher to settle. Where(settled).Take(1) keeps the
                // read in lockstep with the writer; without it we'd race against
                // the Compiling → Ok/Error write-back and return stale state.
                return hub.GetWorkspace().GetMeshNodeStream(nodeTypePath)
                    // JsonElement-tolerant settle check: a degraded NodeType node (Content
                    // stayed a JsonElement because the per-node hub's TypeRegistry lacked the
                    // NodeTypeDefinition $type) still satisfies the wait, instead of hanging to
                    // the 5s timeout and then reporting "Unknown".
                    .Where(n => IsSettled(ReadCompilationStatusFromNode(n)))
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(5))
                    .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
                    .Select(typeNode =>
                    {
                        var def = typeNode.ContentAs<Graph.Configuration.NodeTypeDefinition>(hub.JsonSerializerOptions);
                        if (def is not null)
                            return FormatDiagnosticsFromDef(def, nodeTypePath);
                        // Degraded JsonElement content: format from the tolerant readers so a
                        // settled-but-degraded node reports its real status/error rather than
                        // "has no definition".
                        var status = ReadCompilationStatusFromNode(typeNode);
                        if (status is not null)
                            return FormatDiagnostics(status.Value, nodeTypePath,
                                ReadCompilationError(typeNode), startedAt: null, lastCompiledAt: null,
                                hub.JsonSerializerOptions);
                        return JsonSerializer.Serialize(
                            new { status = "Unknown", message = $"NodeType '{nodeTypePath}' has no definition" },
                            hub.JsonSerializerOptions);
                    });
            });
    }

    /// <summary>
    /// True when the NodeType's <see cref="NodeTypeDefinition.CompilationStatus"/>
    /// has reached a terminal state (<see cref="CompilationStatus.Ok"/> or
    /// <see cref="CompilationStatus.Error"/>). Pending and Compiling are
    /// transient — readers should keep waiting for the watcher's settle
    /// write rather than report a half-baked state.
    /// </summary>
    private static bool IsSettled(Graph.Configuration.NodeTypeDefinition def)
        => IsSettled(def.CompilationStatus);

    /// <summary>
    /// Settled check over a raw <see cref="CompilationStatus"/> read via the
    /// JsonElement-tolerant <see cref="ReadCompilationStatusFromNode"/> — so a
    /// settled-but-DEGRADED node (Content stayed a JsonElement because the per-node
    /// hub's TypeRegistry lacked the NodeTypeDefinition <c>$type</c>) still satisfies
    /// the compile-settle wait instead of hanging until the 5s timeout.
    /// </summary>
    private static bool IsSettled(CompilationStatus? status)
        => status == CompilationStatus.Ok || status == CompilationStatus.Error;

    private string FormatDiagnosticsFromDef(
        Graph.Configuration.NodeTypeDefinition def, string nodeTypePath)
    {
        var status = def.CompilationStatus ?? CompilationStatus.Unknown;
        return FormatDiagnostics(
            status,
            nodeTypePath,
            error: status == CompilationStatus.Error ? def.CompilationError : null,
            startedAt: status == CompilationStatus.Compiling ? def.LastCompileStartedAt : null,
            lastCompiledAt: status == CompilationStatus.Ok ? def.LastCompileSucceededAt : null,
            hub.JsonSerializerOptions);
    }

    /// <summary>
    /// Triggers a compile for a NodeType by flipping its
    /// <see cref="NodeTypeDefinition.CompilationStatus"/> to
    /// <see cref="CompilationStatus.Pending"/> via the canonical remote-stream
    /// write path: <see cref="MeshNodeStreamExtensions.UpdateMeshNode"/> opens a
    /// <c>GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c> against the
    /// owning per-node hub and pushes a <see cref="ChangeItem{T}"/> patch through
    /// the synchronization protocol. The CompileWatcher (installed by
    /// <c>AddMeshDataSource</c>) observes the Pending state on its own MeshNode
    /// stream and runs Roslyn, then writes back <see cref="CompilationStatus.Ok"/>
    /// or <see cref="CompilationStatus.Error"/> plus
    /// <see cref="NodeTypeDefinition.LastCompilationActivityPath"/>.
    ///
    /// <para>Why a dedicated tool over <see cref="Patch"/>: Patch requires both
    /// Read (to merge the existing node) and Update permission on the target node.
    /// <c>Compile</c> only needs the per-node hub to accept the synchronisation
    /// patch — caller drives the same hub that the CompileWatcher listens on, so
    /// state transitions never bottleneck on a routing service.</para>
    ///
    /// <para>Observe progress: poll <c>get @nodeTypePath</c> for
    /// <c>compilationStatus</c> transitions, then once it settles to Ok/Error
    /// follow <c>lastCompilationActivityPath</c> to fetch the full executed-source-
    /// queries / matched-Code-paths / Roslyn-output trace.</para>
    /// </summary>
    public IObservable<string> Compile(string path)
    {
        logger.LogInformation("Compile called with path={Path}", path);

        if (string.IsNullOrWhiteSpace(path))
            return Observable.Return(JsonSerializer.Serialize(
                new { status = "Error", message = "path is required" },
                hub.JsonSerializerOptions));

        var resolvedPath = ResolvePath(path);
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return Observable.Return(JsonSerializer.Serialize(
                new { status = "Error", message = "path is required" },
                hub.JsonSerializerOptions));

        return Observable.Defer(() =>
        {
            IWorkspace workspace;
            try { workspace = hub.GetWorkspace(); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Compile: workspace unavailable for {Path}", resolvedPath);
                return Observable.Return(JsonSerializer.Serialize(
                    new { status = "Error", path = resolvedPath, message = ex.Message },
                    hub.JsonSerializerOptions));
            }

            // Subscribe to the NodeType's stream BEFORE flipping Pending so we
            // don't miss the watcher's status transitions. The stream emits the
            // current node first (whatever status it's in); we wait for a
            // settled Ok/Error after the trigger.
            var stream = workspace.GetMeshNodeStream(resolvedPath);

            try
            {
                // Impersonate System for the Pending flip. Triggering a recompile is an
                // INFRASTRUCTURE operation that fills the assembly cache — it must succeed
                // even when the caller has no Update right on the target partition (the
                // read-only Doc partition is the canonical case). Under the caller's identity
                // this flip was denied → "UpdateMeshNode failed" → the compile never ran and
                // the cache stayed empty (atioz on-demand-compile failure on Doc). The
                // RunCompile watcher + Release-node creation already run as System; this entry
                // flip was the straggler still on the caller's identity.
                var accessService = hub.ServiceProvider.GetService<AccessService>();
                using (accessService?.ImpersonateAsSystem())
                    workspace.GetMeshNodeStream(resolvedPath).Update(node => node with
                    {
                        Content = WithPendingCompilationStatus(node.Content)
                    }).Subscribe(
                        _ => { },
                        ex => logger.LogWarning(ex,
                            "Compile: UpdateMeshNode failed for {Path}", resolvedPath));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Compile trigger failed for {Path}", resolvedPath);
                return Observable.Return(JsonSerializer.Serialize(
                    new { status = "Error", path = resolvedPath, message = ex.Message },
                    hub.JsonSerializerOptions));
            }

            // Wait for the watcher to write back Ok or Error (60s budget — Roslyn
            // first compile of a moderate node is 5-15s; bigger trees can take
            // longer; some hubs may take ~5s to emit a settled state after the
            // initial Pending). Then return a structured result with the error
            // body inline if Error — agents/humans get the diagnostic without a
            // second polling round-trip.
            return stream
                .Where(n =>
                {
                    var status = ReadCompilationStatusFromNode(n);
                    return status == CompilationStatus.Ok || status == CompilationStatus.Error;
                })
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(60))
                .Select(n =>
                {
                    var status = ReadCompilationStatusFromNode(n);
                    var error = ReadCompilationError(n);
                    var activityPath = ReadActivityPath(n);
                    return JsonSerializer.Serialize(
                        new
                        {
                            status = status?.ToString() ?? "Unknown",
                            path = resolvedPath,
                            error,
                            activityPath,
                            message = status == CompilationStatus.Ok
                                ? "Compile SUCCEEDED."
                                : "Compile FAILED — see `error` for Roslyn diagnostics. "
                                  + "Full source-discovery + matched-Code-paths trace lives at "
                                  + (activityPath ?? "(no activity log written)") + "."
                        },
                        hub.JsonSerializerOptions);
                })
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex,
                        "Compile: timeout / observer error waiting for {Path} to settle", resolvedPath);
                    return Observable.Return(JsonSerializer.Serialize(
                        new
                        {
                            status = "Pending",
                            path = resolvedPath,
                            message = "Compile triggered but did not settle within the deadline. "
                                + "Poll `get " + resolvedPath + "` for `compilationStatus` and "
                                + "`lastCompilationActivityPath`. Underlying error: " + ex.Message
                        },
                        hub.JsonSerializerOptions));
                });
        });
    }

    private static CompilationStatus? ReadCompilationStatusFromNode(MeshNode? node)
    {
        if (node?.Content is Graph.Configuration.NodeTypeDefinition def)
            return def.CompilationStatus;
        if (node?.Content is JsonElement json && json.TryGetProperty("compilationStatus", out var p))
        {
            if (p.ValueKind == JsonValueKind.String && Enum.TryParse<CompilationStatus>(p.GetString(), true, out var parsed))
                return parsed;
        }
        return null;
    }

    private static string? ReadCompilationError(MeshNode? node)
    {
        if (node?.Content is Graph.Configuration.NodeTypeDefinition def)
            return def.CompilationError;
        if (node?.Content is JsonElement json && json.TryGetProperty("compilationError", out var p))
            return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        return null;
    }

    private static string? ReadActivityPath(MeshNode? node)
    {
        if (node?.Content is Graph.Configuration.NodeTypeDefinition def)
            return def.LastCompilationActivityPath;
        if (node?.Content is JsonElement json && json.TryGetProperty("lastCompilationActivityPath", out var p))
            return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        return null;
    }

    /// <summary>
    /// Returns a new <c>Content</c> object with <c>compilationStatus</c> set to
    /// <c>Pending</c>. Handles both strongly-typed
    /// <see cref="Graph.Configuration.NodeTypeDefinition"/> (own hub registered
    /// the type) and <see cref="JsonElement"/> (remote hub passed it through
    /// untyped). Other content shapes are returned unchanged with a warning —
    /// this method is only meaningful on a NodeType node.
    /// </summary>
    private object? WithPendingCompilationStatus(object? content)
    {
        switch (content)
        {
            case Graph.Configuration.NodeTypeDefinition def:
                return def with { CompilationStatus = CompilationStatus.Pending };

            case JsonElement json:
            {
                var node = JsonNode.Parse(json.GetRawText()) as JsonObject ?? new JsonObject();
                node["compilationStatus"] = "Pending";
                return JsonSerializer.SerializeToElement(node, hub.JsonSerializerOptions);
            }

            case null:
            {
                var node = new JsonObject { ["compilationStatus"] = "Pending" };
                return JsonSerializer.SerializeToElement(node, hub.JsonSerializerOptions);
            }

            default:
                logger.LogWarning(
                    "Compile: unexpected content type {Type} on NodeType node — wrapping",
                    content.GetType().Name);
                return content;
        }
    }

    /// <summary>
    /// Pure JSON formatter for <see cref="GetDiagnostics"/>. Lives on its own so a unit
    /// test can lock in the exact wording: in particular, the Ok branch must explicitly
    /// say "Compile SUCCEEDED" (not just "status: Ok") so that agents and humans reading
    /// the response can't confuse "no error recorded" with "compile actually ran cleanly".
    /// </summary>
    public static string FormatDiagnostics(
        CompilationStatus status,
        string nodeTypePath,
        string? error,
        DateTimeOffset? startedAt,
        DateTimeOffset? lastCompiledAt,
        JsonSerializerOptions options)
    {
        switch (status)
        {
            case CompilationStatus.Compiling:
            {
                var elapsedMs = startedAt is null
                    ? (long?)null
                    : (long)(DateTimeOffset.UtcNow - startedAt.Value).TotalMilliseconds;
                return JsonSerializer.Serialize(
                    new
                    {
                        status = "Compiling",
                        nodeTypePath,
                        elapsedMs,
                        message = "Compile is IN PROGRESS. The NodeType assembly is not yet available — "
                            + "wait and re-call GetDiagnostics."
                    },
                    options);
            }
            case CompilationStatus.Error:
                return JsonSerializer.Serialize(
                    new
                    {
                        status = "Error",
                        nodeTypePath,
                        error,
                        message = "Compile FAILED. The NodeType assembly was NOT built — see `error` "
                            + "for the Roslyn diagnostics. Fix the source and recycle the NodeType."
                    },
                    options);
            case CompilationStatus.Ok:
                return JsonSerializer.Serialize(
                    new
                    {
                        status = "Ok",
                        nodeTypePath,
                        lastCompiledAt,
                        message = "Compile SUCCEEDED at " + lastCompiledAt?.ToString("u")
                            + ". The NodeType assembly was built without errors and is loaded."
                    },
                    options);
            case CompilationStatus.Unknown:
            default:
                return JsonSerializer.Serialize(
                    new
                    {
                        status = "Unknown",
                        nodeTypePath,
                        message = "NO compile has run since the last invalidation (this is NOT 'Ok'). "
                            + "The assembly state is unknown — trigger a compile (e.g. navigate to a "
                            + "layout area on an instance) and re-call GetDiagnostics."
                    },
                    options);
        }
    }

    /// <summary>
    /// Runs an executable Code node's C# through the kernel (Microsoft.DotNet.Interactive)
    /// and returns status JSON. The target node must have
    /// <c>CodeConfiguration.IsExecutable == true</c>. Emits once when the kernel signals
    /// completion (the kernel hub posts a response to <see cref="SubmitCodeRequest"/>
    /// after the code finishes) or on timeout.
    /// </summary>
    public IObservable<string> ExecuteScript(string path, int timeoutSeconds = 120)
    {
        logger.LogInformation("ExecuteScript called with path={Path}", path);
        if (string.IsNullOrWhiteSpace(path))
            return Observable.Return(JsonSerializer.Serialize(
                new { status = "Error", message = "path is required" },
                hub.JsonSerializerOptions));

        var resolvedPath = ResolvePath(path);

        // Fire-and-forget dispatch. The Code hub creates an Activity at
        // `{partition}/_Activity/{submissionId}` (the kernel runs inside the
        // Activity hub) and writes ActivityLog.Messages + Status as the script
        // executes. We pre-generate the SubmissionId here so we can return the
        // Activity path immediately — callers poll `get @{activityPath}` to
        // observe progress and final status without waiting for an ack.
        //
        // Why no wait? The ack `ExecuteScriptResponse` from the Code hub is a
        // throw-away "got it, here's the activity path" — but routing it back
        // to a hosted MCP session hub is fragile (we lost ~half a day chasing
        // it). The Activity itself is the source of truth: live messages,
        // terminal Status, error details — everything's there. So just return
        // the activity path and let the caller observe.
        var partition = resolvedPath.Split('/', 2)[0];
        if (string.IsNullOrEmpty(partition))
            return Observable.Return(JsonSerializer.Serialize(
                new
                {
                    status = "Error",
                    path = resolvedPath,
                    message = "Could not derive partition from script path."
                },
                hub.JsonSerializerOptions));

        var submissionId = Guid.NewGuid().ToString("N");
        var activityPath = $"{partition}/_Activity/{submissionId}";

        try
        {
            hub.Post(
                new ExecuteScriptRequest { SubmissionId = submissionId },
                o => o.WithTarget(new Address(resolvedPath)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ExecuteScript failed to dispatch for {Path}", resolvedPath);
            return Observable.Return(JsonSerializer.Serialize(
                new { status = "Error", path = resolvedPath, message = ex.Message },
                hub.JsonSerializerOptions));
        }

        return Observable.Return(JsonSerializer.Serialize(
            new
            {
                status = "Dispatched",
                path = resolvedPath,
                submissionId,
                activityPath,
                message = $"Script dispatched. Poll `get @{activityPath}` for live messages " +
                          "and final status (Running → Succeeded/Failed)."
            },
            hub.JsonSerializerOptions));
    }
}
