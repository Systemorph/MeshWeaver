using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Extension methods for configuring per-node-type access rules in the hub configuration.
/// Rules are stored on MessageHubConfiguration and extracted by NodeTypeService
/// to create INodeTypeAccessRule instances consumed by RlsNodeValidator.
/// </summary>
public static class NodeAccessExtensions
{
    /// <summary>
    /// Adds an access rule function for specific operations.
    /// Returns true to allow, false to fall through to next rule.
    /// </summary>
    public static MessageHubConfiguration AddAccessRule(
        this MessageHubConfiguration config,
        IReadOnlyCollection<NodeOperation> operations,
        Func<NodeValidationContext, string?, bool> rule)
    {
        var existing = config.Get<NodeAccessRuleSet>() ?? new();
        return config.Set(existing.Add(operations, rule));
    }

    /// <summary>
    /// Grants read access to all authenticated users.
    /// Registers both a node-level access rule (for row-level security)
    /// and a hub-level permission rule (for AccessControlPipeline).
    /// </summary>
    public static MessageHubConfiguration WithPublicRead(this MessageHubConfiguration config)
        => config
            .AddAccessRule(
                [NodeOperation.Read],
                (_, userId) => !string.IsNullOrEmpty(userId))
            .AddHubPermissionRule(
                Permission.Read,
                (_, userId) => !string.IsNullOrEmpty(userId));

    /// <summary>
    /// Allows users to edit nodes under their own User/{userId} scope.
    /// </summary>
    public static MessageHubConfiguration WithSelfEdit(this MessageHubConfiguration config)
        => config.AddAccessRule(
            [NodeOperation.Update],
            (context, userId) =>
            {
                if (string.IsNullOrEmpty(userId)) return false;
                var nodePath = context.Node.Path;
                if (string.IsNullOrEmpty(nodePath)) return false;
                var userScopePath = $"User/{userId}";
                return nodePath.Equals(userScopePath, StringComparison.OrdinalIgnoreCase)
                       || nodePath.StartsWith(userScopePath + "/", StringComparison.OrdinalIgnoreCase);
            });

    /// <summary>
    /// Gets the access rule set from a hub configuration.
    /// </summary>
    internal static NodeAccessRuleSet? GetNodeAccessRuleSet(this MessageHubConfiguration config)
        => config.Get<NodeAccessRuleSet>();
}

/// <summary>
/// Collected access rule functions for a node type, stored on MessageHubConfiguration.
/// </summary>
public record NodeAccessRuleSet
{
    public IReadOnlyList<(IReadOnlyCollection<NodeOperation> Operations, Func<NodeValidationContext, string?, bool> Check)> Rules { get; init; } = [];

    public NodeAccessRuleSet Add(IReadOnlyCollection<NodeOperation> operations, Func<NodeValidationContext, string?, bool> rule)
        => this with { Rules = [.. Rules, (operations, rule)] };

    /// <summary>
    /// Creates an INodeTypeAccessRule from this rule set.
    /// </summary>
    public INodeTypeAccessRule ToAccessRule(string nodeType)
        => new FunctionalAccessRule(nodeType, this);

    private class FunctionalAccessRule(string nodeType, NodeAccessRuleSet ruleSet) : INodeTypeAccessRule
    {
        public string NodeType => nodeType;

        public IReadOnlyCollection<NodeOperation> SupportedOperations =>
            ruleSet.Rules.SelectMany(r => r.Operations).Distinct().ToArray();

        public Task<bool> HasAccessAsync(NodeValidationContext context, string? userId, CancellationToken ct = default)
        {
            foreach (var (operations, check) in ruleSet.Rules)
            {
                if (operations.Count == 0 || operations.Contains(context.Operation))
                {
                    if (check(context, userId))
                        return Task.FromResult(true);
                }
            }
            return Task.FromResult(false);
        }
    }
}
