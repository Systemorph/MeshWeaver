using System.Text.Json;
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
        JsonSerializerOptions jsonOptions,
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
                await foreach (var node in meshQuery.QueryAsync<MeshNode>(query, jsonOptions))
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

                await foreach (var node in meshQuery.QueryAsync<MeshNode>(query, jsonOptions))
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
    public static async Task<string?> GetNodeTypeAsync(IMeshQuery? meshQuery, JsonSerializerOptions jsonOptions, string? contextPath)
    {
        if (meshQuery == null || string.IsNullOrEmpty(contextPath))
            return null;

        try
        {
            await foreach (var node in meshQuery.QueryAsync<MeshNode>($"path:{contextPath} scope:self", jsonOptions))
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
    /// Orders agents by DisplayOrder then by DisplayName.
    /// </summary>
    public static IReadOnlyList<AgentDisplayInfo> OrderByRelevance(
        IEnumerable<AgentDisplayInfo> agents,
        string? contextPath,
        string? nodeTypePath)
    {
        return agents
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.AgentConfiguration.DisplayName ?? a.Name)
            .ToList();
    }
}
