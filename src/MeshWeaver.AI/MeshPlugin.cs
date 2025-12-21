using System.ComponentModel;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Plugin providing mesh node operations for AI agents.
/// Supports @ prefix shorthand for unified references (e.g., @graph/org1 -> graph/org1).
/// </summary>
public class MeshPlugin(IMessageHub hub, IAgentChat chat)
{
    private readonly ILogger<MeshPlugin> logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshPlugin>>();
    private readonly IMeshCatalog? meshCatalog = hub.ServiceProvider.GetService<IMeshCatalog>();

    /// <summary>
    /// Resolves @ prefix to full path. Example: @graph/org1 -> graph/org1
    /// </summary>
    private static string ResolvePath(string path)
    {
        if (path.StartsWith("@"))
            return path[1..];
        return path;
    }

    [Description("Gets a node by its path. Use @ prefix for shorthand (e.g., @graph/org1).")]
    public async Task<string> GetNode(
        [Description("Path to the node (e.g., @graph/org1 or graph/org1)")] string path)
    {
        logger.LogInformation("GetNode called with path={Path}", path);

        if (meshCatalog?.Persistence == null)
            return "Mesh catalog not available.";

        var resolvedPath = ResolvePath(path);
        var node = await meshCatalog.Persistence.GetNodeAsync(resolvedPath);

        if (node == null)
            return $"Node not found at path: {resolvedPath}";

        return JsonSerializer.Serialize(node, hub.JsonSerializerOptions);
    }

    [Description("Lists child nodes under a parent path. Use @ prefix for shorthand.")]
    public async Task<string> GetChildren(
        [Description("Parent path (e.g., @graph or graph). Empty for root.")] string? parentPath = null)
    {
        logger.LogInformation("GetChildren called with parentPath={ParentPath}", parentPath);

        if (meshCatalog?.Persistence == null)
            return "Mesh catalog not available.";

        var resolvedPath = parentPath != null ? ResolvePath(parentPath) : null;
        var result = new List<object>();
        await foreach (var n in meshCatalog.Persistence.GetChildrenAsync(resolvedPath))
        {
            result.Add(new
            {
                n.Prefix,
                n.Name,
                n.NodeType,
                n.Description
            });
        }

        return JsonSerializer.Serialize(result, hub.JsonSerializerOptions);
    }

    [Description("Lists all available node types that can be used when creating nodes. " +
                 "Each node type has an associated data schema that defines the Content structure.")]
    public string GetNodeTypes()
    {
        logger.LogInformation("GetNodeTypes called");

        if (meshCatalog == null)
            return "Mesh catalog not available.";

        var nodeTypes = meshCatalog.GetNodeTypes();

        return JsonSerializer.Serialize(nodeTypes, hub.JsonSerializerOptions);
    }

    [Description("Gets the JSON schema for a node type's Content structure. " +
                 "Use this to understand what fields are required when creating or updating nodes of this type.")]
    public async Task<string> GetSchema(
        [Description("The node type to get the schema for (from GetNodeTypes)")] string nodeType)
    {
        logger.LogInformation("GetSchema called with nodeType={NodeType}", nodeType);

        if (meshCatalog == null)
            return "Mesh catalog not available.";

        var config = meshCatalog.GetNodeTypeConfiguration(nodeType);
        if (config == null)
        {
            var availableTypes = string.Join(", ", meshCatalog.GetNodeTypes().Select(t => t.NodeType));
            return $"Unknown node type: {nodeType}. Available types: {availableTypes}";
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await hub.AwaitResponse(
                new GetSchemaRequest(config.DataType.Name),
                o => o.WithTarget(hub.Address),
                cts.Token);
            return response.Message.Schema;
        }
        catch (OperationCanceledException)
        {
            return "Operation was cancelled and could not be completed.";
        }
        catch (Exception ex)
        {
            return $"Error getting schema for type {nodeType}: {ex.Message}";
        }
    }

