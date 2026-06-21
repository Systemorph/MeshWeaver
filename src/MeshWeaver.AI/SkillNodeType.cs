using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.AI;

/// <summary>
/// The "Skill" node type — the unified, first-class "a thing that does something" concept. A Skill is
/// EITHER a <b>behaviour</b> the chat performs when invoked (open a combobox + select an agent/model/
/// harness, load a document into the content window, connect/login — what we used to call slash
/// <i>commands</i>) AND/OR an <b>instruction</b> (a <c>SKILL.md</c> body mounted to the Claude Code /
/// Copilot CLIs and advertised to the MeshWeaver agent to load on demand). Users / Spaces / NodeTypes
/// ship their own Skill nodes, discovered through namespace inheritance (<see cref="SkillQueries"/>).
///
/// <para>This SUBSUMES the old <c>Command</c> node type — <see cref="SkillActionKind.Pick"/> is the old
/// <c>CommandDefinition</c> (query + composer field + title). Agents are NOT skills (agents == system
/// prompts; skills == capabilities loaded as you go).</para>
/// </summary>
public static class SkillNodeType
{
    /// <summary>NodeType discriminator.</summary>
    public const string NodeType = "Skill";

    /// <summary>Namespace (partition) the built-in skill catalog lives under.</summary>
    public const string RootNamespace = "Skill";

    /// <summary>
    /// Registers the Skill type node + its content type (PublicRead), and — when not DB-synced — the
    /// static provider that serves the built-in skills (<c>/agent</c>, <c>/model</c>, <c>/harness</c>)
    /// read-only under the <c>Skill</c> partition. Mirrors the retired <c>AddCommandType</c>.
    /// </summary>
    public static TBuilder AddSkillType<TBuilder>(this TBuilder builder,
        IReadOnlySet<string>? serveFromPartition = null) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        builder.ConfigureHub(config => config.WithType<SkillDefinition>(nameof(SkillDefinition)));

        var dbSynced = serveFromPartition?.Contains(RootNamespace) == true;
        builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<BuiltInSkillProvider>();
            if (!dbSynced)
            {
                services.AddSingleton<IStaticNodeProvider>(sp => sp.GetRequiredService<BuiltInSkillProvider>());
                services.AddSingleton<IPartitionStorageProvider>(sp =>
                    new StaticNodePartitionStorageProvider(
                        RootNamespace,
                        sp.GetRequiredService<BuiltInSkillProvider>(),
                        description: "Built-in chat skills (read-only)."));
            }
            return services;
        });
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

    /// <summary>
    /// The query that discovers the skills available in a context — the SAME unified registry pattern as
    /// agents and models: platform <c>Skill</c> + the current space's <c>{space}/Skill</c> + the user's
    /// <c>{user}/Skill</c>, as one <c>namespace:A|B|C</c> exact-membership query
    /// (<see cref="AgentPickerProjection.BuildSkillQueries"/>). <paramref name="contextPath"/> names the
    /// space (its partition); <paramref name="userPath"/> the user's home.
    /// </summary>
    public static string[] SkillQueries(string? contextPath, string? userPath)
        => AgentPickerProjection.BuildSkillQueries(userPath, AgentPickerProjection.PartitionOf(contextPath));

    /// <summary>
    /// Projects a mesh-node snapshot into the available skills, deduped by id (the slash word). Reads
    /// the slash word from <see cref="MeshNode.Id"/> and help text from <see cref="MeshNode.Description"/>;
    /// the spec is the typed (or JsonElement-fallback) <see cref="SkillDefinition"/> content.
    /// </summary>
    public static IReadOnlyList<SkillInfo> ProjectSkills(
        IEnumerable<MeshNode> snapshot, JsonSerializerOptions jsonOptions)
    {
        var byId = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in snapshot)
        {
            if (string.IsNullOrEmpty(node.Id)) continue;
            if (!string.Equals(node.NodeType, NodeType, StringComparison.OrdinalIgnoreCase)) continue;
            var def = node.Content switch
            {
                SkillDefinition d => d,
                JsonElement je => TryDeserialise(je, jsonOptions),
                _ => null,
            };
            if (def is null) continue;
            byId[node.Id] = new SkillInfo
            {
                Id = node.Id,
                Name = node.Name,
                Description = node.Description,
                Path = node.Path,
                Definition = def,
            };
        }
        return byId.Values.OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static SkillDefinition? TryDeserialise(JsonElement je, JsonSerializerOptions opts)
    {
        try { return JsonSerializer.Deserialize<SkillDefinition>(je.GetRawText(), opts); }
        catch { return null; }
    }
}

/// <summary>
/// Content of a <see cref="SkillNodeType"/> node. A skill is a behaviour (<see cref="Action"/>) and/or
/// an instruction (<see cref="Instructions"/>). The skill's NAME and DESCRIPTION come from the owning
/// <see cref="MeshWeaver.Mesh.MeshNode"/>.
/// </summary>
public record SkillDefinition
{
    /// <summary>
    /// INSTRUCTION skill — the <c>SKILL.md</c> body (markdown). Mounted to the CLI harnesses
    /// (when <see cref="AutoMount"/>) and advertised to the MeshWeaver agent to load on demand. Null
    /// for a pure behaviour skill.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// BEHAVIOUR skill — what the skill DOES when invoked in the MeshWeaver chat (open a picker, load a
    /// document into the content window, connect/login). Null for a pure instruction skill.
    /// </summary>
    public SkillAction? Action { get; init; }

