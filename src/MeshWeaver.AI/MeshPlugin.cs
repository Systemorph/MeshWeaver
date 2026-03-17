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
    private readonly ILogger<MeshPlugin> logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshPlugin>>();

    [Description("Retrieves a node or content from the mesh by path. Supports Unified Path prefixes (schema:, model:, data:, content:, collection:, area:, layoutAreas:).")]
    public Task<string> Get(
        [Description("Path to data (e.g., @graph/org1, @NodeType/*, @ACME/schema:, @ACME/model:)")] string path)
        => ops.Get(path);

    [Description("Searches the mesh using GitHub-style query syntax.")]
    public Task<string> Search(
        [Description("Query string (e.g., 'nodeType:Agent', 'path:ACME scope:descendants', 'name:*sales*')")] string query,
        [Description("Base path to search from (e.g., @graph). Empty for all.")] string? basePath = null)
        => ops.Search(query, basePath);

    [Description("Creates a new node in the mesh.")]
    public Task<string> Create(
        [Description("JSON MeshNode object to create")] string node)
        => ops.Create(node);

    [Description("Updates existing nodes in the mesh. Pass a JSON array of MeshNode objects.")]
    public Task<string> Update(
        [Description("JSON array of MeshNode objects with updated fields")] string nodes)
        => ops.Update(nodes);

    [Description("Deletes nodes from the mesh by path.")]
    public Task<string> Delete(
        [Description("JSON array of path strings to delete")] string paths)
        => ops.Delete(paths);

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
            AIFunctionFactory.Create(Delete),
        ];
    }
}
