using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.AI;

/// <summary>
/// Shared helper for querying and ordering agents by relevance to the current context.
/// This is the SINGLE implementation of agent finding and ordering logic.
/// </summary>
public static class AgentOrderingHelper
{
    /// <summary>
    /// Queries agents from the mesh and returns them as AgentDisplayInfo with paths.
    /// Searches NodeType namespace (children) and context path namespace (ancestors).
    /// </summary>
    public static async Task<IReadOnlyList<AgentDisplayInfo>> QueryAgentsAsync(
        IMeshQuery? meshQuery,
        string? contextPath,
        string? nodeTypePath)
    {
        var agentsDict = new Dictionary<string, (AgentConfiguration Config, string Path)>();

        // 1. Query agents from the NodeType namespace (higher priority)
        // Use hierarchy scope to find agents that are children of the NodeType path
        if (meshQuery != null && !string.IsNullOrEmpty(nodeTypePath))
        {
            try
            {
                var query = $"path:{nodeTypePath} nodeType:Agent scope:hierarchy";
                await foreach (var node in meshQuery.QueryAsync<MeshNode>(query))
                {
                    if (node.Content is AgentConfiguration config && !agentsDict.ContainsKey(config.Id))
                    {
                        agentsDict[config.Id] = (config, node.Path ?? "");
                    }
                }
            }
            catch
            {
                // Ignore query errors
            }
        }

        // 2. Query agents from the context path namespace (ancestors)
        if (meshQuery != null)
        {
            try
            {
                var query = string.IsNullOrEmpty(contextPath)
                    ? "nodeType:Agent scope:selfAndAncestors"
                    : $"path:{contextPath} nodeType:Agent scope:selfAndAncestors";

                await foreach (var node in meshQuery.QueryAsync<MeshNode>(query))
                {
                    if (node.Content is AgentConfiguration config && !agentsDict.ContainsKey(config.Id))
                    {
                        agentsDict[config.Id] = (config, node.Path ?? "");
                    }
                }
            }
            catch
            {
                // Ignore query errors
            }
        }

        // Build display info list
        return agentsDict.Values
            .Select(x => new AgentDisplayInfo
            {
                Name = x.Config.Id,
                Path = x.Path,
                Description = x.Config.Description ?? x.Config.DisplayName ?? x.Config.Id,
                GroupName = x.Config.GroupName,
                DisplayOrder = x.Config.DisplayOrder,
                IndentLevel = 0,
                Icon = x.Config.Icon,
                CustomIconSvg = x.Config.CustomIconSvg,
                AgentConfiguration = x.Config
            })
            .ToList();
    }

    /// <summary>
    /// Gets the NodeType for a given context path.
    /// </summary>
    public static async Task<string?> GetNodeTypeAsync(IMeshQuery? meshQuery, string? contextPath)
    {
        if (meshQuery == null || string.IsNullOrEmpty(contextPath))
            return null;

        try
        {
            await foreach (var node in meshQuery.QueryAsync<MeshNode>($"path:{contextPath} scope:self"))
            {
                if (!string.IsNullOrEmpty(node.NodeType) && node.NodeType != "Agent" && node.NodeType != "Markdown")
                {
                    return node.NodeType;
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }

    /// <summary>
    /// Orders agents by relevance to the current context.
    /// Priority order:
    /// 1) Agents in the namespace matching the current path (highest) → 1000
    /// 2) Agents in the namespace matching the NodeType → 500
    /// 3) Agents in ancestor paths of context → 200 - hops
    /// 4) Agents in ancestor paths of NodeType → 100 - hops
    /// </summary>
    public static IReadOnlyList<AgentDisplayInfo> OrderByRelevance(
        IEnumerable<AgentDisplayInfo> agents,
        string? contextPath,
        string? nodeTypePath)
    {
        var normalizedContextPath = (contextPath ?? "").TrimStart('/');
        var normalizedNodeTypePath = (nodeTypePath ?? "").TrimStart('/');

        return agents
            .OrderByDescending(a => CalculatePathRelevance(a.Path, normalizedContextPath, normalizedNodeTypePath))
            .ThenBy(a => a.DisplayOrder)
            .ThenBy(a => a.Name)
            .ToList();
    }

    /// <summary>
    /// Calculates how relevant an agent's path is to the current context.
    /// Higher score = more relevant (closer match).
    /// </summary>
    public static int CalculatePathRelevance(string? agentPath, string contextPath, string nodeTypePath)
    {
        if (string.IsNullOrEmpty(agentPath))
            return 0;

        var normalizedAgentPath = agentPath.TrimStart('/');

        // Get the parent path of the agent (remove the agent name itself)
        var lastSlash = normalizedAgentPath.LastIndexOf('/');
        var agentNamespace = lastSlash > 0 ? normalizedAgentPath[..lastSlash] : "";

        // 1) Exact namespace match to context path (highest priority)
        if (!string.IsNullOrEmpty(contextPath) &&
            agentNamespace.Equals(contextPath, StringComparison.OrdinalIgnoreCase))
        {
            return 1000;
        }

        // 2) Exact namespace match to NodeType (second priority)
        if (!string.IsNullOrEmpty(nodeTypePath) &&
            agentNamespace.Equals(nodeTypePath, StringComparison.OrdinalIgnoreCase))
        {
            return 500;
        }

        // 3) Agent namespace is an ancestor of context path
        if (!string.IsNullOrEmpty(contextPath) && IsAncestorOf(agentNamespace, contextPath))
        {
            var hops = GetHopCount(agentNamespace, contextPath);
            return 200 - hops; // Base 200 for path ancestors, -1 per hop
        }

        // 4) Agent namespace is an ancestor of NodeType path
        if (!string.IsNullOrEmpty(nodeTypePath) && IsAncestorOf(agentNamespace, nodeTypePath))
        {
            var hops = GetHopCount(agentNamespace, nodeTypePath);
            return 100 - hops; // Base 100 for NodeType ancestors, -1 per hop
        }

        // Agent path doesn't match context or NodeType hierarchy
        return 0;
    }

    /// <summary>
    /// Checks if ancestorPath is an ancestor of descendantPath.
    /// Empty string (root) is an ancestor of all non-empty paths.
    /// </summary>
    private static bool IsAncestorOf(string ancestorPath, string descendantPath)
    {
        if (string.IsNullOrEmpty(descendantPath))
            return false;

        if (string.IsNullOrEmpty(ancestorPath))
            return true; // Root is ancestor of everything

        return descendantPath.StartsWith(ancestorPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the number of path segments between ancestor and descendant.
    /// </summary>
    private static int GetHopCount(string ancestorPath, string descendantPath)
    {
        var descendantDepth = descendantPath.Split('/').Length;
        var ancestorDepth = string.IsNullOrEmpty(ancestorPath) ? 0 : ancestorPath.Split('/').Length;
        return descendantDepth - ancestorDepth;
    }
}
