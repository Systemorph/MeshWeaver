using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.AI;

/// <summary>
/// Ships the built-in chat skills as read-only <c>nodeType:Skill</c> nodes under the
/// <see cref="SkillNodeType.RootNamespace"/> partition: <c>/agent</c>, <c>/model</c>, <c>/harness</c> —
/// each a <see cref="SkillActionKind.Pick"/> behaviour (the old <c>CommandDefinition</c>). Users,
/// Spaces and NodeTypes add their own Skill nodes in their own partitions; all are discovered together
/// via <see cref="SkillNodeType.SkillQueries"/>. Replaces the retired <c>BuiltInCommandProvider</c>.
/// </summary>
public class BuiltInSkillProvider : IStaticNodeProvider
{
    /// <inheritdoc />
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        // `sort:order` puts the catalog head first so the picker's default-to-first selects it —
        // ordering lives in the QUERY (data), never replicated in the GUI picker.
        yield return Pick("agent", "Switch the agent for subsequent messages",
            "namespace:Agent nodeType:Agent -content.modelTier:utility sort:order", "agentName", "Choose an agent");
        yield return Pick("model", "Switch the AI model for subsequent messages",
            "namespace:_Provider nodeType:LanguageModel scope:descendants sort:order", "modelName", "Choose a model");
        yield return Pick("harness", "Switch the harness (runtime) for subsequent messages",
            "namespace:Harness nodeType:Harness sort:order", "harness", "Choose a harness");
    }

    private static MeshNode Pick(string id, string description, string query, string field, string title) =>
        new(id, SkillNodeType.RootNamespace)
        {
            NodeType = SkillNodeType.NodeType,
            Name = $"/{id}",
            Description = description,
            Category = "Skills",
            Icon = "Sparkle",
            State = MeshNodeState.Active,
            Content = new SkillDefinition
            {
                Action = new SkillAction { Kind = SkillActionKind.Pick, Query = query, Field = field, Title = title }
            }
        };
}