    /// <summary>
    /// Whether the skill is <b>auto-mounted</b>: instruction skills materialised to the shared skills
    /// directory so the CLI harnesses discover them up-front, and advertised to the MeshWeaver harness.
    /// When <c>false</c> the skill still exists but is referenced/loaded on demand only. Default <c>true</c>.
    /// </summary>
    public bool AutoMount { get; init; } = true;

    /// <summary>
    /// Whether invoking the skill launches a <b>sub-thread</b> (a separate conversation the skill runs
    /// in) rather than running inline in the current thread. Default <c>false</c>.
    /// </summary>
    public bool LaunchesSubThread { get; init; } = false;

    /// <summary>
    /// The harness this skill belongs to — <c>null</c> = the MeshWeaver harness / applies everywhere.
    /// This is what makes the status bar a per-harness, CLI-like control strip: the status row renders
    /// exactly the ACTIVE harness's skills (each chip showing the current composer value, clickable into
    /// its Pick combobox), and the <c>/</c>-menu offers them. Claude Code ships <c>/model</c> + <c>/effort</c>
    /// (Harness = "ClaudeCode") with options it provides; Copilot ships its own; MeshWeaver keeps
    /// <c>/agent</c> + <c>/model</c>. A skill is a value + a picker + a status chip — one concept.
    /// </summary>
    public string? Harness { get; init; }
}

/// <summary>
/// A behaviour a skill performs in the MeshWeaver chat — one discriminated <see cref="Kind"/>.
/// <see cref="SkillActionKind.Pick"/> is the old <c>CommandDefinition</c> (Query + Field + Title).
/// </summary>
public record SkillAction
{
    /// <summary>
    /// The action discriminator. NOT <c>required</c> and defaults to <see cref="SkillActionKind.Pick"/>:
    /// the hub serializer OMITS default-valued properties on write, so a <c>Pick</c> action (the default
    /// enum value 0) is written with no <c>kind</c> field — a <c>required</c> Kind then fails to
    /// deserialize ("missing required properties including: 'kind'"), dropping every Pick skill. A plain
    /// default round-trips the omitted value correctly.
    /// </summary>
    public SkillActionKind Kind { get; init; } = SkillActionKind.Pick;

    /// <summary><see cref="SkillActionKind.Pick"/>: the mesh query whose nodes the combobox lists.</summary>
    public string? Query { get; init; }

    /// <summary><see cref="SkillActionKind.Pick"/>: the camelCase <c>ThreadComposer</c> field the
    /// selected node PATH is written to (<c>harness</c> / <c>agentName</c> / <c>modelName</c>).</summary>
    public string? Field { get; init; }

    /// <summary><see cref="SkillActionKind.Pick"/>: the combobox title (e.g. "Choose a model").</summary>
    public string? Title { get; init; }

    /// <summary><see cref="SkillActionKind.OpenContent"/>: the node/path to load into the content window.</summary>
    public string? ContentPath { get; init; }

    /// <summary><see cref="SkillActionKind.Connect"/>/<see cref="SkillActionKind.Disconnect"/>: the
    /// provider (<c>ClaudeCode</c> / <c>Copilot</c>).</summary>
    public string? Provider { get; init; }
}

/// <summary>What a <see cref="SkillAction"/> does when the skill is invoked.</summary>
public enum SkillActionKind
{
    /// <summary>Open a combobox over <see cref="SkillAction.Query"/> and write the pick to the composer.</summary>
    Pick,

    /// <summary>Load a node/document into the content window.</summary>
    OpenContent,

    /// <summary>Log in / connect this provider's CLI subscription.</summary>
    Connect,

    /// <summary>Log out / forget this provider's CLI subscription.</summary>
    Disconnect,
}

/// <summary>
/// A resolved skill for the chat input — its slash word (<see cref="Id"/>), name/description, path, and
/// the spec. Projected from a <c>nodeType:Skill</c> node by <see cref="SkillNodeType.ProjectSkills"/>.
/// </summary>
public record SkillInfo
{
    /// <summary>The slash word (e.g. <c>model</c> for <c>/model</c>) — the Skill node's id.</summary>
    public required string Id { get; init; }

    /// <summary>Display name (the node's Name).</summary>
    public string? Name { get; init; }

    /// <summary>Help text shown in autocomplete (the node's Description).</summary>
    public string? Description { get; init; }

    /// <summary>The skill node's full path (for load-on-demand by the agent).</summary>
    public string? Path { get; init; }

    /// <summary>The skill spec.</summary>
    public required SkillDefinition Definition { get; init; }

    /// <summary>For a <see cref="SkillActionKind.Pick"/> skill, the picker request carrying the typed argument.</summary>
    public NodePickerRequest? ToPickerRequest(string? searchTerm) =>
        Definition.Action is { Kind: SkillActionKind.Pick, Query: { } q, Field: { } f }
            ? new NodePickerRequest(q, f, Definition.Action.Title ?? Name ?? Id, searchTerm)
            : null;
}

/// <summary>
/// A request from a <see cref="SkillActionKind.Pick"/> skill to the host to render the generic node
/// selector: list the mesh nodes matching <see cref="Query"/>, and on selection write the chosen
/// node's PATH onto the composer field <see cref="ComposerField"/> (a camelCase <c>ThreadComposer</c>
/// property — <c>harness</c>, <c>agentName</c>, <c>modelName</c>). When <see cref="SearchTerm"/> is
/// non-null the host pre-filters to it and auto-selects an exact match. (Was <c>MeshWeaver.AI.Commands</c>.)
/// </summary>
public record NodePickerRequest(string Query, string ComposerField, string Title, string? SearchTerm = null);
