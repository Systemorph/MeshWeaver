using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.AI;

/// <summary>
/// Ships the standard chat commands as read-only <c>nodeType:Command</c> nodes under the
/// <see cref="CommandNodeType.RootNamespace"/> partition: <c>/agent</c>, <c>/model</c>,
/// <c>/harness</c>. Each is a declarative <see cref="CommandDefinition"/> (query + composer field +
/// title) — the same spec the C# <c>MeshNodePickCommand</c> subclasses carry, now as data. Users,
/// Spaces and NodeTypes add their own Command nodes in their own partitions; all are discovered
/// together via <see cref="CommandNodeType.CommandQueries"/>.
/// </summary>
public class BuiltInCommandProvider : IStaticNodeProvider
{
    /// <inheritdoc />
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        yield return Command("agent", "Switch the agent for subsequent messages",
            "namespace:Agent nodeType:Agent", "agentName", "Choose an agent");
        yield return Command("model", "Switch the AI model for subsequent messages",
            "namespace:_Provider nodeType:LanguageModel scope:descendants", "modelName", "Choose a model");
        yield return Command("harness", "Switch the harness (runtime) for subsequent messages",
            "namespace:Harness nodeType:Harness", "harness", "Choose a harness");
    }

    private static MeshNode Command(string id, string description, string query, string field, string title) =>
        new(id, CommandNodeType.RootNamespace)
        {
            NodeType = CommandNodeType.NodeType,
            Name = $"/{id}",
            Description = description,
            Category = "Commands",
            Icon = "Sparkle",
            State = MeshNodeState.Active,
            Content = new CommandDefinition { Query = query, ComposerField = field, Title = title }
        };
}
