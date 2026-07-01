using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

namespace MeshWeaver.AI;

/// <summary>
/// The built-in chat skills as a static-repo import source for the <c>Skill</c> partition — the same
/// nodes <see cref="BuiltInSkillProvider"/> serves in-memory, materialized into the DB partition on
/// boot so skills are served from the database on the distributed/PG path (Orleans routing does NOT
/// consult the in-memory adapter; without this import <c>/agent</c>, <c>/model</c> and <c>/harness</c>
/// are invisible to <c>namespace:Skill</c> queries). Replaces the retired <c>CommandStaticRepoSource</c>.
/// </summary>
public sealed class SkillStaticRepoSource(BuiltInSkillProvider provider) : IStaticRepoSource
{
    /// <inheritdoc />
    public string Partition => SkillNodeType.RootNamespace;

    /// <inheritdoc />
    // Skill definitions ship with no meaningful version → fingerprint on content, so an edited
    // built-in skill re-imports.
    public bool Versioned => false;

    /// <inheritdoc />
    // Skill content nodes PLUS the partition's PublicRead "_Policy" (PartitionAccessPolicy). On the
    // SYNCED path the in-memory provider that served the policy is gated off, so the policy MUST be
    // imported or the partition has no read policy → its skills are unreadable (the Harness wedge —
    // OrleansHarnessPartitionPublicReadTest). Only OTHER "_"-governance is dropped.
    public IReadOnlyList<MeshNode> EnumerateSourceNodes() =>
        provider.GetStaticNodes()
            .Where(n => n.NodeType == "PartitionAccessPolicy"
                        || !n.Segments.Skip(1).Any(seg => seg.StartsWith('_')))
            .ToArray();

    /// <inheritdoc />
    public MeshNode? PartitionRoot => new(SkillNodeType.RootNamespace)
    {
        Name = "Skills",
        Icon = "/static/NodeTypeIcons/sparkle.svg",
        NodeType = "Space",
        State = MeshNodeState.Active,
        Content = new MarkdownContent
        {
            Content = """
                # Skills

                The built-in chat skills available across the platform (`/agent`, `/model`,
                `/harness`). Spaces and NodeTypes add their own Skill nodes in their own partitions.
                """
        }
    };
}
