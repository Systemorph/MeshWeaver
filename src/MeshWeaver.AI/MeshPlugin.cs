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
/// </summary>
public class MeshPlugin(IMessageHub hub, IAgentChat chat)
{
    private readonly MeshOperations ops = new(hub);
    private readonly AccessService? accessService = hub.ServiceProvider.GetService<AccessService>();
    private readonly ILogger<MeshPlugin> logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshPlugin>>();

    /// <summary>
    /// Restores the user's access context before each tool call.
    /// AsyncLocal doesn't flow through the AI framework's async streaming + tool invocation,
    /// so we must explicitly set it from the captured ThreadExecutionContext.
    /// No disposal needed — we're inside the thread's InvokeAsync async flow.
    /// </summary>
    private void RestoreUserContext()
    {
        var userCtx = chat.ExecutionContext?.UserAccessContext;
        if (userCtx != null)
            accessService?.SetContext(userCtx);
    }

    [Description("Retrieves a node or content from the mesh by path. Supports Unified Path prefixes (schema:, model:, data:, content:, collection:, area:, layoutAreas:).")]
    public Task<string> Get(
        [Description("Path to data (e.g., @graph/org1, @NodeType/*, @ACME/schema:, @ACME/model:)")] string path)
    {
        RestoreUserContext();
        return ops.Get(path);
    }

    [Description("Searches the mesh using GitHub-style query syntax.")]
    public Task<string> Search(
        [Description("Query string (e.g., 'nodeType:Agent', 'path:ACME scope:descendants', 'name:*sales*')")] string query,
        [Description("Base path to search from (e.g., @graph). Empty for all.")] string? basePath = null)
    {
        RestoreUserContext();
        return ops.Search(query, basePath);
    }

    [Description("Creates a new node in the mesh. ALWAYS set the 'name' property to a human-readable display name.")]
    public Task<string> Create(
        [Description("JSON MeshNode with required: id, name, nodeType, namespace. Example: {\"id\":\"my-page\",\"namespace\":\"MyOrg\",\"name\":\"My Page\",\"nodeType\":\"Markdown\"}")] string node)
    {
        RestoreUserContext();
        return ops.Create(node);
    }

    [Description("Full replacement update of existing nodes. Pass a JSON array of complete MeshNode objects (from Get). WARNING: all fields are replaced — missing fields become null.")]
    public Task<string> Update(
        [Description("JSON array of complete MeshNode objects")] string nodes)
    {
        RestoreUserContext();
        return ops.Update(nodes);
    }

    [Description("Partial update of a single node. Only the specified fields are changed; all other fields are preserved. Use this for simple changes like updating icon, name, or content without needing to Get the full node first.")]
    public Task<string> Patch(
        [Description("Path to the node (e.g., @User/rbuergi/my-node)")] string path,
        [Description("JSON object with only the fields to update (e.g., {\"icon\": \"<svg>...</svg>\"} or {\"name\": \"New Name\", \"content\": {...}})")] string fields)
    {
        RestoreUserContext();
        return ops.Patch(path, fields);
    }

    [Description("Deletes nodes from the mesh by path.")]
    public Task<string> Delete(
        [Description("JSON array of path strings to delete")] string paths)
    {
        RestoreUserContext();
        return ops.Delete(paths);
    }

    [Description("Displays a node's visual layout in the chat UI.")]
    public string NavigateTo(
        [Description("Path to navigate to (e.g., @graph/org1)")] string path)
    {
        logger.LogInformation("NavigateTo called with path={Path}", path);

        var resolvedPath = MeshOperations.ResolvePath(path);
        var address = new Address(resolvedPath);
        var layoutControl = Controls.LayoutArea(address, string.Empty);

        chat.DisplayLayoutArea(layoutControl);
        return $"Navigating to: {resolvedPath}";
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
