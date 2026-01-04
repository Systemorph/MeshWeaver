using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;

namespace MeshWeaver.AI;

/// <summary>
/// AI agent for navigating and managing the mesh graph structure.
/// Provides CRUD operations on nodes, navigation capabilities, and comment management.
/// Uses @ prefix shorthand for node paths (e.g., @graph/org1 instead of graph/org1).
/// </summary>
[ExposedInDefaultAgent]
public class MeshAgent(IMessageHub hub) : IAgentDefinition, IAgentWithTools, IAgentWithContext
{
    public string Name => "MeshAgent";
    public string? GroupName => "Graph";
    public int DisplayOrder => 0;
    public string? Icon => "Organization";

    public string Description =>
        "Manages the mesh graph structure. Can read, create, update, and delete nodes. " +
        "Navigate the hierarchy, organize content, and manage comments. " +
        "Use @ prefix for node paths (e.g., @graph/org1).";

    public string Instructions => """
        You are the MeshAgent, specialized in managing the mesh graph structure.

        ## Capabilities
        - **Navigate** the node hierarchy (organizations, projects, etc.)
        - **Read** node details and list children
        - **Create** new nodes at any level
        - **Update** existing node properties and descriptions
        - **Delete** nodes (optionally with children)
        - **Search** for nodes by name or description
        - **Comments** - add, view, and delete comments on nodes

        ## Path Conventions
        Use the @ prefix as shorthand for paths:
        - @graph - Root of the graph
        - @graph/org1 - An organization
        - @graph/org1/project1 - A project under an organization
        - @graph/org1/project1/story1 - A story under a project

        The @ prefix is optional but recommended for clarity.

        ## Best Practices
        1. Always confirm destructive operations (delete) with the user before proceeding
        2. Use search to find nodes when the exact path is unknown
        3. List children to understand the hierarchy before navigating
        4. Provide clear feedback after each operation
        5. When creating nodes, suggest appropriate node types based on context

        ## Node Types
        Common node types in the hierarchy:
        - Organization (org) - Top-level grouping
        - Project - Work containers under organizations
        - Story - Work items under projects
        - Custom types can be defined by the user

        ## Descriptions
        Node descriptions support Markdown formatting:
        - Use headers for structure
        - Use bullet points for lists
        - Use code blocks for technical content
        - Use links for references

        ## Comments
        Comments are threaded discussions on nodes:
        - Use GetComments to view existing comments
        - Use AddComment to add new comments
        - Use DeleteComment to remove comments (requires comment ID)
        """;

    public IEnumerable<AITool> GetTools(IAgentChat chat)
    {
        var plugin = new MeshPlugin(hub, chat);
        return plugin.CreateTools();
    }

    public bool Matches(AgentContext? context)
    {
        if (context?.Address == null)
            return false;

        var addressStr = context.Address.ToString();

        // Match graph-related addresses
        return addressStr.StartsWith("graph", StringComparison.OrdinalIgnoreCase) ||
               addressStr.Contains("/graph/", StringComparison.OrdinalIgnoreCase) ||
               addressStr.Contains("/_Nodes", StringComparison.OrdinalIgnoreCase);
    }
}
