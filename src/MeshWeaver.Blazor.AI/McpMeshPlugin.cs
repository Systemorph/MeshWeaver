using System.ComponentModel;
using MeshWeaver.AI;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MeshWeaver.Blazor.AI;

/// <summary>
/// MCP wrapper exposing mesh operations as MCP tools.
/// Thin wrapper over MeshOperations with MCP attributes and URL-based NavigateTo.
/// </summary>
[McpServerToolType]
public class McpMeshPlugin
{
    private readonly MeshOperations ops;
    private readonly ILogger<McpMeshPlugin> logger;
    private readonly string baseUrl;

    public McpMeshPlugin(
        IMessageHub hub,
        IOptions<McpConfiguration>? config = null)
    {
        ops = new MeshOperations(hub);
        logger = hub.ServiceProvider.GetRequiredService<ILogger<McpMeshPlugin>>();
        baseUrl = config?.Value.BaseUrl ?? "http://localhost:5000";
    }

    [McpServerTool]
    [Description("Retrieves a node from the mesh by path. Supports @ prefix shorthand, /* for children, and Unified Path prefixes (path/schema:, path/model:).")]
    public Task<string> Get(
        [Description("Path to data (e.g., @graph/org1, @Agent/*, @Cornerstone/schema:, @Cornerstone/schema:TypeName, @Cornerstone/model:)")] string path)
        => ops.Get(path);

    [McpServerTool]
    [Description("Searches the mesh using GitHub-style query syntax. Returns up to 50 matching nodes.")]
    public Task<string> Search(
        [Description("Query string (e.g., 'nodeType:Agent', 'laptop', 'path:ACME scope:descendants', 'name:*sales*')")] string query,
        [Description("Base path to search from (e.g., @graph). Empty for all.")] string? basePath = null)
        => ops.Search(query, basePath);

    [McpServerTool]
    [Description("Creates a new node in the mesh. Pass a JSON MeshNode object with id, namespace, name, nodeType, and content fields.")]
    public Task<string> Create(
        [Description("JSON MeshNode object to create (e.g., {\"id\": \"NewOrg\", \"namespace\": \"ACME\", \"name\": \"New Org\", \"nodeType\": \"Organization\", \"content\": {}})")] string node)
        => ops.Create(node);

    [McpServerTool]
    [Description("Updates existing nodes in the mesh. Pass a JSON array of complete MeshNode objects. Always Get before Update — the entire node is replaced, not merged.")]
    public Task<string> Update(
        [Description("JSON array of MeshNode objects with all fields (get existing node first, modify, then pass here)")] string nodes)
        => ops.Update(nodes);

    [McpServerTool]
    [Description("Partial update of a single node. Only the keys present in 'fields' are changed; omitted keys preserve existing values. Do NOT include 'content' unless overwriting — never set 'content' to null. Prefer this over Update for small edits like icon/name/category.")]
    public Task<string> Patch(
        [Description("Path to the node (e.g., @User/rbuergi/my-node)")] string path,
        [Description("JSON object with ONLY the fields to change. Examples: {\"icon\": \"<svg>...</svg>\"}, {\"name\": \"New Name\"}.")] string fields)
        => ops.Patch(path, fields);

    [McpServerTool]
    [Description("Deletes one or more nodes from the mesh by path. Recursive: deleting a parent removes all descendants. To remove a subtree, just pass the root path — children do not need to be enumerated.")]
    public Task<string> Delete(
        [Description("JSON array of path strings to delete (e.g., [\"ACME/OldProject\", \"ACME/ArchivedTask\"])")] string paths)
        => ops.Delete(paths);

    [McpServerTool]
    [Description("Moves a node and its descendants to a new path. Equivalent to the Move menu item. Requires Delete on the source namespace and Create on the target. Source and target are full paths (namespace + id), e.g. 'OrgA/Child' -> 'OrgB/Child'.")]
    public Task<string> Move(
        [Description("Current path of the node (e.g., @OrgA/Child)")] string sourcePath,
        [Description("New path for the node (e.g., @OrgB/Child)")] string targetPath)
        => ops.Move(sourcePath, targetPath);

    [McpServerTool]
    [Description("Copies a node and all its descendants to a target namespace. Equivalent to the Copy menu item. Source ids are preserved; paths are rewritten under the target namespace.")]
    public Task<string> Copy(
        [Description("Current path of the node to copy (e.g., @OrgA/Child)")] string sourcePath,
        [Description("Target namespace to copy under (e.g., @OrgB)")] string targetNamespace,
        [Description("Overwrite existing nodes at the target. Default: false.")] bool force = false)
        => ops.Copy(sourcePath, targetNamespace, force);

    [McpServerTool]
    [Description("Returns a URL to view a node in the MeshWeaver UI. Use this to provide links for users to open in their browser.")]
    public string NavigateTo(
        [Description("Path to navigate to (e.g., @graph/org1)")] string path)
    {
        logger.LogInformation("MCP NavigateTo called with path={Path}", path);

        var resolvedPath = MeshOperations.ResolvePath(path);
        return $"{baseUrl}/node/{Uri.EscapeDataString(resolvedPath)}";
    }

    [McpServerTool]
    [Description("Returns compilation diagnostics for a NodeType (or any instance of one). Status is 'Ok' when the type compiled cleanly, 'Error' with details when it failed, 'Compiling' while a compile is in progress (with elapsedMs), or 'Unknown' when no compile has happened yet. Use after creating/updating a NodeType to verify it actually compiles — a NodeType that doesn't compile is not 'done'.")]
    public Task<string> GetDiagnostics(
        [Description("Path to a NodeType (e.g., @Systemorph/SocialMedia/Profile) or to any instance of one")] string path)
        => ops.GetDiagnostics(path);

    [McpServerTool]
    [Description("Recycles the hub at the given path by posting DisposeRequest. Forces a fresh hub initialization on the next access — use after fixing a broken NodeType, after editing the `sources` list, or whenever a grain is stuck in a cached bad state. Returns {status:'Recycled', path}. Wait ~100ms before the next access so the grain teardown completes.")]
    public Task<string> Recycle(
        [Description("Path to the node (e.g., @Systemorph/SocialMedia/Profile). Use the NodeType path to recycle the whole type; use an instance path to recycle just that instance's hub.")] string path)
        => ops.Recycle(path);
}

/// <summary>
/// Configuration options for MCP integration.
/// </summary>
public class McpConfiguration
{
    /// <summary>
    /// Base URL for the MeshWeaver UI. Used for generating NavigateTo URLs.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:5000";
}
