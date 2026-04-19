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
    [Description("Deletes one or more nodes from the mesh by path.")]
    public Task<string> Delete(
        [Description("JSON array of path strings to delete (e.g., [\"ACME/OldProject\", \"ACME/ArchivedTask\"])")] string paths)
        => ops.Delete(paths);

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
