using MeshWeaver.Graph.Configuration;
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
    // Additive: users may register their OWN harnesses in this partition; the import must never prune
    // them. Only harnesses the build PREVIOUSLY shipped (in the manifest) but has since dropped are removed.
    public PartitionSyncMode SyncMode => PartitionSyncMode.Additive;

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
    // The partition root is the catalog's single nodeType:NodeType node (id = the type name) —
    // NOT a nodeType:Space root. It IS the routable partition root AND the "Harness" NodeType
    // definition, and it LINKS (NodeTypeDefinition.StaticTypeName) to the registered static C#
    // type "Harness" for its HubConfiguration (a non-serialisable delegate, supplied in-memory).
    // Postgres owns this node — the sole runtime owner of @Harness — so the in-memory type-def
    // (registered definition-only) never collides with it. See Doc/Architecture/NodeTypeCatalogs.md.
    public MeshNode? PartitionRoot => new(HarnessNodeType.RootNamespace)
    {
        Name = "Harnesses",
        NodeType = MeshNode.NodeTypePath,
        Icon = "/static/NodeTypeIcons/bot.svg",
        State = MeshNodeState.Active,
        Content = new NodeTypeDefinition
        {
            StaticTypeName = HarnessNodeType.NodeType,
            Description =
                "The available execution harnesses (runtimes) — MeshWeaver's native agent loop, and "
                + "any CLI harness (Claude Code, GitHub Copilot) whose assembly is deployed. Pick one "
                + "per thread with `/harness`."
        }
    };
}
