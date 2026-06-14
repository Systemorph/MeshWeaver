using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

namespace MeshWeaver.AI;

/// <summary>
/// The built-in chat commands as a static-repo import source for the <c>Command</c> partition. The
/// same nodes <see cref="BuiltInCommandProvider"/> serves in-memory are materialized into the DB
/// partition by the static-repo import on boot, so commands are served from the database on the
/// distributed/PG path (Orleans routing does NOT consult the in-memory adapter — without this import
/// <c>/agent</c>, <c>/model</c> and <c>/harness</c> are invisible to <c>namespace:Command</c> queries,
/// i.e. the chat finds no commands). Mirrors <see cref="AgentStaticRepoSource"/>.
/// </summary>
public sealed class CommandStaticRepoSource(BuiltInCommandProvider provider) : IStaticRepoSource
{
    /// <inheritdoc />
    public string Partition => CommandNodeType.RootNamespace;

    /// <inheritdoc />
    // Command definitions ship with no meaningful version → fingerprint on content, so an edited
    // built-in command re-imports.
    public bool Versioned => false;

    /// <inheritdoc />
    // Content command nodes only — drop any "_"-prefixed governance node (defensive; the provider
    // currently emits none). The importer never overwrites/prunes governance.
    public IReadOnlyList<MeshNode> EnumerateSourceNodes() =>
        provider.GetStaticNodes()
            .Where(n => !n.Segments.Skip(1).Any(seg => seg.StartsWith('_')))
            .ToArray();

    /// <inheritdoc />
    public MeshNode? PartitionRoot => new(CommandNodeType.RootNamespace)
    {
        Name = "Commands",
        NodeType = "Space",
        State = MeshNodeState.Active,
        Content = new MarkdownContent
        {
            Content = """
                # Commands

                The built-in chat slash-commands available across the platform (`/agent`, `/model`,
                `/harness`). Spaces and NodeTypes add their own Command nodes in their own partitions.
                """
        }
    };
}
