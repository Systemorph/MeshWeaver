using System.ComponentModel;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Plugin providing mesh operations for AI agents.
/// Thin wrapper over MeshOperations with AITool factory and layout-based NavigateTo.
/// Resolves relative paths against the current chat context.
/// </summary>
public class MeshPlugin(IMessageHub hub, IAgentChat chat)
{
    private readonly MeshOperations ops = new(hub) { OnNodeChange = change => chat.ForwardNodeChange?.Invoke(change) };
    private readonly ILogger<MeshPlugin> logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshPlugin>>();

    [Description("Retrieves a node or content from the mesh by path. Paths are relative to current context; use @/ prefix for absolute paths. Supports Unified Path prefixes (schema:, model:, data:, content:, collection:, area:, layoutAreas:).")]
    public Task<string> Get(
        [Description("Path to data. Relative: @content:file.docx, @MyChild/*. Absolute: @/OrgA/Doc, @/OrgA/content:file.docx")] string path)
        => ops.Get(ResolveContextPath(path));

    [Description("Searches the mesh using GitHub-style query syntax.")]
    public Task<string> Search(
        [Description("Query string (e.g., 'nodeType:Agent', 'path:ACME scope:descendants', 'name:*sales*')")] string query,
        [Description("Base path to search from (e.g., @graph). Empty for all.")] string? basePath = null)
        => ops.Search(query, basePath != null ? ResolveContextPath(basePath) : null);

    [Description("Creates a new node in the mesh. ALWAYS set the 'name' property to a human-readable display name.")]
    public Task<string> Create(
        [Description("JSON MeshNode with required: id, name, nodeType, namespace. Example: {\"id\":\"my-page\",\"namespace\":\"MyOrg\",\"name\":\"My Page\",\"nodeType\":\"Markdown\"}")] string node)
        => ops.Create(node);

    [Description("Full replacement update of existing nodes. Pass a JSON array of complete MeshNode objects (from Get). WARNING: all fields are replaced — missing fields become null.")]
    public Task<string> Update(
        [Description("JSON array of complete MeshNode objects")] string nodes)
        => ops.Update(nodes);

    [Description("Partial update of a single node. Only the specified fields are changed; all other fields are preserved. Use this for simple changes like updating icon, name, or content without needing to Get the full node first.")]
    public Task<string> Patch(
        [Description("Path to the node (e.g., @User/rbuergi/my-node)")] string path,
        [Description("JSON object with only the fields to update (e.g., {\"icon\": \"<svg>...</svg>\"} or {\"name\": \"New Name\", \"content\": {...}})")] string fields)
        => ops.Patch(ResolveContextPath(path), fields);

    [Description("Deletes nodes from the mesh by path.")]
    public Task<string> Delete(
        [Description("JSON array of path strings to delete")] string paths)
        => ops.Delete(paths);

    [Description("Displays a node's visual layout in the chat UI.")]
    public string NavigateTo(
        [Description("Path to navigate to (e.g., @graph/org1)")] string path)
    {
        logger.LogInformation("NavigateTo called with path={Path}", path);

        var resolvedPath = MeshOperations.ResolvePath(ResolveContextPath(path));
        var address = new Address(resolvedPath);
        var layoutControl = Controls.LayoutArea(address, string.Empty);

        chat.DisplayLayoutArea(layoutControl);
        return $"Navigating to: {resolvedPath}";
    }

    /// <summary>
    /// Resolves a path relative to the current chat context.
    /// Absolute paths (starting with @/ or /) are returned as-is.
    /// Relative paths (e.g., @content:file.docx, @MyChild) are prepended with context path.
    /// </summary>
    private string ResolveContextPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        var raw = path.StartsWith("@") ? path[1..] : path;

        // Absolute path — starts with /
        if (raw.StartsWith("/"))
            return "@" + raw[1..]; // strip the leading / and re-add @

        // Already looks absolute (contains a colon with address before it, like OrgA/content:file)
        // Check if this is a unified path with address prefix
        var colonIndex = raw.IndexOf(':');
        if (colonIndex > 0)
        {
            var beforeColon = raw[..colonIndex];
            // If there's a slash before the colon, it has an address prefix already
            if (beforeColon.Contains('/'))
                return path; // already absolute
        }
        else if (raw.Contains('/'))
        {
            // No colon, has slashes — could be a multi-segment path like "OrgA/Doc"
            // If it has 2+ segments, likely absolute already
            return path;
        }

        // Relative path — prepend context
        var contextPath = chat.Context?.Context;
        if (string.IsNullOrEmpty(contextPath))
            return path; // no context, return as-is

        // For unified refs like "content:file.docx", prepend context as address
        if (colonIndex > 0)
            return $"@{contextPath}/{raw}";

        // For simple names like "MyChild", prepend context
        return $"@{contextPath}/{raw}";
    }

    /// <summary>
    /// Creates the standard tools for this plugin (read-only operations).
    /// </summary>
    public IList<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(Get),
            AIFunctionFactory.Create(Search),
            AIFunctionFactory.Create(NavigateTo),
        ];
    }

    /// <summary>
    /// Creates all tools including write operations (Create, Update, Delete).
    /// </summary>
    public IList<AITool> CreateAllTools()
    {
        return
        [
            AIFunctionFactory.Create(Get),
            AIFunctionFactory.Create(Search),
            AIFunctionFactory.Create(NavigateTo),
            AIFunctionFactory.Create(Create),
            AIFunctionFactory.Create(Update),
            AIFunctionFactory.Create(Patch),
            AIFunctionFactory.Create(Delete),
        ];
    }
}
