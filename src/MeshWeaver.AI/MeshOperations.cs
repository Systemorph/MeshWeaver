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
using MeshWeaver.Layout;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh.Services;
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
            .Where(n => n?.Content is Graph.Configuration.NodeTypeDefinition d && IsSettled(d))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(5))
            .Select(n => (n!.Content as Graph.Configuration.NodeTypeDefinition)?.CompilationError)
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

    public IObservable<string> Get(string path)
    {
        logger.LogInformation("Get called with path={Path}", path);

        if (string.IsNullOrWhiteSpace(path))
            return Observable.Return("Error: path is required.");

        var resolvedPath = ResolvePath(path);
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return Observable.Return("Error: path is required.");

        // Handle children query (path/*) — ObserveQuery emits a QueryResultChange whose
        // Initial change contains every matching child in a single batch. Take(1) completes
        // the stream as soon as the first snapshot arrives; no await, no FromAsync bridge.
        if (resolvedPath.EndsWith("/*"))
        {
            var parentPath = resolvedPath[..^2];
            return mesh.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{parentPath}"))
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

        // Single-node content read via GetDataRequest + MeshNodeReference + RegisterCallback.
        // See Doc/Architecture/CqrsAndContentAccess.md — queries are for sets only.
        return TryResolveUnifiedPath(resolvedPath)
            .SelectMany(unified => unified != null
                ? Observable.Return(unified)
                : FetchNode(resolvedPath).SelectMany(node =>
                    {
                        if (node is null)
                            return GetWithBrokenNodeTypeFallback(resolvedPath);
                        return LookupCompilationError(node)
                            .Select(compileError => compileError != null
                                ? JsonSerializer.Serialize(
                                    new { node, compilationError = compileError },
                                    hub.JsonSerializerOptions)
                                : JsonSerializer.Serialize(node, hub.JsonSerializerOptions));
                    }))
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
    /// even though its hub is broken. We read it via
    /// <see cref="IMeshService.QueryAsync"/> as the documented exception to
    /// "queries are for sets only" (see <c>Doc/Architecture/CqrsAndContentAccess.md</c>):
    /// the live content is unreachable, the catalog snapshot is the best we
    /// have, and the wrapped response surfaces the compile error so callers
    /// (Coder agent, MCP, UI overlays) can fix the source instead of seeing a
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
                var compileError = (node?.Content as Graph.Configuration.NodeTypeDefinition)?.CompilationError;
                if (string.IsNullOrEmpty(compileError))
                    return Observable.Return($"Not found: {resolvedPath}");

                // Live ObserveQuery — first emission carries the snapshot; the catalog
                // is the source of truth here (the per-node hub is broken by
                // definition, so live content is unreachable).
                return mesh.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{resolvedPath}"))
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

            void EmitOnce(MeshNode? node)
            {
                if (Interlocked.Exchange(ref emitted, 1) != 0) return;
                observer.OnNext(node);
                observer.OnCompleted();
            }

            cts.Token.Register(() => EmitOnce(null));

            try
            {
                var delivery = hub.Post(
                    new GetDataRequest(new MeshNodeReference()),
                    o => o.WithTarget(new Address(resolvedPath)));
                if (delivery == null) { EmitOnce(null); return Disposable.Create(() => cts.Dispose()); }

                hub.Observe(delivery)
                    .Subscribe(
                        d =>
                        {
                            try
                            {
                                if (d.Message is GetDataResponse resp)
                                {
                                    MeshNode? node = resp.Data as MeshNode;
                                    if (node == null && resp.Data is JsonElement je)
                                        node = je.Deserialize<MeshNode>(hub.JsonSerializerOptions);

                                    // Routing-fallback safety: when no per-node hub
                                    // exists for the requested path, monolith routing
                                    // forwards GetDataRequest to the closest ancestor's
                                    // hub, which returns ITS OWN MeshNode. Treat any
                                    // path mismatch as not-found so callers (Patch,
                                    // Update, Delete) don't accidentally operate on
                                    // an ancestor.
                                    if (node != null
                                        && !string.Equals(node.Path, resolvedPath, StringComparison.OrdinalIgnoreCase))
                                        node = null;

                                    EmitOnce(node);
                                }
                                else
                                {
                                    // Unexpected response — node not found / no handler.
                                    EmitOnce(null);
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "FetchNode callback failed for {Path}", resolvedPath);
                                EmitOnce(null);
                            }
                        },
                        ex =>
                        {
                            // DeliveryFailure or other error — node not found / no handler.
                            logger.LogDebug(ex, "FetchNode delivery failed for {Path}", resolvedPath);
                            EmitOnce(null);
                        });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FetchNode post failed for {Path}", resolvedPath);
                EmitOnce(null);
            }

            return Disposable.Create(() => cts.Dispose());
        });

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

            try
            {
                var delivery = hub.Post(
                    DataChangeRequest.Update([node]),
                    o => o.WithTarget(new Address(node.Path)))!;

                hub.Observe(delivery)
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

            return () => cts.Dispose();
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

        // Try new slash format: address/prefix/path where prefix is a known UCR keyword
        if (addressPart == null)
        {
            var segments = resolvedPath.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                if (UcrPrefixResolver.PrefixToAreaMap.ContainsKey(segments[i]))
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
            try
            {
                var delivery = hub.Post(
                    new GetDataRequest(reference),
                    o => o.WithTarget(address))!;

                hub.Observe(delivery)
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

            return () => { };
        })
        .Timeout(TimeSpan.FromSeconds(10))
        .Catch((TimeoutException _) =>
            Observable.Return<string?>($"Error: Timeout resolving '{remainder}' at {addressPart}"));
    }

    public IObservable<string> Search(string query, string? basePath = null)
    {
        logger.LogInformation("Search called with query={Query}, basePath={BasePath}", query, basePath);

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

        // Snapshot semantics: Take(1) on ObserveQuery gives us the Initial change
        // containing every match for this query in one batch — no async enumeration,
        // no FromAsync bridge.
        return mesh.ObserveQuery<MeshNode>(new MeshQueryRequest { Query = fullQuery, Limit = 50 })
            .Take(1)
            .Select(change =>
            {
                var list = change.Items
                    .Select(node => (object)new { node.Path, node.Name, node.NodeType })
                    .ToImmutableList();
                return JsonSerializer.Serialize(list, hub.JsonSerializerOptions);
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Error searching with query {Query}", query);
                return Observable.Return($"Error: {ex.Message}");
            });
    }

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

            if (string.IsNullOrWhiteSpace(meshNode.Name))
                return Observable.Return("Error: 'name' property is required. Provide a human-readable display name.");

            meshNode = SanitizeNodeId(meshNode);

            // Validate content against schema if both nodeType and content are provided.
            var validationObs = !string.IsNullOrEmpty(meshNode.NodeType) && meshNode.Content != null
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

                var meshNode = SanitizeNodeId(rawNode);

                if (string.IsNullOrWhiteSpace(meshNode.Id))
                {
                    perNode = perNode.Add(Observable.Return(
                        "Error: node is missing 'id'. Every node requires an id — fetch with Get first if unsure."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(meshNode.Name))
                {
                    perNode = perNode.Add(Observable.Return(
                        $"Error: node at {meshNode.Path} has empty 'name'. Provide a non-empty human-readable display name."));
                    continue;
                }

                if (string.IsNullOrEmpty(meshNode.NodeType))
                {
                    perNode = perNode.Add(Observable.Return(
                        $"Error: node at {meshNode.Path} is missing 'nodeType'. Update requires the complete node (from Get). Use Patch for partial updates."));
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
                                .Select(updated =>
                                {
                                    OnNodeChange?.Invoke(new NodeChangeEntry
                                    {
                                        Path = updated.Path,
                                        Operation = "Updated",
                                        VersionBefore = versionBefore,
                                        VersionAfter = updated.Version,
                                        NodeType = updated.NodeType,
                                        NodeName = updated.Name
                                    });
                                    return $"Updated: {updated.Path}";
                                })
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
                    return Observable.Return($"Error: node not found at {resolvedPath}");

                // Content-specific rejections carry the expected schema so agents
                // can recover on the next call without guessing.
                if (jsonObj.ContainsKey("content") && jsonObj["content"] is null)
                    return BuildNullContentError(existing.Path, existing.NodeType!);

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
                            .Select(updated =>
                            {
                                OnNodeChange?.Invoke(new NodeChangeEntry
                                {
                                    Path = updated.Path,
                                    Operation = "Updated",
                                    VersionBefore = versionBefore,
                                    VersionAfter = updated.Version,
                                    NodeType = updated.NodeType,
                                    NodeName = updated.Name
                                });
                                var versionText = updated.Version > versionBefore
                                    ? $" (v{versionBefore} → v{updated.Version})"
                                    : "";
                                return $"Patched: {updated.Path}{versionText}";
                            })
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

            try
            {
                var delivery = hub.Post(
                    new PatchDataRequest(new MeshNodeReference(), new RawJson(rawPatch)),
                    o => o.WithTarget(new Address(resolvedPath)))!;

                hub.Observe(delivery)
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

            return () => cts.Dispose();
        });

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
            return hub.Observe(
                new GetDataRequest(new ContentCollectionReference([collectionName])),
                o => o.WithTarget(targetAddress))
                .Take(1)
                .Select(collectionResponse =>
                {
                    // Deserialize via the hub's JSON options so the naming policy (camelCase)
                    // and all fields — including IsEditable — round-trip correctly. The
                    // manual TryGetProperty form this replaced silently dropped IsEditable
                    // for read-only collections, letting writes through.
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
                    return Observable.FromAsync(async () =>
                    {
                        var collection = await contentService.GetCollectionAsync(qualifiedCollectionName, CancellationToken.None);
                        if (collection == null)
                            return $"Error: failed to initialize collection '{qualifiedCollectionName}'.";

                        var dir = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "";
                        var fileName = Path.GetFileName(filePath);
                        using var ms = new MemoryStream(bytes);
                        await collection.SaveFileAsync(dir, fileName, ms);

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
            return Observable.Return($"Error: {ex.Message}");
        });
    }

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
        var meshConfig = hub.ServiceProvider.GetService<MeshConfiguration>();
        if (meshConfig != null
            && meshConfig.Nodes.TryGetValue(nodeType, out var staticNode)
            && staticNode.HubConfiguration != null)
        {
            return Observable.Return<Func<MessageHubConfiguration, MessageHubConfiguration>?>(staticNode.HubConfiguration);
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
                        var schemaNode = hub.JsonSerializerOptions.GetJsonSchemaAsNode(typeDefinition!.Type);
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
            try
            {
                var delivery = hub.Post(
                    new MoveNodeRequest(resolvedSource, resolvedTarget),
                    o => o.WithTarget(new Address(resolvedSource)))!;

                hub.Observe(delivery)
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

            return () => cts.Dispose();
        });
    }

    /// <summary>
    /// Copies a node and all its descendants to a target namespace. Delegates to
    /// <see cref="NodeCopyHelper.CopyNodeTree"/> — fully reactive pipeline (ObserveQuery +
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
                if (meshConfig != null && meshConfig.Nodes.ContainsKey(nodeTypePath))
                    return Observable.Return(FormatDiagnostics(
                        CompilationStatus.Ok, nodeTypePath,
                        error: null, startedAt: null, lastCompiledAt: null,
                        hub.JsonSerializerOptions));

                // Slow path: subscribe to the NodeType's live stream and wait for
                // the CompileWatcher to settle. Where(settled).Take(1) keeps the
                // read in lockstep with the writer; without it we'd race against
                // the Compiling → Ok/Error write-back and return stale state.
                return hub.GetWorkspace().GetMeshNodeStream(nodeTypePath)
                    .Where(n => n?.Content is Graph.Configuration.NodeTypeDefinition d && IsSettled(d))
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(5))
                    .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
                    .Select(typeNode =>
                    {
                        var def = typeNode?.Content as Graph.Configuration.NodeTypeDefinition;
                        if (def is null)
                            return JsonSerializer.Serialize(
                                new { status = "Unknown", message = $"NodeType '{nodeTypePath}' has no definition" },
                                hub.JsonSerializerOptions);
                        return FormatDiagnosticsFromDef(def, nodeTypePath);
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
    {
        var status = def.CompilationStatus;
        return status == CompilationStatus.Ok || status == CompilationStatus.Error;
    }

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
