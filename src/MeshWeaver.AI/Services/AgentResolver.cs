using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Services;

/// <summary>
/// Resolves agent configurations from the graph with hierarchical lookup.
/// Agents are stored as MeshNodes with nodeType="Agent" and Content=AgentConfiguration.
/// </summary>
public class AgentResolver : IAgentResolver
{
    private readonly IPersistenceService _persistence;
    private readonly ILogger<AgentResolver> _logger;

    /// <summary>
    /// The NodeType value used to identify agent nodes.
    /// </summary>
    public const string AgentNodeType = "Agent";

    public AgentResolver(IPersistenceService persistence, ILogger<AgentResolver> logger)
    {
        _persistence = persistence;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentConfiguration>> GetAgentsForContextAsync(
        string? contextPath,
        CancellationToken ct = default)
    {
        var agents = new Dictionary<string, (AgentConfiguration Config, int Depth)>();
        var segments = string.IsNullOrEmpty(contextPath)
            ? Array.Empty<string>()
            : contextPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Search from most specific namespace to root
        // At each level, find agents (MeshNodes with nodeType="Agent")
        for (int depth = segments.Length; depth >= 0; depth--)
        {
            var namespacePath = depth == 0 ? "" : string.Join("/", segments.Take(depth));

            try
            {
                await foreach (var node in _persistence.GetChildrenAsync(namespacePath).WithCancellation(ct))
                {
                    if (!IsAgentNode(node))
                        continue;

                    var config = ExtractAgentConfiguration(node, namespacePath);
                    if (config != null && !agents.ContainsKey(config.Id))
                    {
                        // First found wins (most specific namespace)
                        agents[config.Id] = (config, depth);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error loading agents from namespace {Namespace}", namespacePath);
            }
        }

        return agents.Values
            .Select(x => x.Config)
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.Id)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<AgentConfiguration?> GetAgentAsync(
        string agentPath,
        string? contextPath = null,
        CancellationToken ct = default)
    {
        // Resolve the path
        var resolvedPath = ResolvePath(agentPath, contextPath);

        try
        {
            var node = await _persistence.GetNodeAsync(resolvedPath, ct);
            if (node == null || !IsAgentNode(node))
            {
                // If not found at exact path, try hierarchical lookup by agent Id
                var agentId = agentPath.Split('/').Last();
                var agents = await GetAgentsForContextAsync(contextPath, ct);
                return agents.FirstOrDefault(a => a.Id == agentId);
            }

            return ExtractAgentConfiguration(node, node.Namespace);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting agent at path {Path}", resolvedPath);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<AgentConfiguration?> GetDefaultAgentAsync(
        string? contextPath = null,
        CancellationToken ct = default)
    {
        var agents = await GetAgentsForContextAsync(contextPath, ct);
        return agents.FirstOrDefault(a => a.IsDefault);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentConfiguration>> GetExposedAgentsAsync(
        string? contextPath = null,
        CancellationToken ct = default)
    {
        var agents = await GetAgentsForContextAsync(contextPath, ct);
        return agents.Where(a => a.ExposedInNavigator).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentConfiguration>> FindMatchingAgentsAsync(
        AgentContext context,
        string? contextPath = null,
        CancellationToken ct = default)
    {
        var agents = await GetAgentsForContextAsync(contextPath, ct);
        var matching = new List<AgentConfiguration>();

        foreach (var agent in agents)
        {
            if (string.IsNullOrEmpty(agent.ContextMatchPattern))
                continue;

            if (MatchesContext(agent.ContextMatchPattern, context))
            {
                matching.Add(agent);
            }
        }

        return matching;
    }

    /// <summary>
    /// Checks if a MeshNode represents an agent.
    /// </summary>
    private static bool IsAgentNode(MeshNode node)
    {
        return string.Equals(node.NodeType, AgentNodeType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts AgentConfiguration from a MeshNode.
    /// </summary>
    private AgentConfiguration? ExtractAgentConfiguration(MeshNode node, string? ns)
    {
        if (node.Content is AgentConfiguration config)
        {
            return config;
        }

        // Try to deserialize from Content if it's not already the right type
        // This handles JSON deserialization cases
        if (node.Content is System.Text.Json.JsonElement jsonElement)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<AgentConfiguration>(
                    jsonElement.GetRawText());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize AgentConfiguration from node {Path}", node.Path);
            }
        }

        // Fallback: create configuration from node properties
        return new AgentConfiguration
        {
            Id = node.Id,
            DisplayName = node.Name,
            Description = node.Description,
            IconName = node.IconName,
            DisplayOrder = node.DisplayOrder
        };
    }

    /// <summary>
    /// Resolves an agent path that may be relative or absolute.
    /// </summary>
    private static string ResolvePath(string agentPath, string? contextPath)
    {
        // Absolute path
        if (agentPath.StartsWith("/"))
            return agentPath.TrimStart('/');

        // Already a full path (contains /)
        if (agentPath.Contains('/'))
            return agentPath;

        // Relative path - prepend context
        if (string.IsNullOrEmpty(contextPath))
            return agentPath;

        return $"{contextPath}/{agentPath}";
    }

    /// <summary>
    /// Evaluates an RSQL-like context match pattern against the current context.
    /// Supports simple patterns like:
    /// - "address.type==pricing" - exact match on address type
    /// - "address.path=like=*Todo*" - wildcard match
    /// </summary>
    private static bool MatchesContext(string pattern, AgentContext context)
    {
        if (context.Address == null)
            return false;

        var addressStr = context.Address.ToString();

        // Simple pattern matching
        // Pattern: "field==value" or "field=like=*pattern*"
        if (pattern.Contains("=like="))
        {
            var parts = pattern.Split("=like=");
            if (parts.Length == 2)
            {
                var field = parts[0].Trim();
                var value = parts[1].Trim().Trim('*');

                var fieldValue = GetFieldValue(field, context);
                return fieldValue?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
            }
        }
        else if (pattern.Contains("=="))
        {
            var parts = pattern.Split("==");
            if (parts.Length == 2)
            {
                var field = parts[0].Trim();
                var value = parts[1].Trim();

                var fieldValue = GetFieldValue(field, context);
                return string.Equals(fieldValue, value, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Fallback: simple contains check on the full address
        return addressStr.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets a field value from the context for pattern matching.
    /// </summary>
    private static string? GetFieldValue(string field, AgentContext context)
    {
        return field.ToLowerInvariant() switch
        {
            "address" or "address.path" => context.Address?.ToString(),
            "address.type" => context.Address?.Type,
            "address.id" => context.Address?.Id,
            _ => context.Address?.ToString()
        };
    }
}
