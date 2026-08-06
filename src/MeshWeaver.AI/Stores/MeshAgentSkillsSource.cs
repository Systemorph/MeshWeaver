using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Stores;

/// <summary>
/// Mesh-node implementation of the Microsoft Agent Framework's <see cref="AgentSkillsSource"/>,
/// serving our <c>nodeType:Skill</c> nodes as MAF skills.
///
/// <para>MAF's built-in sources read skills from a directory of <c>SKILL.md</c> files or from an
/// in-memory list. Ours are already mesh nodes — authored in the portal, versioned, permissioned,
/// and discovered through namespace inheritance. This adapter exposes exactly that set through MAF's
/// interface, so nothing about how we author or store skills changes.</para>
///
/// <para><b>Discovery goes through the SAME query set the GUI uses</b> —
/// <see cref="SkillNodeType.SkillQueries"/>, which the chat's combobox, slash menu and autocomplete
/// also call. That shared entry point is the point: the skills a user sees listed are exactly the
/// skills an agent round resolves. Layering is <b>1.</b> the user's own, <b>2.</b> the context node's
/// partition, <b>3.</b> the node type's partition, then the platform defaults; layers 2 and 3 resolve
/// over the whole partition subtree, so a space or plugin can author a skill next to the content it
/// belongs to. Where two layers define the same skill name the more specific one wins.</para>
///
/// <para><b>One live query, no per-skill reads.</b> The synced collection carries whole nodes,
/// content included, so listing N skills costs ONE shared subscription — the body is already in hand
/// when MAF asks for it, which is why <c>GetContentAsync</c> does no I/O at all.</para>
///
/// <para><b>Behaviour-only skills.</b> A Skill node can be an instruction (a <c>SKILL.md</c> body), a
/// behaviour (open a picker, connect a CLI), or both. Only the body is meaningful to a model, so a
/// behaviour-only skill simply yields an empty body — the same benign no-op MAF's own file source
/// produces for an empty <c>SKILL.md</c>.</para>
/// </summary>
/// <remarks>
/// <see cref="AgentSkillsSource"/> is marked <c>[Experimental("MAAI001")]</c> upstream; the project
/// opts in through <c>NoWarn</c> in <c>MeshWeaver.AI.csproj</c>.
/// </remarks>
public sealed class MeshAgentSkillsSource : AgentSkillsSource
{
    // Mirrors AgentSkillFrontmatter's own limits, which are internal to MAF. Values that exceed them
    // are trimmed (description) or the skill is skipped (name) rather than throwing, so one
    // malformed node can never take down skill discovery for a whole round.
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;

    // MAF's skill-name grammar: lowercase alphanumerics separated by single hyphens.
    private static readonly Regex NonNameChars = new("[^a-z0-9]+", RegexOptions.Compiled);

    private readonly MeshStoreAccess mesh;
    private readonly ILogger<MeshAgentSkillsSource>? logger;
    private readonly string? contextPath;
    private readonly string? userPath;
    private readonly string? nodeTypePath;
    private readonly IReadOnlyList<string> precedence;

