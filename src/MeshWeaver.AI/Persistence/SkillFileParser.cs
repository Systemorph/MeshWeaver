using System.Text.Json;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;

namespace MeshWeaver.AI.Persistence;

/// <summary>
/// Parses <c>.md</c> files declaring <c>nodeType: Skill</c> into a <b>Skill</b> MeshNode whose
/// content is a <see cref="SkillDefinition"/> — the markdown body becoming
/// <see cref="SkillDefinition.Instructions"/>. The exact counterpart of
/// <see cref="AgentFileParser"/>, and contributed to <see cref="FileFormatParserRegistry"/> the same
/// way (registered by <c>AddAI</c>, so core hosting never references this assembly).
///
/// <para>🚨 <b>Why this exists (#1984), and why the front-matter fix alone was not enough.</b> Two
/// parsers claimed <c>.md</c>: the agent parser, which returns null for anything that is not
/// <c>nodeType: Agent</c>, and the catch-all <c>MarkdownFileParser</c>. A skill therefore fell
/// through to the catch-all, which — once its front-matter casing was fixed — produced a node
/// correctly TYPED <c>Skill</c> but carrying <c>MarkdownContent</c>. <c>SkillNodeType</c> is
/// <c>WithContentType&lt;SkillDefinition&gt;()</c>, and <c>ContentAs&lt;SkillDefinition&gt;</c>
/// recovers only a same-short-named type, so the skill's <c>Instructions</c> read <c>null</c> and
/// the skill was EMPTY. That is worse than the original bug, not better: a node listed as plain
/// Markdown is visible in a listing, whereas a Skill node with no instructions looks completely
/// normal everywhere and simply does nothing.</para>
///
/// <para>The body of the format lives in <see cref="SkillMarkdown"/> — the one place a Skill node ↔
/// its <c>.md</c> conversion is defined, whose round trip <c>SkillMarkdownRoundTripTest</c> pins.
/// This class is the adapter that puts that format into the shared parser chain: it decides whether
/// a file is ours, and it supplies the id/namespace the CHAIN derives from the file's path (which
/// <see cref="SkillMarkdown.TryParse"/> cannot know — it is also fed flat files from
/// <c>content/ai/Skill</c>, where the namespace is always the platform Skill partition).</para>
/// </summary>
public sealed class SkillFileParser : IFileFormatParser
{
    /// <summary>
    /// Options for recovering an untyped content payload as <see cref="SkillDefinition"/> on the
    /// SERIALIZE path. The target type is fixed and non-polymorphic here, so only camelCase-
    /// insensitive property matching is needed — what the Web defaults give. Immutable and never
    /// written after construction, so it is a constant rather than state.
    /// </summary>
    private static readonly JsonSerializerOptions ContentReadOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions => [".md"];

    /// <inheritdoc />
    public MeshNode? Parse(string filePath, string content, string relativePath)
    {
        // Decline every file that does not DECLARE itself a skill — this parser sits in the shared
        // .md chain, so claiming anything else would turn ordinary markdown pages into Skill nodes.
        if (!SkillMarkdown.IsSkillMarkdown(content))
            return null;

        var (id, ns) = MarkdownNodePath.DeriveIdAndNamespace(relativePath);

        // A skill whose front matter is malformed is DECLINED, never thrown on: these files are
        // parsed during mesh startup and an uncaught throw takes the host down (the 2026-08-07
        // incident that gave SkillMarkdown its TryParse). Returning null hands the file to the
        // catch-all Markdown parser, so the author still gets a readable page instead of nothing.
        var node = SkillMarkdown.TryParse(content, id, out _);
        if (node is null)
            return null;

        return node with
        {
            // SkillMarkdown places every skill in the platform Skill partition — correct for the
            // built-in catalog it was written for, wrong here: a plugin ships its skills inside its
            // OWN partition (Hosting/Skill/deployment), and the chain's convention is that the
            // directory chain IS the namespace.
            Namespace = ns,
            LastModified = FileTimestamps.ObservedAt(filePath),
        };
    }

    /// <inheritdoc />
    public string Serialize(MeshNode node) =>
        SkillMarkdown.Serialize(
            AsTypedSkill(node)
            ?? throw new ArgumentException(
                $"Node '{node.Path}' is not a Skill node carrying a {nameof(SkillDefinition)}; "
                + $"select a serializer with {nameof(FileFormatParserRegistry)}.{nameof(FileFormatParserRegistry.GetSerializerFor)}.",
                nameof(node)));

    /// <inheritdoc />
    public bool CanSerialize(MeshNode node) => AsTypedSkill(node) is not null;

    /// <summary>
    /// The node with its content guaranteed to be a typed <see cref="SkillDefinition"/>, or null
    /// when this parser must NOT claim the write.
    ///
    /// <para>🚨 The <c>$type</c> check on the untyped branch is load-bearing, and refusing is the
    /// SAFE answer. <c>SkillDefinition</c> has no required members, so deserializing an unrelated
    /// payload into it — say the <c>MarkdownContent</c> a not-yet-retyped Skill node still carries —
    /// succeeds and yields a definition with every field null. Claiming that write would emit a
    /// skill file containing only front matter and CLOBBER the body on the sync-back. Declining
    /// instead lets the catch-all Markdown parser write the node exactly as it does today: no
    /// regression, and no data loss on the one node shape this fix has not reached yet.</para>
    /// </summary>
    private static MeshNode? AsTypedSkill(MeshNode node)
    {
        if (node.Content is SkillDefinition)
            return node;
        if (!string.Equals(node.NodeType, SkillNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!DeclaresSkillDefinition(node.Content))
            return null;
        var def = node.ContentAs<SkillDefinition>(ContentReadOptions);
        return def is null ? null : node with { Content = def };
    }

    /// <summary>
    /// Whether an untyped content payload carries a <c>$type</c> discriminator naming
    /// <see cref="SkillDefinition"/> — the only evidence that an unresolved blob really is a skill
    /// definition rather than some other shape that would deserialize into an empty one.
    /// </summary>
    private static bool DeclaresSkillDefinition(object? content) => content switch
    {
        JsonElement { ValueKind: JsonValueKind.Object } je =>
            je.TryGetProperty("$type", out var t)
            && t.ValueKind == JsonValueKind.String
            && IsSkillDefinitionDiscriminator(t.GetString()),
        System.Text.Json.Nodes.JsonObject jo =>
            jo.TryGetPropertyValue("$type", out var t)
            && IsSkillDefinitionDiscriminator(t?.GetValue<string>()),
        _ => false,
    };

    // Assembly-qualified and plain discriminators both occur, hence the containment test rather
    // than equality — the same shape AgentFileParser applies to AgentConfiguration.
    private static bool IsSkillDefinitionDiscriminator(string? discriminator) =>
        discriminator is not null
        && discriminator.Contains(nameof(SkillDefinition), StringComparison.Ordinal);
}
