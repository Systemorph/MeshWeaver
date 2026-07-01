using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

namespace MeshWeaver.AI;

/// <summary>
/// The built-in agents as a static-repo import source for the <c>Agent</c> partition. The same
/// nodes <see cref="BuiltInAgentProvider"/> serves in-memory are here materialized (content +
/// prerender) into the DB partition by the static-repo import on boot, so agents are served from
/// the database on the distributed/PG path (Orleans routing doesn't consult the in-memory adapter).
/// Governance nodes (the access policy) are NOT imported — they stay served by the in-memory
/// provider. See <c>Doc/Architecture/StaticRepoImport.md</c>.
/// </summary>
public sealed class AgentStaticRepoSource(BuiltInAgentProvider provider) : IStaticRepoSource
{
    /// <inheritdoc />
    public string Partition => "Agent";

    /// <inheritdoc />
    // Agent definitions ship with no meaningful version → fingerprint on content, so an edited
    // agent .md re-imports.
    public bool Versioned => false;

    /// <inheritdoc />
    // Content agent nodes PLUS the partition's PublicRead "_Policy" (PartitionAccessPolicy). On the
    // SYNCED path the in-memory provider that served the policy is gated off, so the policy MUST be
    // imported or the partition has no read policy → its nodes are unreadable (same wedge as Harness —
    // see HarnessStaticRepoSource + OrleansHarnessPartitionPublicReadTest). Agent happens to work on
    // atioz today only because its policy persisted in the DB before the sync-gate existed; a fresh
    // env would fail identically. Only OTHER "_"-governance (per-user _Access grants) is dropped.
    public IReadOnlyList<MeshNode> EnumerateSourceNodes() =>
        provider.GetStaticNodes()
            .Where(n => n.NodeType == "PartitionAccessPolicy"
                        || !n.Segments.Skip(1).Any(seg => seg.StartsWith('_')))
            .ToArray();

    /// <inheritdoc />
    public MeshNode? PartitionRoot => new("Agent")
    {
        Name = "Agents",
        Icon = "/static/NodeTypeIcons/sparkle.svg",
        NodeType = "Space",
        State = MeshNodeState.Active,
        Content = new MarkdownContent
        {
            Content = """
                # Agents

                The built-in agents available across the platform. Open one to see its instructions,
                or use the chat input below to start a thread with an agent.
                """
        }
    };
}
