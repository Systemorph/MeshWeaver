using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using MeshWeaver.Mesh;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MeshWeaver.AI;

/// <summary>
/// The one place that converts a <b>Skill</b> node ↔ its <c>.md</c> file (YAML frontmatter + a
/// <c>SKILL.md</c> body). <see cref="Parse"/> is what <see cref="BuiltInSkillProvider"/> reads the
/// built-in <c>content/ai/Skill</c> files with; <see cref="Serialize"/> is its exact inverse, used to
/// write a mesh-edited skill BACK to that section (the sync-back). Keeping both here — round-trip
/// pinned by <c>SkillMarkdownRoundTripTest</c> — is what guarantees a sync-back never corrupts a skill.
/// </summary>
public static class SkillMarkdown
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseYamlFrontMatter().Build();

    private static readonly IDeserializer YamlIn = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // OmitNull so a null field disappears; the serialize model below uses nullable fields and sets the
    // built-in defaults (autoMount=true, category="Skills", icon="Sparkle", name="/{id}", order=0) to
    // null so they are omitted exactly as the hand-authored files omit them → a clean round-trip.
    private static readonly ISerializer YamlOut = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>Parses a skill <c>.md</c> (frontmatter + body) into a <c>Skill</c> MeshNode, or null.</summary>
    public static MeshNode? Parse(string content, string id) => TryParse(content, id, out _);

    /// <summary>
    /// Parses a skill <c>.md</c> into a <c>Skill</c> MeshNode, and on failure says WHY.
    ///
    /// <para>🚨 The reason is the whole point. A skill that fails to parse is SKIPPED, never thrown on
    /// — these files load during mesh startup, so an uncaught throw takes the host down (the 2026-08-07
    /// incident). But a bare <c>null</c> makes "skipped" indistinguishable from "there was never a file
    /// here": the catalog silently loses an entry and nothing is red anywhere, which the author
    /// experiences as <i>"my skill doesn't appear and there's no error"</i>. Callers must surface
    /// <paramref name="error"/> (<see cref="BuiltInSkillProvider"/> records it as an
    /// <see cref="AiContentLoadFailure"/> and writes it to stderr), and the catalog guard test asserts
    /// on it — that is what turns a dropped skill into a diagnosable one.</para>
    /// </summary>
    /// <param name="content">The file's text (YAML front matter + markdown body).</param>
    /// <param name="id">The skill id — the file name without its extension, i.e. the slash word.</param>
    /// <param name="error">Null on success; otherwise an author-actionable reason.</param>
    /// <returns>The parsed node, or null when <paramref name="error"/> is set.</returns>
    public static MeshNode? TryParse(string content, string id, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(id))
        {
            error = "no id — a skill's id is its file name, which cannot be empty";
            return null;
        }

        var document = Markdig.Markdown.Parse(content, Pipeline);
        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        if (yamlBlock == null)
        {
            error = "no YAML front matter — a skill file must open with a '---' fenced block "
                    + "declaring at least 'nodeType: Skill' and a 'description:'";
            return null;
        }

        SkillFrontMatter? fm;
        try
        {
            fm = YamlIn.Deserialize<SkillFrontMatter>(yamlBlock.Lines.ToString());
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            // Malformed front-matter (e.g. an unquoted ':' inside a description) must never take down
            // skill loading. Built-in skills load during mesh startup, so an uncaught throw here
            // crashes the whole host (every full-mesh test dies). Skip the bad file instead — but SAY SO.
            error = "invalid YAML front matter. An unquoted ':' inside a value is the usual cause — "
                    + $"quote the value. {ex.Message}";
            return null;
        }
        if (fm == null)
        {
            error = "empty YAML front matter — nothing to build a skill from";
            return null;
        }

        var body = content[(yamlBlock.Span.End + 1)..].TrimStart('\r', '\n').Trim();

        var definition = new SkillDefinition
        {
            Instructions = string.IsNullOrWhiteSpace(body) ? null : body,
            AutoMount = fm.AutoMount,
            LaunchesSubThread = fm.LaunchesSubThread,
            Harness = fm.Harness,
            Action = fm.Action is null ? null : new SkillAction
            {
                Kind = Enum.TryParse<SkillActionKind>(fm.Action.Kind, ignoreCase: true, out var kind)
                    ? kind : SkillActionKind.Pick,
                Query = fm.Action.Query,
                Field = fm.Action.Field,
                Title = fm.Action.Title,
                ContentPath = fm.Action.ContentPath,
                Provider = fm.Action.Provider,
            },
        };

        return new MeshNode(id, SkillNodeType.RootNamespace)
        {
            NodeType = SkillNodeType.NodeType,
            Name = fm.Name ?? $"/{id}",
            Description = fm.Description,
            Category = fm.Category ?? "Skills",
            Icon = fm.Icon ?? "Sparkle",
            Order = fm.Order,
            State = MeshNodeState.Active,
            Content = definition,
        };
    }

    /// <summary>
    /// Serializes a <c>Skill</c> node back to its <c>.md</c> text — the exact inverse of
    /// <see cref="Parse"/> (the built-in defaults are omitted, so an unedited skill round-trips to a
    /// byte-identical file). The node's <see cref="MeshNode.Content"/> must be a <see cref="SkillDefinition"/>.
    /// </summary>
    public static string Serialize(MeshNode node)
    {
        // Fail fast rather than silently serialize an empty skill — a lossy write would clobber the
        // source file during sync-back. Callers normalise a JsonElement fallback to a typed
        // SkillDefinition (or skip) before reaching here.
        if (node.Content is not SkillDefinition def)
            throw new ArgumentException(
                $"Skill node '{node.Id}' has {node.Content?.GetType().Name ?? "null"} content, not a {nameof(SkillDefinition)}.",
                nameof(node));

        var fm = new SkillFrontMatterOut
        {
            NodeType = SkillNodeType.NodeType,
            // Omit the fields Parse fills with defaults, so a hand-authored file round-trips unchanged.
            Name = string.Equals(node.Name, $"/{node.Id}", StringComparison.Ordinal) ? null : node.Name,
            Description = node.Description,
            Icon = string.Equals(node.Icon, "Sparkle", StringComparison.Ordinal) ? null : node.Icon,
            Category = string.Equals(node.Category, "Skills", StringComparison.Ordinal) ? null : node.Category,
            Order = node.Order == 0 ? null : node.Order,
            AutoMount = def.AutoMount ? null : false,               // default true → omit
            LaunchesSubThread = def.LaunchesSubThread ? true : null, // default false → omit
            Harness = def.Harness,
            Action = def.Action is null ? null : new SkillActionFrontMatterOut
            {
                // Pick is the default enum value → omit (Parse defaults a missing kind to Pick).
                Kind = def.Action.Kind == SkillActionKind.Pick ? null : def.Action.Kind.ToString(),
                Query = def.Action.Query,
                Field = def.Action.Field,
                Title = def.Action.Title,
                ContentPath = def.Action.ContentPath,
                Provider = def.Action.Provider,
            },
        };

        var yaml = YamlOut.Serialize(fm).TrimEnd('\r', '\n');
        var body = def.Instructions?.Trim() ?? "";
        return body.Length == 0 ? $"---\n{yaml}\n---\n" : $"---\n{yaml}\n---\n\n{body}\n";
    }

    // ── The frontmatter models ──────────────────────────────────────────────

    // Read model — matches the hand-authored files (camelCase, missing fields default).
    internal sealed class SkillFrontMatter
    {
        public string? NodeType { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? Category { get; set; }
        public int Order { get; set; }
        public bool AutoMount { get; set; } = true;
        public bool LaunchesSubThread { get; set; }
        public string? Harness { get; set; }
        public SkillActionFrontMatter? Action { get; set; }
    }

    internal sealed class SkillActionFrontMatter
    {
        public string? Kind { get; set; }
        public string? Query { get; set; }
        public string? Field { get; set; }
        public string? Title { get; set; }
        public string? ContentPath { get; set; }
        public string? Provider { get; set; }
    }

    // Write model — all nullable so defaults can be OMITTED (OmitNull) for a clean round-trip.
    private sealed class SkillFrontMatterOut
    {
        public string? NodeType { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? Category { get; set; }
        public int? Order { get; set; }
        public bool? AutoMount { get; set; }
        public bool? LaunchesSubThread { get; set; }
        public string? Harness { get; set; }
        public SkillActionFrontMatterOut? Action { get; set; }
    }

    private sealed class SkillActionFrontMatterOut
    {
        public string? Kind { get; set; }
        public string? Query { get; set; }
        public string? Field { get; set; }
        public string? Title { get; set; }
        public string? ContentPath { get; set; }
        public string? Provider { get; set; }
    }
}
