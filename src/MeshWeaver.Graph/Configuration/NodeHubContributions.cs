using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The extension point a MODULE uses to add per-node-hub registrations to a node type it does not
/// own — the platform declares the seam, the module fills it, and a deployment without the module
/// runs the same code path as a no-op.
///
/// <para>Introduced for the collaboration carve-out: <c>MarkdownNodeType</c> used to call
/// <c>AddComments()</c> directly, which pinned the whole comment/tracked-change implementation into
/// <c>MeshWeaver.Graph</c>. The registration now arrives from the <c>MeshWeaver.Markdown.Collaboration</c>
/// module instead, and <c>MarkdownNodeType</c> only invokes whatever has been contributed.</para>
///
/// <para>🚨 Why this shape and not a static registry: process-wide static state survives mesh
/// disposal and bleeds across tests and partitions (Doc/Architecture/NoStaticState.md). The
/// contributions ride the hub CONFIGURATION, so their lifetime is the hub's.</para>
///
/// <para>🚨 Ordering is what makes this work: <c>MeshNodeHubFactory</c> composes a node hub as
/// <c>nodeConfig(defaultConfig(config))</c> — every <c>ConfigureDefaultNodeHub</c> lambda has
/// already run by the time a node type's own <c>HubConfiguration</c> executes, so a contribution
/// registered by a module is visible to <see cref="ApplyNodeHubContributions(MessageHubConfiguration, string?)"/> below.</para>
/// </summary>
public static class NodeHubContributions
{
    /// <summary>
    /// The accumulated contributions carried on a hub configuration. A record so it is replaced,
    /// never mutated — two modules contributing must not race on one list.
    /// </summary>
    /// <param name="Contributions">The registrations to apply, in registration order. A null
    /// <c>NodeType</c> applies to every node type that admits contributions; a non-null one applies
    /// ONLY to that node type — which is what <c>WithDefaultArea</c> forces: a view set that names
    /// the type's default area must not land on every hub, or the last registration silently wins
    /// and every node type opens on some other type's page.</param>
    public record NodeHubContributionSet(
        ImmutableList<(string? NodeType, Func<MessageHubConfiguration, MessageHubConfiguration> Apply)> Contributions);

    /// <summary>
    /// Contributes a registration to every node hub that opts in with
    /// <see cref="ApplyNodeHubContributions(MessageHubConfiguration, string?)"/>. Called by a module from
    /// <c>ConfigureDefaultNodeHub</c>.
    /// </summary>
    /// <param name="configuration">The default node hub configuration being built.</param>
    /// <param name="contribution">The registration to apply to opting-in node types.</param>
    /// <returns>The configuration, for chaining.</returns>
    public static MessageHubConfiguration AddNodeHubContribution(
        this MessageHubConfiguration configuration,
        Func<MessageHubConfiguration, MessageHubConfiguration> contribution)
        => configuration.AddNodeHubContribution(null, contribution);

    /// <summary>
    /// Contributes a registration to the node type named by <paramref name="nodeType"/> only.
    /// </summary>
    /// <param name="configuration">The default node hub configuration being built.</param>
    /// <param name="nodeType">The node type to contribute to, or null for every opting-in type.</param>
    /// <param name="contribution">The registration to apply.</param>
    /// <returns>The configuration, for chaining.</returns>
    public static MessageHubConfiguration AddNodeHubContribution(
        this MessageHubConfiguration configuration,
        string? nodeType,
        Func<MessageHubConfiguration, MessageHubConfiguration> contribution)
    {
        var existing = configuration.Get<NodeHubContributionSet>();
        return configuration.Set(new NodeHubContributionSet(
            (existing?.Contributions ?? []).Add((nodeType, contribution))));
    }

    /// <summary>
    /// Applies every contributed registration. Called by a node type's own
    /// <c>HubConfiguration</c> to admit module registrations; a no-op when no module contributed,
    /// which is exactly what a deployment without the module runs.
    /// </summary>
    /// <param name="configuration">The node hub configuration being built.</param>
    /// <returns>The configuration with every contribution applied.</returns>
    public static MessageHubConfiguration ApplyNodeHubContributions(
        this MessageHubConfiguration configuration)
        => configuration.ApplyNodeHubContributions(null);

    /// <summary>
    /// Applies every contribution registered for <paramref name="nodeType"/>, plus every unkeyed
    /// one. A no-op when no module contributed — which is exactly what a deployment without the
    /// module runs.
    /// </summary>
    /// <param name="configuration">The node hub configuration being built.</param>
    /// <param name="nodeType">The node type being configured, or null for unkeyed only.</param>
    /// <returns>The configuration with every matching contribution applied.</returns>
    public static MessageHubConfiguration ApplyNodeHubContributions(
        this MessageHubConfiguration configuration,
        string? nodeType)
    {
        var set = configuration.Get<NodeHubContributionSet>();
        if (set is null)
            return configuration;
        return set.Contributions
            .Where(c => c.NodeType is null
                || string.Equals(c.NodeType, nodeType, StringComparison.OrdinalIgnoreCase))
            .Aggregate(configuration, (config, c) => c.Apply(config));
    }
}
