using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

namespace MeshWeaver.AI;

/// <summary>
/// The built-in harnesses as a static-repo import source for the <c>Harness</c> partition. The same
/// nodes <see cref="BuiltInHarnessProvider"/> serves in-memory are materialized into the DB partition
/// by the static-repo import on boot, so harnesses are served from the database on the distributed/PG
/// path (Orleans routing does NOT consult the in-memory adapter — without this import the harness
/// catalog is invisible to <c>namespace:Harness</c> queries, i.e. the harness picker is empty / the
/// combobox spins). Mirrors <see cref="AgentStaticRepoSource"/>.
/// </summary>
public sealed class HarnessStaticRepoSource(BuiltInHarnessProvider provider) : IStaticRepoSource
{
    /// <inheritdoc />
    public string Partition => HarnessNodeType.RootNamespace;

    /// <inheritdoc />
    // Harness definitions ship with no meaningful version → fingerprint on content.
    public bool Versioned => false;

    /// <inheritdoc />
    // Content harness nodes PLUS the partition's PublicRead "_Policy" (PartitionAccessPolicy). On the
    // SYNCED path the in-memory provider that used to serve the policy is gated off, so WITHOUT
    // importing it the Harness partition has NO read policy → every user (even admins — partitions are
    // not data-superuser readable) is denied Read → the Harness/MeshWeaver hub init throws
    // UnauthorizedAccessException → FAILED hub → the chat composer's harness picker can't load → the
    // composer disappears (atioz 2026-06-15; Orleans repro: OrleansHarnessPartitionPublicReadTest).
    // Only OTHER "_"-governance (e.g. per-user _Access grants) is dropped; the partition-level access
    // policy MUST travel to the DB partition.
    public IReadOnlyList<MeshNode> EnumerateSourceNodes() =>
        provider.GetStaticNodes()
            .Where(n => n.NodeType == "PartitionAccessPolicy"
                        || !n.Segments.Skip(1).Any(seg => seg.StartsWith('_')))
            .ToArray();

    /// <inheritdoc />
    public MeshNode? PartitionRoot => new(HarnessNodeType.RootNamespace)
    {
        Name = "Harnesses",
        NodeType = "Space",
        State = MeshNodeState.Active,
        Content = new MarkdownContent
        {
            Content = """
                # Harnesses

                The available execution harnesses (runtimes) — MeshWeaver's native agent loop, and any
                CLI harness (Claude Code, GitHub Copilot) whose assembly is deployed. Pick one per
                thread with `/harness`.
                """
        }
    };
}
