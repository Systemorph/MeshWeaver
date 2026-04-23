using System.Collections.Immutable;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
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
    private readonly INodeTypeService? nodeTypeService;

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
        this.nodeTypeService = hub.ServiceProvider.GetService<INodeTypeService>();
    }

    /// <summary>
    /// Looks up the cached compilation error for the owning NodeType of <paramref name="node"/>.
    /// - If <paramref name="node"/> is a NodeType definition, checks its own path.
    /// - Otherwise checks the NodeType's path.
    /// Returns <c>null</c> if no error is recorded.
    /// </summary>
    private string? LookupCompilationError(MeshNode node)
    {
        if (nodeTypeService == null) return null;
        var nodeTypePath = node.Content is Graph.Configuration.NodeTypeDefinition
            ? node.Path
            : node.NodeType;
        return !string.IsNullOrEmpty(nodeTypePath)
            ? nodeTypeService.GetCompilationError(nodeTypePath)
            : null;
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

        // Handle children query (path/*)
        if (resolvedPath.EndsWith("/*"))
        {
            var parentPath = resolvedPath[..^2];
            return Observable.FromAsync(async ct =>
                {
                    var result = ImmutableList<object>.Empty;
                    await foreach (var node in mesh.QueryAsync<MeshNode>(
                        MeshQueryRequest.FromQuery($"namespace:{parentPath}")).WithCancellation(ct))
                    {
                        result = result.Add(new
                        {
                            node.Path,
                            node.Name,
                            node.NodeType,
                            node.Icon
                        });
                    }
                    return JsonSerializer.Serialize(result, hub.JsonSerializerOptions);
                })
                .SubscribeOn(TaskPoolScheduler.Default)
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex, "Error getting data at path {Path}", resolvedPath);
                    return Observable.Return($"Error: {ex.Message}");
                });
        }

        // Unified path first, then fall back to direct node lookup.
        return TryResolveUnifiedPath(resolvedPath)
            .SelectMany(unified => unified != null
                ? Observable.Return(unified)
                : Observable.FromAsync(async ct =>
                    {
                        await foreach (var node in mesh.QueryAsync<MeshNode>(
                            MeshQueryRequest.FromQuery($"path:{resolvedPath}")).WithCancellation(ct))
                        {
                            var compileError = LookupCompilationError(node);
                            return compileError != null
                                ? JsonSerializer.Serialize(
                                    new { node, compilationError = compileError },
                                    hub.JsonSerializerOptions)
                                : JsonSerializer.Serialize(node, hub.JsonSerializerOptions);
                        }
                        return $"Not found: {resolvedPath}";
                    })
                    .SubscribeOn(TaskPoolScheduler.Default))
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Error getting data at path {Path}", resolvedPath);
                return Observable.Return($"Error: {ex.Message}");
            });
    }

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
        // observable from a non-hub thread.
        return Observable.Create<string?>(observer =>
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var delivery = hub.Post(
                    new GetDataRequest(reference),
                    o => o.WithTarget(address))!;

                hub.RegisterCallback(delivery, (d, _) =>
                {
                    try
                    {
                        if (d is IMessageDelivery<DeliveryFailure> failure)
                            observer.OnNext($"Error: {failure.Message.Message ?? "Delivery failed to " + addressPart}");
                        else if (d is IMessageDelivery<GetDataResponse> dataResponse)
                        {
                            var responseMsg = dataResponse.Message;
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
                    return Task.FromResult(d);
                }, cts.Token);
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
            }

            return () => cts.Dispose();
        });
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

        return Observable.FromAsync(async ct =>
            {
                var results = ImmutableList<object>.Empty;
                await foreach (var item in mesh.QueryAsync(
                    new MeshQueryRequest { Query = fullQuery, Limit = 50 }).WithCancellation(ct))
                {
                    if (item is MeshNode node)
                    {
                        results = results.Add(new { node.Path, node.Name, node.NodeType });
                    }
                    else
                    {
                        results = results.Add(item);
                    }
                }
                return JsonSerializer.Serialize(results, hub.JsonSerializerOptions);
            })
            .SubscribeOn(TaskPoolScheduler.Default)
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
            if (!string.IsNullOrEmpty(meshNode.NodeType) && meshNode.Content != null)
            {
                var validationError = ValidateContentWithSchema(meshNode);
                if (validationError != null)
                    return Observable.Return(validationError);
            }

            return mesh.CreateNode(meshNode)
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
                });
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
                    perNode = perNode.Add(Observable.Return(
                        BuildNullContentError(meshNode.Path, meshNode.NodeType!)));
                    continue;
                }

                var validationError = ValidateContentWithSchema(meshNode);
                if (validationError != null)
                {
                    perNode = perNode.Add(Observable.Return(validationError));
                    continue;
                }

                var versionBefore = meshNode.Version;
                var currentPath = meshNode.Path;
                perNode = perNode.Add(
                    mesh.UpdateNode(meshNode)
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
                            Observable.Return($"Error updating {currentPath}: {ex.Message}")));
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

            // Read the current node first, then build the merged update.
            return Observable.FromAsync(ct =>
                    mesh.QueryAsync<MeshNode>($"path:{resolvedPath}").FirstOrDefaultAsync(ct).AsTask())
                .SubscribeOn(TaskPoolScheduler.Default)
                .SelectMany(existing =>
                {
                    if (existing == null)
                        return Observable.Return($"Error: node not found at {resolvedPath}");

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

                    if (jsonObj.ContainsKey("content") && jsonObj["content"] is null)
                        return Observable.Return(BuildNullContentError(existing.Path, existing.NodeType!));

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

                    if (jsonObj.ContainsKey("content") && !string.IsNullOrEmpty(merged.NodeType) && merged.Content != null)
                    {
                        var validationError = ValidateContentWithSchema(merged);
                        if (validationError != null)
                            return Observable.Return(validationError);
                    }

                    if (jsonObj.ContainsKey("name") && string.IsNullOrWhiteSpace(merged.Name))
                        return Observable.Return(
                            $"Error: cannot patch {existing.Path}: 'name' is empty. " +
                            "Provide a non-empty human-readable display name, or omit the 'name' key to keep the current name.");

                    var versionBefore = existing.Version;
                    string? beforeJson;
                    try { beforeJson = SerialisePretty(existing); }
                    catch { beforeJson = null; }

                    return mesh.UpdateNode(merged)
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
                            try
                            {
                                var afterJson = SerialisePretty(updated);
                                var diff = beforeJson is null
                                    ? ""
                                    : DiffUtil.UnifiedDiff(beforeJson, afterJson, updated.Path);
                                return string.IsNullOrEmpty(diff)
                                    ? $"Patched: {updated.Path}{versionText}"
                                    : $"Patched: {updated.Path}{versionText}\n\n```diff\n{diff}```";
                            }
                            catch (Exception serExn)
                            {
                                logger.LogWarning(serExn,
                                    "Patch succeeded but diff rendering failed for {Path}", updated.Path);
                                return $"Patched: {updated.Path}{versionText}";
                            }
                        })
                        .Catch((Exception ex) =>
                            Observable.Return($"Error patching {merged.Path}: {ex.Message}"));
                })
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex, "Error patching node at {Path}", path);
                    return Observable.Return($"Error: {ex.Message}");
                });
        });
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
    /// agent can fill content correctly on the next call.
    /// </summary>
    internal string BuildNullContentError(string path, string nodeType)
    {
        var msg = $"Error: cannot write {path}: 'content' is null. " +
                  "Fetch the node first with Get, modify the returned content in-place, " +
                  "and resend the complete node. Never send null content.";
        var schema = GetContentSchema(nodeType);
        if (schema != null)
            msg += $" Expected content schema for NodeType '{nodeType}': {schema}";
        return msg;
    }

    /// <summary>
    /// Runs schema validation for <paramref name="meshNode"/> and, when invalid,
    /// appends the expected JSON schema to the error so the agent can recover.
    /// Returns null when content is valid (or when no schema is available).
    /// </summary>
    internal string? ValidateContentWithSchema(MeshNode meshNode)
    {
        var validationError = ValidateContentAgainstSchema(meshNode);
        if (validationError == null)
            return null;

        if (!string.IsNullOrEmpty(meshNode.NodeType))
        {
            var schema = GetContentSchema(meshNode.NodeType);
            if (schema != null)
                validationError += $" Expected content schema for NodeType '{meshNode.NodeType}': {schema}";
        }
        return validationError;
    }

    /// <summary>
    /// Returns the JSON schema string for the content type registered against
    /// <paramref name="nodeType"/>, or null if no schema can be derived.
    /// </summary>
    internal string? GetContentSchema(string nodeType)
    {
        try
        {
            var nodeTypeService = hub.ServiceProvider.GetService<INodeTypeService>();
            if (nodeTypeService == null)
                return null;

            var hubConfig = nodeTypeService.GetCachedHubConfiguration(nodeType);
            if (hubConfig == null)
                return null;

            var tempAddress = new Address($"_schema_lookup/{Guid.NewGuid():N}");
            var tempHub = hub.GetHostedHub(tempAddress, hubConfig);
            if (tempHub == null)
                return null;

            try
            {
                var typeRegistry = tempHub.ServiceProvider.GetService<ITypeRegistry>();
                if (typeRegistry == null || !typeRegistry.TryGetType(nodeType, out var typeDefinition))
                    return null;

                var schemaNode = hub.JsonSerializerOptions.GetJsonSchemaAsNode(typeDefinition!.Type);
                return schemaNode.ToJsonString();
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
    }

    /// <summary>
    /// Validates node content against the content type for its NodeType.
    /// Creates a temporary hub with the NodeType's configuration to find the
    /// registered content type, then attempts to deserialize the content into that type.
    /// Returns an error message if invalid, or null if valid/no schema available.
    /// </summary>
    internal string? ValidateContentAgainstSchema(MeshNode meshNode)
    {
        try
        {
            var nodeTypeService = hub.ServiceProvider.GetService<INodeTypeService>();
            if (nodeTypeService == null)
                return null;

            var hubConfig = nodeTypeService.GetCachedHubConfiguration(meshNode.NodeType!);
            if (hubConfig == null)
                return null;

            var tempAddress = new Address($"_schema_validation/{Guid.NewGuid():N}");
            var tempHub = hub.GetHostedHub(tempAddress, hubConfig);
            if (tempHub == null)
                return null;

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
                    if (deserialized == null)
                        return $"Error: Content is null after deserialization for NodeType '{meshNode.NodeType}'.";

                    return null;
                }
                catch (JsonException ex)
                {
                    return $"Error: Content does not match the schema for NodeType '{meshNode.NodeType}'. {ex.Message}";
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

                hub.RegisterCallback(delivery, (d, _) =>
                {
                    try
                    {
                        if (d is IMessageDelivery<DeliveryFailure> failure)
                        {
                            observer.OnNext(
                                $"Error moving {resolvedSource} -> {resolvedTarget}: {failure.Message.Message ?? "delivery failed"}");
                        }
                        else if (d is IMessageDelivery<MoveNodeResponse> resp)
                        {
                            var msg = resp.Message;
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
                    return Task.FromResult(d);
                }, cts.Token);
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
    /// <see cref="NodeCopyHelper.CopyNodeTreeAsync"/> — the helper itself is async
    /// enumeration over <see cref="IMeshService"/>, which we wrap via
    /// <c>Observable.FromAsync</c> on the task-pool scheduler so the copy never
    /// occupies the caller's hub.
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

        return Observable.FromAsync(ct =>
                NodeCopyHelper.CopyNodeTreeAsync(mesh, mesh, hub, resolvedSource, resolvedTarget, force, logger))
            .SubscribeOn(TaskPoolScheduler.Default)
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
            nodeTypeService?.InvalidateCache(resolvedPath);

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
        if (nodeTypeService == null)
            return Observable.Return(JsonSerializer.Serialize(
                new { status = "Unknown", message = "INodeTypeService not registered on this hub" },
                hub.JsonSerializerOptions));

        return Observable.FromAsync(ct =>
                mesh.QueryAsync<MeshNode>(MeshQueryRequest.FromQuery($"path:{resolvedPath}"))
                    .FirstOrDefaultAsync(ct).AsTask())
            .SubscribeOn(TaskPoolScheduler.Default)
            .Select(node =>
            {
                var nodeTypePath = node?.Content is Graph.Configuration.NodeTypeDefinition
                    ? node.Path
                    : node?.NodeType;

                if (string.IsNullOrEmpty(nodeTypePath))
                    return JsonSerializer.Serialize(
                        new { status = "Unknown", message = $"Not found: {resolvedPath}" },
                        hub.JsonSerializerOptions);

                var status = nodeTypeService.GetStatus(nodeTypePath);
                return FormatDiagnostics(
                    status,
                    nodeTypePath,
                    error: status == CompilationStatus.Error ? nodeTypeService.GetCompilationError(nodeTypePath) : null,
                    startedAt: status == CompilationStatus.Compiling ? nodeTypeService.GetCompilationStartedAt(nodeTypePath) : null,
                    lastCompiledAt: status == CompilationStatus.Ok ? nodeTypeService.GetLastSuccessfulCompileAt(nodeTypePath) : null,
                    hub.JsonSerializerOptions);
            });
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

        return Observable.FromAsync(ct =>
                mesh.QueryAsync<MeshNode>(MeshQueryRequest.FromQuery($"path:{resolvedPath}"))
                    .FirstOrDefaultAsync(ct).AsTask())
            .SubscribeOn(TaskPoolScheduler.Default)
            .SelectMany(node =>
            {
                if (node is null)
                    return Observable.Return(JsonSerializer.Serialize(
                        new { status = "Error", message = $"Node not found: {resolvedPath}" },
                        hub.JsonSerializerOptions));

                string? code = null;
                bool isExecutable = false;
                if (node.Content is Mesh.CodeConfiguration cc)
                {
                    code = cc.Code;
                    isExecutable = cc.IsExecutable;
                }
                else if (node.Content is System.Text.Json.JsonElement je)
                {
                    if (je.TryGetProperty("code", out var codeProp)) code = codeProp.GetString();
                    if (je.TryGetProperty("isExecutable", out var execProp)) isExecutable = execProp.GetBoolean();
                }

                if (string.IsNullOrWhiteSpace(code))
                    return Observable.Return(JsonSerializer.Serialize(
                        new { status = "Error", message = $"Node at {resolvedPath} has no Code content" },
                        hub.JsonSerializerOptions));

                if (!isExecutable)
                    return Observable.Return(JsonSerializer.Serialize(
                        new { status = "Error", message = $"Node at {resolvedPath} is not marked IsExecutable=true" },
                        hub.JsonSerializerOptions));

                var kernelAddress = AddressExtensions.CreateKernelAddress(
                    "code-" + resolvedPath.Replace('/', '-'));
                var submissionId = Guid.NewGuid().ToString("N");

                return Observable.Create<string>(observer =>
                {
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                    var completed = 0;

                    void EmitOnce(Func<object> payloadFactory)
                    {
                        if (Interlocked.Exchange(ref completed, 1) != 0) return;
                        try
                        {
                            observer.OnNext(JsonSerializer.Serialize(payloadFactory(), hub.JsonSerializerOptions));
                        }
                        catch (Exception ex) { observer.OnError(ex); return; }
                        observer.OnCompleted();
                    }

                    try
                    {
                        var delivery = hub.Post(
                            new SubmitCodeRequest(code) { Id = submissionId },
                            o => o.WithTarget(kernelAddress))!;

                        hub.RegisterCallback(delivery, (d, _) =>
                        {
                            if (d is IMessageDelivery<SubmitCodeResponse> resp)
                            {
                                var r = resp.Message;
                                EmitOnce(() => new
                                {
                                    status = r.Success ? "Executed" : "Error",
                                    path = resolvedPath,
                                    submissionId = r.SubmissionId,
                                    kernelAddress = kernelAddress.ToString(),
                                    outputUrl = $"{kernelAddress}/{r.SubmissionId}",
                                    error = r.Error,
                                    message = r.Success
                                        ? "Code dispatched and kernel signalled completion. Side effects "
                                          + "(e.g. mesh.CreateNode calls inside the script) have happened. "
                                          + "Console output / return value is at the kernel layout area path above."
                                        : $"Kernel reported failure: {r.Error}"
                                });
                            }
                            else if (d is IMessageDelivery<DeliveryFailure> failure)
                            {
                                EmitOnce(() => new
                                {
                                    status = "Error",
                                    path = resolvedPath,
                                    submissionId,
                                    message = $"Delivery failed: {failure.Message.Message ?? "unknown"}"
                                });
                            }
                            else
                            {
                                EmitOnce(() => new
                                {
                                    status = "Executed",
                                    path = resolvedPath,
                                    submissionId,
                                    kernelAddress = kernelAddress.ToString(),
                                    outputUrl = $"{kernelAddress}/{submissionId}",
                                    message = $"Unexpected response {d.Message?.GetType().Name} — check kernel progress area for status."
                                });
                            }
                            return Task.FromResult(d);
                        }, cts.Token);

                        cts.Token.Register(() => EmitOnce(() => new
                        {
                            status = "Timeout",
                            path = resolvedPath,
                            timeoutSeconds,
                            message = $"Kernel did not signal completion within {timeoutSeconds}s. Side effects may still have happened — check the kernel's progress area."
                        }));
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "ExecuteScript failed for {Path}", resolvedPath);
                        EmitOnce(() => new { status = "Error", path = resolvedPath, message = ex.Message });
                    }

                    return () => cts.Dispose();
                });
            });
    }
}
