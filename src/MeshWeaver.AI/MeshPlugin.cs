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

    [Description("Retrieves a node or content from the mesh by path. Paths are relative to current context; use @/ prefix for absolute paths. Supports Unified Path prefixes: content/, data/, schema/, model/, collection/, area/.")]
    public Task<string> Get(
        [Description("Path to data. Relative: @content/file.docx, @MyChild/*. Absolute: @/OrgA/Doc, @/OrgA/content/file.docx. For spaces: \"@content/My File.docx\"")] string path)
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

    [Description("Full replacement update of existing nodes. ALWAYS Get the node first, modify the returned object, then send it back here unchanged-except-for-edits. The 'content' field MUST be present and non-null — null content is rejected and the response will include the expected schema. Prefer Patch for small changes.")]
    public Task<string> Update(
        [Description("JSON array of complete MeshNode objects fetched via Get and then modified")] string nodes)
        => ops.Update(nodes);

    [Description("Partial update of a single node. Only the keys present in 'fields' are changed; omitted keys preserve existing values. Do NOT include 'content' unless you intend to overwrite it — and never set 'content' to null (will be rejected with the schema). Prefer this over Update for small edits like icon/name/category.")]
    public Task<string> Patch(
        [Description("Path to the node (e.g., @User/rbuergi/my-node)")] string path,
        [Description("JSON object with ONLY the fields to change. Examples: {\"icon\": \"<svg>...</svg>\"}, {\"name\": \"New Name\"}. Include 'content' only if overwriting — and never as null.")] string fields)
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

    private string ResolveContextPath(string path) => MeshOperations.ResolveContextPath(chat, path);

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
