using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.AI;

/// <summary>
/// The "Skill" node type — a first-class mesh node that behaves exactly like a Claude Code / GitHub
/// Copilot <c>SKILL.md</c>: a name + description + instructions the CLI agent invokes on demand.
/// Users (and Spaces / NodeTypes) create Skill nodes; the <c>AgentSkillSyncService</c> materialises
/// every platform Skill node (alongside <c>nodeType:Agent</c> nodes) onto the shared on-disk skills
/// directory, so the co-hosted Claude Code / Copilot CLIs discover and invoke them as skills.
/// </summary>
public static class SkillNodeType
{
    /// <summary>NodeType discriminator.</summary>
    public const string NodeType = "Skill";

    /// <summary>Namespace (partition) the platform skill catalog lives under.</summary>
    public const string RootNamespace = "Skill";

    /// <summary>Registers the Skill type node + its content type, served PublicRead.</summary>
    public static TBuilder AddSkillType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        builder.ConfigureHub(config => config.WithType<SkillDefinition>(nameof(SkillDefinition)));
        return builder;
    }

    /// <summary>The type-definition node for nodeType="Skill".</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Skill",
        Icon = "/static/NodeTypeIcons/sparkle.svg",
        IsSatelliteType = false,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<SkillDefinition>())
    };
}

/// <summary>
/// Content of a <see cref="SkillNodeType"/> node — the <c>SKILL.md</c> body. The skill's NAME and
/// DESCRIPTION come from the owning <see cref="MeshWeaver.Mesh.MeshNode"/> (<c>Name</c> /
/// <c>Description</c> — the same convention agents follow); this carries only the instructions (the
/// markdown the agent runs when it invokes the skill).
/// </summary>
public record SkillDefinition
{
    /// <summary>The skill instructions — the <c>SKILL.md</c> body (markdown).</summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// Whether the skill is <b>auto-mounted</b>: materialised to the shared skills directory so the CLI
    /// harnesses (Claude Code / Copilot) discover it up-front, and advertised to the MeshWeaver harness.
    /// When <c>false</c> the skill still exists in the mesh but is NOT mounted — it is referenced/loaded
    /// on demand (by path) only when a task calls for it. Default <c>true</c>.
    /// </summary>
    public bool AutoMount { get; init; } = true;

    /// <summary>
    /// Whether invoking the skill launches a <b>sub-thread</b> (a separate conversation the skill runs
    /// in) rather than running inline in the current thread. Default <c>false</c>.
    /// </summary>
    public bool LaunchesSubThread { get; init; } = false;
}
