using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Shared mesh operations for AI agents and MCP tools.
/// All operations go through Hub messaging to enforce security via validators.
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
    /// Resolves @ prefix and quotes from path. Example: @graph/org1 -> graph/org1, "@content/My File.md" -> content/My File.md
    /// </summary>
    public static string ResolvePath(string path)
    {
        // Strip surrounding quotes (autocomplete wraps spaced paths in quotes)
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            path = path[1..^1];

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

    public async Task<string> Get(string path)
    {
        logger.LogInformation("Get called with path={Path}", path);

        var resolvedPath = ResolvePath(path);

        try
        {
            // Handle children query (path/*)
            if (resolvedPath.EndsWith("/*"))
            {
                var parentPath = resolvedPath[..^2];
                var result = ImmutableList<object>.Empty;
                var query = $"namespace:{parentPath}";
                await foreach (var node in mesh.QueryAsync<MeshNode>(MeshQueryRequest.FromQuery(query)))
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
            }

            // Check for Unified Path prefix (e.g., "ACME/schema:", "ACME/data:Collection/id")
            var unifiedResult = await TryResolveUnifiedPathAsync(resolvedPath);
            if (unifiedResult != null)
                return unifiedResult;

            // Get single node via query (reads from persistence, not cached)
            await foreach (var node in mesh.QueryAsync<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{resolvedPath}")))
            {
                return JsonSerializer.Serialize(node, hub.JsonSerializerOptions);
            }

            return $"Not found: {resolvedPath}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error getting data at path {Path}", resolvedPath);
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Tries to resolve a path as a Unified Path with prefix (schema/, model/, data/, content/).
    /// Supports both legacy colon format (address/prefix:path) and new slash format (address/prefix/path).
    /// Parses the path to find the prefix, splits into address and remainder,
    /// then routes data request to the resolved address.
    /// Returns null if the path is not a Unified Path.
    /// </summary>
    private async Task<string?> TryResolveUnifiedPathAsync(string resolvedPath)
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
                    // Found a UCR prefix at segment i — everything before is address, everything from i onwards is remainder
                    if (i > 0)
                    {
                        addressPart = string.Join("/", segments.Take(i));
                        remainder = string.Join("/", segments.Skip(i));
                    }
                    else
                    {
                        // Prefix at the start (e.g., "content/file.md") — relative path, no address
                        addressPart = null;
                        remainder = resolvedPath;
                    }
                    break;
                }
            }
        }

        if (remainder == null)
            return null;

        var reference = new UnifiedReference(remainder);
        Address address;
        if (!string.IsNullOrEmpty(addressPart))
        {
            address = new Address(addressPart);
        }
        else
        {
            // No address — route to the current hub
            address = hub.Address;
        }

        logger.LogInformation("Resolving Unified Path: address={Address}, remainder={Remainder}",
            addressPart, remainder);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var delivery = hub.Post(
            new GetDataRequest(reference),
            o => o.WithTarget(address))!;
        var callbackResponse = await hub.RegisterCallback(delivery, (d, _) => Task.FromResult(d), cts.Token);

        // Handle routing failures (e.g., node hub not found in Orleans)
        if (callbackResponse is IMessageDelivery<DeliveryFailure> failure)
            return $"Error: {failure.Message.Message ?? "Delivery failed to " + addressPart}";

        if (callbackResponse is not IMessageDelivery<GetDataResponse> dataResponse)
            return $"Error: Unexpected response type {callbackResponse.Message?.GetType().Name} for {remainder} at {addressPart}";

        var responseMsg = dataResponse.Message;

        if (responseMsg.Error != null)
            return $"Error: {responseMsg.Error}";

        return JsonSerializer.Serialize(responseMsg.Data, hub.JsonSerializerOptions);
    }

    public async Task<string> Search(string query, string? basePath = null)
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
            // Remove empty namespace: placeholder — basePath provides the namespace context.
            // Use namespace: (not path:) so scope defaults to Children (search within, not exact).
            var cleanQuery = query.Replace("namespace:", "").Trim();
            fullQuery = $"namespace:{resolvedBase} {cleanQuery}".Trim();
        }

        try
        {
            var results = ImmutableList<object>.Empty;
            await foreach (var item in mesh.QueryAsync(new MeshQueryRequest { Query = fullQuery, Limit = 50 }))
            {
                if (item is MeshNode node)
                {
                    results = results.Add(new
                    {
                        node.Path,
                        node.Name,
                        node.NodeType
                    });
                }
                else
                {
                    results = results.Add(item);
                }
            }

            return JsonSerializer.Serialize(results, hub.JsonSerializerOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error searching with query {Query}", query);
            return $"Error: {ex.Message}";
        }
    }

    public async Task<string> Create(string node)
    {
        logger.LogInformation("Create called");

        try
        {
            var sanitized = RepairJson(node);
            var meshNode = JsonSerializer.Deserialize<MeshNode>(sanitized, hub.JsonSerializerOptions);
            if (meshNode == null)
                return "Invalid node: deserialized to null.";

            if (string.IsNullOrWhiteSpace(meshNode.Name))
                return "Error: 'name' property is required. Provide a human-readable display name.";

            meshNode = SanitizeNodeId(meshNode);

            // Validate content against schema if both nodeType and content are provided
            if (!string.IsNullOrEmpty(meshNode.NodeType) && meshNode.Content != null)
            {
                var validationError = await ValidateContentWithSchemaAsync(meshNode);
                if (validationError != null)
                    return validationError;
            }

            var tcs = new TaskCompletionSource<string>();
            mesh.CreateNode(meshNode).Subscribe(
                created =>
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
                    tcs.TrySetResult($"Created: {created.Path}");
                },
                ex => tcs.TrySetResult($"Error creating node: {ex.Message}"));
            return await tcs.Task;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Create: invalid JSON, length={Length}", node.Length);
            return $"Invalid JSON: {ex.Message}. Tip: ensure all quotes and special characters in markdown content are properly escaped for JSON strings.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error creating node");
            return $"Error: {ex.Message}";
        }
    }

    public async Task<string> Update(string nodes)
    {
        logger.LogInformation("Update called");

        try
        {
            var sanitized = RepairJson(nodes);
            var nodeList = JsonSerializer.Deserialize<List<MeshNode>>(sanitized, hub.JsonSerializerOptions);
            if (nodeList == null || nodeList.Count == 0)
                return "No nodes provided.";

            var results = ImmutableList<string>.Empty;
            foreach (var rawNode in nodeList)
            {
                if (rawNode == null)
                {
                    results = results.Add("Error: array contained a null entry. " +
                                "Each array element must be a complete MeshNode JSON object.");
                    continue;
                }

                var meshNode = SanitizeNodeId(rawNode);

                // Reject empty identity — without id we cannot address the node.
                if (string.IsNullOrWhiteSpace(meshNode.Id))
                {
                    results = results.Add("Error: node is missing 'id'. " +
                                "Every node requires an id — fetch with Get first if unsure.");
                    continue;
                }

                // Reject empty name — downstream UI and streams key off Name.
                if (string.IsNullOrWhiteSpace(meshNode.Name))
                {
                    results = results.Add($"Error: node at {meshNode.Path} has empty 'name'. " +
                                "Provide a non-empty human-readable display name.");
                    continue;
                }

                // Reject partial nodes — Update does full replacement.
                // Use Patch for partial changes instead.
                if (string.IsNullOrEmpty(meshNode.NodeType))
                {
                    results = results.Add($"Error: node at {meshNode.Path} is missing 'nodeType'. " +
                                "Update requires the complete node (from Get). Use Patch for partial updates.");
                    continue;
                }

                // Reject updates that would blank out content — agents must always send the
                // full content payload. Returning the schema lets the agent reconstruct it.
                if (meshNode.Content == null)
                {
                    results = results.Add(await BuildNullContentErrorAsync(meshNode.Path, meshNode.NodeType!));
                    continue;
                }

                // Validate the content against the registered content type for this NodeType.
                var validationError = await ValidateContentWithSchemaAsync(meshNode);
                if (validationError != null)
                {
                    results = results.Add(validationError);
                    continue;
                }

                var versionBefore = meshNode.Version;
                var updateTcs = new TaskCompletionSource<string>();
                mesh.UpdateNode(meshNode).Subscribe(
                    updated =>
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
                        updateTcs.TrySetResult($"Updated: {updated.Path}");
                    },
                    ex => updateTcs.TrySetResult($"Error updating {meshNode.Path}: {ex.Message}"));
                results = results.Add(await updateTcs.Task);
            }

            return string.Join("\n", results);
        }
        catch (JsonException ex)
        {
            return $"Invalid JSON: {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error updating nodes");
            return $"Error: {ex.Message}";
        }
    }

    public async Task<string> Patch(string path, string fields)
    {
        logger.LogInformation("Patch called for path={Path}", path);

        try
        {
            var resolvedPath = ResolvePath(path);
            var existing = await mesh.QueryAsync<MeshNode>($"path:{resolvedPath}").FirstOrDefaultAsync();
            if (existing == null)
                return $"Error: node not found at {resolvedPath}";

            var sanitized = RepairJson(fields);
            var jsonObj = JsonNode.Parse(sanitized) as JsonObject;
            if (jsonObj == null)
                return "Error: fields must be a JSON object";

            // Reject patches that explicitly blank out content (key present, value null).
            // Omitting the key entirely is fine — that preserves existing content.
            if (jsonObj.ContainsKey("content") && jsonObj["content"] is null)
                return await BuildNullContentErrorAsync(existing.Path, existing.NodeType!);

            // Deserialize to get typed values using the hub's serializer options
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

            // If the patch touches content, validate the merged content against the node's schema.
            // This protects downstream consumers (sync streams, persistence) from shape-broken writes.
            if (jsonObj.ContainsKey("content") && !string.IsNullOrEmpty(merged.NodeType) && merged.Content != null)
            {
                var validationError = await ValidateContentWithSchemaAsync(merged);
                if (validationError != null)
                    return validationError;
            }

            // Reject empty or effectively-empty names — empty string names corrupt UI
            // and downstream streams that key off Name.
            if (jsonObj.ContainsKey("name") && string.IsNullOrWhiteSpace(merged.Name))
                return $"Error: cannot patch {existing.Path}: 'name' is empty. " +
                       "Provide a non-empty human-readable display name, or omit the 'name' key to keep the current name.";

            var versionBefore = existing.Version;
            var patchTcs = new TaskCompletionSource<string>();
            mesh.UpdateNode(merged).Subscribe(
                updated =>
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

                    // Silent-failure guard: if the version did not increment, the write did
                    // not commit (likely a stale snapshot read or a routing-layer no-op).
                    // The agent must see this and retry/refresh — never report success on a no-op.
                    if (updated.Version == versionBefore)
                    {
                        logger.LogWarning(
                            "Patch silent-failure on {Path}: version unchanged ({Version}) — write did not commit",
                            updated.Path, versionBefore);
                        patchTcs.TrySetResult(
                            $"Error: patch on {updated.Path} did not commit (version stayed at {versionBefore}). " +
                            "This usually means a stale snapshot — retry after re-fetching the node.");
                        return;
                    }

                    patchTcs.TrySetResult($"Patched: {updated.Path} (v{versionBefore} → v{updated.Version})");
                },
                ex => patchTcs.TrySetResult($"Error patching {merged.Path}: {ex.Message}"));
            return await patchTcs.Task;
        }
        catch (JsonException ex)
        {
            return $"Invalid JSON: {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error patching node at {Path}", path);
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Sanitizes a MeshNode's Id: if the Id contains slashes, splits it into proper Id + Namespace.
    /// This prevents duplicate rows in the DB (the DB has a CHECK constraint blocking slashes in id).
    /// </summary>
    private MeshNode SanitizeNodeId(MeshNode node)
    {
        if (string.IsNullOrEmpty(node.Id) || !node.Id.Contains('/'))
            return node;

        // Split full path into namespace + id
        var lastSlash = node.Id.LastIndexOf('/');
        var ns = node.Id[..lastSlash];
        var id = node.Id[(lastSlash + 1)..];

        // If the node already has a namespace, prepend it
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

        // Try parsing first — if it's valid, return as-is
        try
        {
            using var doc = JsonDocument.Parse(json);
            return json;
        }
        catch (JsonException)
        {
            // Fall through to repair
        }

        // Repair: try truncating to last complete JSON structure
        // Find the last closing brace/bracket that makes valid JSON
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
                catch (JsonException)
                {
                    // Try next position
                }
            }
        }

        return json; // Return original if repair fails
    }

    public Task<string> Delete(string paths)
    {
        logger.LogInformation("Delete called");

        try
        {
            var pathList = JsonSerializer.Deserialize<List<string>>(paths, hub.JsonSerializerOptions);
            if (pathList == null || pathList.Count == 0)
                return Task.FromResult("No paths provided.");

            var results = ImmutableList<string>.Empty;
            foreach (var path in pathList)
            {
                var resolvedPath = ResolvePath(path);
                // Fire-and-forget: post delete request, don't await (avoids deadlock)
                mesh.DeleteNode(resolvedPath).Subscribe();
                results = results.Add($"Delete requested: {resolvedPath}");
            }

            return Task.FromResult(string.Join("\n", results));
        }
        catch (JsonException ex)
        {
            return Task.FromResult($"Invalid JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error deleting nodes");
            return Task.FromResult($"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the standard "content is null" rejection message for Update/Patch,
    /// embedding the JSON schema for the node's content type when available so the
    /// agent can fill content correctly on the next call.
    /// </summary>
    internal async Task<string> BuildNullContentErrorAsync(string path, string nodeType)
    {
        var msg = $"Error: cannot write {path}: 'content' is null. " +
                  "Fetch the node first with Get, modify the returned content in-place, " +
                  "and resend the complete node. Never send null content.";
        var schema = await GetContentSchemaAsync(nodeType);
        if (schema != null)
            msg += $" Expected content schema for NodeType '{nodeType}': {schema}";
        return msg;
    }

    /// <summary>
    /// Runs schema validation for <paramref name="meshNode"/> and, when invalid,
    /// appends the expected JSON schema to the error so the agent can recover.
    /// Returns null when content is valid (or when no schema is available).
    /// </summary>
    internal async Task<string?> ValidateContentWithSchemaAsync(MeshNode meshNode)
    {
        var validationError = await ValidateContentAgainstSchemaAsync(meshNode);
        if (validationError == null)
            return null;

        if (!string.IsNullOrEmpty(meshNode.NodeType))
        {
            var schema = await GetContentSchemaAsync(meshNode.NodeType);
            if (schema != null)
                validationError += $" Expected content schema for NodeType '{meshNode.NodeType}': {schema}";
        }
        return validationError;
    }

    /// <summary>
    /// Returns the JSON schema string for the content type registered against
    /// <paramref name="nodeType"/>, or null if no schema can be derived.
    /// </summary>
    internal Task<string?> GetContentSchemaAsync(string nodeType)
    {
        try
        {
            var nodeTypeService = hub.ServiceProvider.GetService<INodeTypeService>();
            if (nodeTypeService == null)
                return Task.FromResult<string?>(null);

            var hubConfig = nodeTypeService.GetCachedHubConfiguration(nodeType);
            if (hubConfig == null)
                return Task.FromResult<string?>(null);

            var tempAddress = new Address($"_schema_lookup/{Guid.NewGuid():N}");
            var tempHub = hub.GetHostedHub(tempAddress, hubConfig);
            if (tempHub == null)
                return Task.FromResult<string?>(null);

            try
            {
                var typeRegistry = tempHub.ServiceProvider.GetService<ITypeRegistry>();
                if (typeRegistry == null || !typeRegistry.TryGetType(nodeType, out var typeDefinition))
                    return Task.FromResult<string?>(null);

                var schemaNode = hub.JsonSerializerOptions.GetJsonSchemaAsNode(typeDefinition!.Type);
                return Task.FromResult<string?>(schemaNode.ToJsonString());
            }
            finally
            {
                tempHub.Dispose();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Schema retrieval skipped for NodeType {NodeType}", nodeType);
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Validates node content against the content type for its NodeType.
    /// Creates a temporary hub with the NodeType's configuration to find the
    /// registered content type, then attempts to deserialize the content into that type.
    /// Returns an error message if invalid, or null if valid/no schema available.
    /// </summary>
    internal Task<string?> ValidateContentAgainstSchemaAsync(MeshNode meshNode)
    {
        try
        {
            var nodeTypeService = hub.ServiceProvider.GetService<INodeTypeService>();
            if (nodeTypeService == null)
                return Task.FromResult<string?>(null);

            var hubConfig = nodeTypeService.GetCachedHubConfiguration(meshNode.NodeType!);
            if (hubConfig == null)
                return Task.FromResult<string?>(null);

            // Create a temporary hub with the NodeType's config to access its type registry
            var tempAddress = new Address($"_schema_validation/{Guid.NewGuid():N}");
            var tempHub = hub.GetHostedHub(tempAddress, hubConfig);
            if (tempHub == null)
                return Task.FromResult<string?>(null);

            try
            {
                // Find the content type from the hub's type registry
                var typeRegistry = tempHub.ServiceProvider.GetService<ITypeRegistry>();
                if (typeRegistry == null || !typeRegistry.TryGetType(meshNode.NodeType!, out var typeDefinition))
                    return Task.FromResult<string?>(null);

                var contentType = typeDefinition!.Type;

                // Serialize content to JSON and try to deserialize into the target type
                var contentJson = JsonSerializer.Serialize(meshNode.Content, hub.JsonSerializerOptions);
                try
                {
                    var deserialized = JsonSerializer.Deserialize(contentJson, contentType, hub.JsonSerializerOptions);
                    if (deserialized == null)
                        return Task.FromResult<string?>($"Error: Content is null after deserialization for NodeType '{meshNode.NodeType}'.");

                    return Task.FromResult<string?>(null); // Valid
                }
                catch (JsonException ex)
                {
                    return Task.FromResult<string?>($"Error: Content does not match the schema for NodeType '{meshNode.NodeType}'. {ex.Message}");
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
            return Task.FromResult<string?>(null);
        }
    }
}
