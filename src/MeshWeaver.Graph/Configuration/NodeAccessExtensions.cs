using System.Reactive.Linq;
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
    /// Allows users to edit nodes under their own partition (path = userId or userId/...).
    /// Post-v10: each user has their own root-level partition ({userId}). The legacy
    /// "User/{userId}" prefix is still honoured for in-flight transitional data.
    /// </summary>
    public static MessageHubConfiguration WithSelfEdit(this MessageHubConfiguration config)
        => config.AddAccessRule(
            [NodeOperation.Update],
            (context, userId) =>
            {
                if (string.IsNullOrEmpty(userId)) return false;
                var nodePath = context.Node.Path;
                if (string.IsNullOrEmpty(nodePath)) return false;
                if (nodePath.Equals(userId, StringComparison.OrdinalIgnoreCase)
                    || nodePath.StartsWith(userId + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
                var legacyPrefix = "User/" + userId;
                return nodePath.Equals(legacyPrefix, StringComparison.OrdinalIgnoreCase)
                       || nodePath.StartsWith(legacyPrefix + "/", StringComparison.OrdinalIgnoreCase);
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
    /// <summary>
    /// The collected access rules, each pairing the operations it applies to with a check
    /// predicate that returns true to allow access (false to fall through to the next rule).
    /// </summary>
    public IReadOnlyList<(IReadOnlyCollection<NodeOperation> Operations, Func<NodeValidationContext, string?, bool> Check)> Rules { get; init; } = [];

    /// <summary>
    /// Returns a new rule set with an additional access rule appended.
    /// </summary>
    /// <param name="operations">The operations the rule applies to (empty means all operations).</param>
    /// <param name="rule">The check predicate, given the validation context and the user id, returning true to allow access.</param>
    /// <returns>A new rule set including the added rule.</returns>
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

        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
        {
            foreach (var (operations, check) in ruleSet.Rules)
            {
                if (operations.Count == 0 || operations.Contains(context.Operation))
                {
                    if (check(context, userId))
                        return Observable.Return(true);
                }
            }
            return Observable.Return(false);
        }
    }
}
