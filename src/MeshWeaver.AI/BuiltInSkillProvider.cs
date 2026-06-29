using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MeshWeaver.AI;

/// <summary>
/// Provides the built-in skill nodes from embedded <c>Data/Skill/*.md</c> resources — the SAME
/// .md-with-YAML authoring model as agents (<see cref="BuiltInAgentProvider"/>). Each skill is a
/// <c>nodeType: Skill</c> markdown file: <b>behaviour</b> skills carry an <c>action:</c> block in the
/// frontmatter (<c>Pick</c> / <c>OpenContent</c> / <c>Connect</c>), <b>instruction</b> skills carry
/// their how-to in the markdown body. The slash word is the file name (<c>agent.md</c> → <c>/agent</c>).
/// Discovered together with per-space / per-user skills via <see cref="SkillNodeType.SkillQueries"/>.
/// </summary>
public class BuiltInSkillProvider : IStaticNodeProvider
{
    private static readonly Lazy<MeshNode[]> LazyNodes = new(LoadEmbeddedNodes);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .Build();

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <inheritdoc />
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        // Read-only, world-readable policy for the Skill namespace — the skill catalog is public, same
        // as Agent/Harness. On the SYNCED path this _Policy MUST be imported (SkillStaticRepoSource),
        // else the partition has no read policy and the skills are unreadable → the chat finds no skills
        // (the Harness wedge, atioz 2026-06-15). The write caps keep the built-in skills unmodifiable.
        yield return new MeshNode("_Policy", SkillNodeType.RootNamespace)
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Access Policy",
            Content = new PartitionAccessPolicy
            {
                PublicRead = true,
                Create = false,
                Update = false,
                Delete = false,
                Comment = false,
                Thread = false,
            }
        };

        foreach (var node in LazyNodes.Value)
            yield return node;
    }

    private static MeshNode[] LoadEmbeddedNodes()
    {
        var assembly = typeof(BuiltInSkillProvider).Assembly;
        // Resource names dot-separate path segments: Data/Skill/agent.md → {asm}.Data.Skill.agent.md
        var skillPrefix = $"{assembly.GetName().Name}.Data.{SkillNodeType.RootNamespace}.";

        var nodes = new List<MeshNode>();
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(skillPrefix, StringComparison.Ordinal)
                                 && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                     .Order())
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;
            using var reader = new StreamReader(stream);
            var node = ParseSkillNode(reader.ReadToEnd(), ResourceNameToId(resourceName, skillPrefix));
            if (node != null) nodes.Add(node);
        }
        return nodes.ToArray();
    }

    private static MeshNode? ParseSkillNode(string content, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        var document = Markdig.Markdown.Parse(content, Pipeline);
        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        if (yamlBlock == null) return null;

        var fm = Yaml.Deserialize<SkillFrontMatter>(yamlBlock.Lines.ToString());
        if (fm == null) return null;

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

    private static string ResourceNameToId(string resourceName, string skillPrefix)
    {
        var rest = resourceName[skillPrefix.Length..]; // e.g. "agent.md"
        var lastDot = rest.LastIndexOf('.');           // strip the ".md" extension
        return lastDot > 0 ? rest[..lastDot] : rest;
    }

    private sealed class SkillFrontMatter
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

    private sealed class SkillActionFrontMatter
    {
        public string? Kind { get; set; }
        public string? Query { get; set; }
        public string? Field { get; set; }
        public string? Title { get; set; }
        public string? ContentPath { get; set; }
        public string? Provider { get; set; }
    }
}