    /// <summary>
    /// Creates a source over the skills visible from a chat context.
    /// </summary>
    /// <param name="hub">Hub supplying the workspace, I/O pool and the caller's identity.</param>
    /// <param name="contextPath">The space / context node path — its partition subtree (layer 2).</param>
    /// <param name="userPath">The user's home path, contributing <c>{user}/Skill</c> (layer 1).</param>
    /// <param name="nodeTypePath">The current node's TYPE path — its partition subtree (layer 3).</param>
    public MeshAgentSkillsSource(
        IMessageHub hub,
        string? contextPath = null,
        string? userPath = null,
        string? nodeTypePath = null)
    {
        mesh = new MeshStoreAccess(hub, nameof(MeshAgentSkillsSource));
        logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger<MeshAgentSkillsSource>();
        // 🚨 Paths only — the QUERIES are resolved live in GetSkills through
        // AiSettingsNodeType.ObserveSkillQueries, the exact same call the chat's slash autocomplete
        // and slash execution make. That is what guarantees the skills a user SEES are the skills
        // this source FEEDS to the agent framework, INCLUDING any sources the user configured or a
        // skill package installed. Never reconstruct these query strings here.
        this.contextPath = contextPath;
        this.userPath = userPath;
        this.nodeTypePath = nodeTypePath;

        // The partitions that define layer precedence, most specific first. A skill's own path tells
        // us which layer it came from — the union returns one flat set, so rank is recovered here
        // rather than inferred from result order, which the union does not promise.
        precedence = new[]
            {
                AgentPickerProjection.PartitionOf(userPath),
                AgentPickerProjection.PartitionOf(contextPath),
                AgentPickerProjection.PartitionOf(nodeTypePath),
            }
            .Where(partition => !string.IsNullOrEmpty(partition))
            .Select(partition => partition!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The LIVE set of skills visible from this context — re-emits whenever a skill is added,
    /// edited or removed anywhere in the layered scope. This is the surface MeshWeaver consumes.
    /// </summary>
    public IObservable<IReadOnlyCollection<AgentSkill>> GetSkills() =>
        mesh.Stream(() => AiSettingsNodeType
            .ObserveSkillQueries(
                mesh.Hub.GetWorkspace(), mesh.Hub, mesh.Hub.ServiceProvider,
                userPath, contextPath, nodeTypePath)
            .Select(resolved => mesh.Query(resolved).Select(Project))
            // Switch, not Merge: when the user edits their skill sources the old query set is
            // abandoned rather than left racing the new one.
            .Switch());

    /// <inheritdoc />
    public override Task<IList<AgentSkill>> GetSkillsAsync(
        AgentSkillsSourceContext context, CancellationToken cancellationToken = default) =>
        // Sanctioned SDK-boundary adapter — MAF's signature can carry one snapshot and nothing more,
        // so the .Take(1) sits here rather than in GetSkills, which stays live.
        GetSkills()
            .Take(1)
            .Select(skills => (IList<AgentSkill>)skills.ToList())
            .ToTask(cancellationToken);

    /// <summary>
    /// Projects the unioned snapshot into MAF skills, applying layer precedence: when two layers
    /// define the same skill name the more specific one wins, which is what lets a user override a
    /// platform skill by name.
    /// </summary>
    private IReadOnlyCollection<AgentSkill> Project(IEnumerable<MeshNode> nodes) =>
        nodes
            .Where(node => string.Equals(node.NodeType, SkillNodeType.NodeType,
                StringComparison.OrdinalIgnoreCase))
            .Select(node => (Node: node, Skill: ToSkill(node)))
            .Where(candidate => candidate.Skill is not null)
            .GroupBy(candidate => candidate.Skill!.Frontmatter.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(candidate => RankOf(candidate.Node.Path)).First().Skill!)
            .OrderBy(skill => skill.Frontmatter.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Projects one Skill node into a MAF skill, or <see langword="null"/> when it cannot satisfy
    /// MAF's frontmatter rules — validated with MAF's OWN validators, so this adapter can never hand
    /// the harness a skill it would reject.
    /// </summary>
    private AgentSkill? ToSkill(MeshNode node)
    {
        var name = SanitizeName(node.Id);
        if (!AgentSkillFrontmatter.ValidateName(name, out var nameReason))
        {
            logger?.LogDebug(
                "Skipping skill {Path}: name '{Name}' is not valid for the agent framework — {Reason}",
                node.Path, name, nameReason);
            return null;
        }

        var description = Trim(node.Description ?? node.Name ?? name, MaxDescriptionLength);
        if (!AgentSkillFrontmatter.ValidateDescription(description, out var descriptionReason))
        {
            logger?.LogDebug(
                "Skipping skill {Path}: description is not valid for the agent framework — {Reason}",
                node.Path, descriptionReason);
            return null;
        }

        var frontmatter = new AgentSkillFrontmatter(name, description)
        {
            Metadata = new AdditionalPropertiesDictionary
            {
                // The mesh path is the skill's identity everywhere else in the platform (it is what
                // load_skill takes), so it travels with the skill rather than being reconstructed.
                ["meshPath"] = node.Path,
            },
        };

        return new MeshAgentSkill(frontmatter, InstructionsOf(node) ?? string.Empty);
    }

    /// <summary>
    /// The precedence rank of a node by the partition it lives in — lower wins. Anything outside the
    /// three context layers is a platform default and ranks last.
    /// </summary>
    private int RankOf(string path)
    {
        var partition = AgentPickerProjection.PartitionOf(path);
        if (partition is null) return precedence.Count;
        for (var i = 0; i < precedence.Count; i++)
            if (string.Equals(precedence[i], partition, StringComparison.OrdinalIgnoreCase))
                return i;
        return precedence.Count;
    }

    /// <summary>
    /// Folds an arbitrary mesh node id into MAF's skill-name grammar: lowercased, every run of
    /// non-alphanumerics collapsed to a single hyphen, hyphens trimmed from the ends, and clipped to
    /// the length limit (then re-trimmed, since clipping can expose a trailing hyphen).
    /// </summary>
    private static string SanitizeName(string id)
    {
        var lowered = NonNameChars.Replace(id.ToLowerInvariant(), "-").Trim('-');
        return lowered.Length <= MaxNameLength ? lowered : lowered[..MaxNameLength].Trim('-');
    }

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// One skill. Both the frontmatter and the body come from the synced query's node, so nothing
    /// here performs I/O — <see cref="GetContentAsync"/> is a completed <see cref="ValueTask{T}"/>
    /// over a value already in hand.
    /// </summary>
    private sealed class MeshAgentSkill(AgentSkillFrontmatter frontmatter, string instructions) : AgentSkill
    {
        /// <inheritdoc />
        public override AgentSkillFrontmatter Frontmatter => frontmatter;

        /// <inheritdoc />
        public override ValueTask<string> GetContentAsync(CancellationToken cancellationToken = default) =>
            new(instructions);
    }

    /// <summary>
    /// The instructions a Skill node carries, tolerating typed or JSON-round-tripped content.
    /// <see langword="null"/> for a pure behaviour skill, which has no body.
    /// </summary>
    private string? InstructionsOf(MeshNode node) => node.Content switch
    {
        SkillDefinition definition => definition.Instructions,
        JsonElement json => TryDeserialize(json)?.Instructions,
        _ => null,
    };

    private SkillDefinition? TryDeserialize(JsonElement json)
    {
        try
        {
            return JsonSerializer.Deserialize<SkillDefinition>(
                json.GetRawText(), mesh.Hub.JsonSerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