    [Description("Creates a new node at the specified path. " +
                 "Use GetNodeTypes to see available types and GetSchema to get the Content structure for a type.")]
    public async Task<string> CreateNode(
        [Description("Full path for the new node (e.g., @graph/neworg)")] string path,
        [Description("Display name for the node")] string name,
        [Description("Type of the node - must be a valid type from GetNodeTypes")] string nodeType,
        [Description("JSON content matching the schema for the node type (from GetSchema). " +
                     "If not provided, a default content object will be created.")] string? contentJson = null,
        [Description("Description of the node (markdown supported)")] string? description = null)
    {
        logger.LogInformation("CreateNode called with path={Path}, name={Name}, nodeType={NodeType}", path, name, nodeType);

        if (meshCatalog?.Persistence == null)
            return "Mesh catalog not available.";

        var resolvedPath = ResolvePath(path);

        // Validate node type exists
        var config = meshCatalog.GetNodeTypeConfiguration(nodeType);
        if (config == null)
        {
            var availableTypes = string.Join(", ", meshCatalog.GetNodeTypes().Select(t => t.NodeType));
            return $"Invalid node type: {nodeType}. Available types: {availableTypes}. " +
                   $"Use GetNodeTypes to see available types and GetSchema to get the Content structure.";
        }

        // Check if node already exists
        if (await meshCatalog.Persistence.ExistsAsync(resolvedPath))
            return $"Node already exists at path: {resolvedPath}";

        // Parse and validate content if provided
        object? content = null;
        if (!string.IsNullOrWhiteSpace(contentJson))
        {
            try
            {
                content = JsonSerializer.Deserialize(contentJson, config.DataType, hub.JsonSerializerOptions);
                if (content == null)
                {
                    return $"Failed to parse content JSON. Please ensure it matches the schema for type '{nodeType}'. " +
                           $"Use GetSchema(\"{nodeType}\") to see the required structure.";
                }
            }
            catch (JsonException ex)
            {
                return $"Invalid JSON content: {ex.Message}. " +
                       $"Please ensure the content matches the schema for type '{nodeType}'. " +
                       $"Use GetSchema(\"{nodeType}\") to see the required structure.";
            }
        }
        else
        {
            // Create default content with minimal required fields
            content = new NodeDescription
            {
                Id = resolvedPath,
                Description = description ?? string.Empty
            };
        }

        var node = new MeshNode(resolvedPath)
        {
            Name = name,
            Description = description,
            NodeType = nodeType,
            Content = content
        };

        await meshCatalog.Persistence.SaveNodeAsync(node);
        return $"Node created successfully at: {resolvedPath} with type: {nodeType}";
    }

    [Description("Updates an existing node at the specified path.")]
    public async Task<string> UpdateNode(
        [Description("Path to the node to update (e.g., @graph/org1)")] string path,
        [Description("New display name (optional)")] string? name = null,
        [Description("New description (optional, markdown supported)")] string? description = null,
        [Description("New node type (optional)")] string? nodeType = null)
    {
        logger.LogInformation("UpdateNode called with path={Path}", path);

        if (meshCatalog?.Persistence == null)
            return "Mesh catalog not available.";

        var resolvedPath = ResolvePath(path);
        var existingNode = await meshCatalog.Persistence.GetNodeAsync(resolvedPath);

        if (existingNode == null)
            return $"Node not found at path: {resolvedPath}";

        var updatedNode = existingNode with
        {
            Name = name ?? existingNode.Name,
            Description = description ?? existingNode.Description,
            NodeType = nodeType ?? existingNode.NodeType,
            Content = description != null
                ? new NodeDescription { Id = resolvedPath, Description = description }
                : existingNode.Content
        };

        await meshCatalog.Persistence.SaveNodeAsync(updatedNode);
        return $"Node updated successfully at: {resolvedPath}";
    }

    [Description("Deletes a node at the specified path.")]
    public async Task<string> DeleteNode(
        [Description("Path to the node to delete (e.g., @graph/org1)")] string path,
        [Description("If true, also deletes all child nodes recursively")] bool recursive = false)
    {
        logger.LogInformation("DeleteNode called with path={Path}, recursive={Recursive}", path, recursive);

        if (meshCatalog?.Persistence == null)
            return "Mesh catalog not available.";

        var resolvedPath = ResolvePath(path);

        if (!await meshCatalog.Persistence.ExistsAsync(resolvedPath))
            return $"Node not found at path: {resolvedPath}";

        await meshCatalog.Persistence.DeleteNodeAsync(resolvedPath, recursive);
        return $"Node deleted successfully at: {resolvedPath}" + (recursive ? " (including all children)" : "");
    }

    private const string NodesArea = "_Nodes";

    [Description("Navigates to a node in the UI. Displays the node's tabbed view.")]
    public string NavigateTo(
        [Description("Path to navigate to (e.g., @graph/org1)")] string path)
    {
        logger.LogInformation("NavigateTo called with path={Path}", path);

        var resolvedPath = ResolvePath(path);
        var address = new Address(resolvedPath);
        var layoutControl = Controls.LayoutArea(address, NodesArea);

        chat.DisplayLayoutArea(layoutControl);
        return $"Navigating to: {resolvedPath}";
    }

    [Description("Searches for nodes matching a query.")]
    public async Task<string> SearchNodes(
        [Description("Search query to match against name, description, or content")] string query,
        [Description("Optional parent path to search under (e.g., @graph)")] string? parentPath = null)
    {
        logger.LogInformation("SearchNodes called with query={Query}, parentPath={ParentPath}", query, parentPath);

        if (meshCatalog?.Persistence == null)
            return "Mesh catalog not available.";

        var resolvedParent = parentPath != null ? ResolvePath(parentPath) : null;
        var result = new List<object>();
        await foreach (var n in meshCatalog.Persistence.SearchAsync(resolvedParent, query))
        {
            result.Add(new
            {
                n.Prefix,
                n.Name,
                n.NodeType,
                n.Description
            });
        }

        return JsonSerializer.Serialize(result, hub.JsonSerializerOptions);
    }

    #region Comments

    [Description("Gets all comments for a node.")]
    public async Task<string> GetComments(
        [Description("Path to the node (e.g., @graph/org1)")] string path)
    {
        logger.LogInformation("GetComments called with path={Path}", path);

        if (meshCatalog?.Persistence == null)
            return "Mesh catalog not available.";

        var resolvedPath = ResolvePath(path);
        var comments = new List<Comment>();
        await foreach (var comment in meshCatalog.Persistence.GetCommentsAsync(resolvedPath))
            comments.Add(comment);

        return JsonSerializer.Serialize(comments, hub.JsonSerializerOptions);
    }

    [Description("Adds a comment to a node.")]
    public async Task<string> AddComment(
        [Description("Path to the node (e.g., @graph/org1)")] string path,
        [Description("Comment text (markdown supported)")] string text,
        [Description("Author name (optional)")] string? author = null)
    {
        logger.LogInformation("AddComment called with path={Path}", path);

        if (meshCatalog?.Persistence == null)
            return "Mesh catalog not available.";

        var resolvedPath = ResolvePath(path);

        var comment = new Comment
        {
            NodePath = resolvedPath,
            Author = author ?? "AI Agent",
            Text = text,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var saved = await meshCatalog.Persistence.AddCommentAsync(comment);
        return $"Comment added successfully with ID: {saved.Id}";
    }

    [Description("Deletes a comment by its ID.")]
    public async Task<string> DeleteComment(
        [Description("The comment ID to delete")] string commentId)
    {
        logger.LogInformation("DeleteComment called with commentId={CommentId}", commentId);

        if (meshCatalog?.Persistence == null)
            return "Mesh catalog not available.";

        await meshCatalog.Persistence.DeleteCommentAsync(commentId);
        return $"Comment deleted successfully: {commentId}";
    }

    #endregion

    /// <summary>
    /// Creates all tools for this plugin.
    /// </summary>
    public IList<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(GetNode),
            AIFunctionFactory.Create(GetChildren),
            AIFunctionFactory.Create(GetNodeTypes),
            AIFunctionFactory.Create(GetSchema),
            AIFunctionFactory.Create(CreateNode),
            AIFunctionFactory.Create(UpdateNode),
            AIFunctionFactory.Create(DeleteNode),
            AIFunctionFactory.Create(NavigateTo),
            AIFunctionFactory.Create(SearchNodes),
            AIFunctionFactory.Create(GetComments),
            AIFunctionFactory.Create(AddComment),
            AIFunctionFactory.Create(DeleteComment)
        ];
    }
}
