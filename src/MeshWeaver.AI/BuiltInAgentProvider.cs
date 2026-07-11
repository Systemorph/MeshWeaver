using System.Collections.Immutable;
using System.Reflection;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MeshWeaver.AI;

/// <summary>
/// Provides built-in agent nodes and supporting documentation shipped from the platform.
/// Loads agent definitions from embedded .md resources and serves them as static MeshNodes.
/// </summary>
public class BuiltInAgentProvider : IStaticNodeProvider
{
    /// <summary>Id of the built-in utility agent that names new threads from the first user message.</summary>
    public const string ThreadNamerId = "ThreadNamer";

    private static readonly Lazy<MeshNode[]> LazyNodes = new(LoadAllNodes);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .Build();

    private static readonly IDeserializer AgentYamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly IDeserializer MarkdownYamlDeserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    private const string RootNamespace = "Agent";

    /// <summary>
    /// Returns the static agent nodes: the world-readable Agent-namespace access policy, the
    /// built-in ThreadNamer agent, and every agent/markdown node loaded from embedded resources.
    /// </summary>
    /// <returns>The built-in agent and supporting MeshNodes.</returns>
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        // Read-only, world-readable policy for the Agent namespace. PublicRead grants
        // Read to every user (the agent catalog is a public catalog) WITHOUT needing a
        // per-user role at the Agent scope — and because this is a static provider node,
        // it is present from the first permission evaluation (no synced-query cold-start
        // race, which previously left the agent picker empty → "No suitable agent").
        // The write caps keep the built-in agents unmodifiable.
        yield return new MeshNode("_Policy", RootNamespace)
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
                Thread = false
            }
        };

        foreach (var node in LazyNodes.Value)
            yield return node;
    }

    private static MeshNode[] LoadAllNodes()
    {
        return ImmutableList.Create(CreateThreadNamerNode())
            .AddRange(LoadEmbeddedNodes())
            .ToArray();
    }

    private static MeshNode CreateThreadNamerNode()
    {
        var config = new AgentConfiguration
        {
            Id = ThreadNamerId,
            Instructions = """
                            You are a thread naming assistant. Given the user's first message in a new conversation,
                            generate a concise descriptive name and a PascalCase identifier for the thread.

                            Respond with EXACTLY two lines, nothing else:
                            Name: <short descriptive name, 3-8 words, no quotes>
                            Id: <PascalCase identifier, alphanumeric only, no spaces>

                            Examples:
                            - "How do I set up CI/CD?" -> Name: Setting Up CI CD Pipeline / Id: SettingUpCiCdPipeline
                            - "What's the pricing for enterprise?" -> Name: Enterprise Pricing Inquiry / Id: EnterprisePricingInquiry
                            - "Fix the login bug" -> Name: Fix Login Bug / Id: FixLoginBug
                            """,
            ExposedInNavigator = false,
            ModelTier = "utility",
        };

        // Order lives on the node (sorts last in the picker); not duplicated on the config.
        return new MeshNode(ThreadNamerId, "Agent")
        {
            Name = "Thread Namer",
            NodeType = "Agent",
            Order = 999,
            // Custom inline SVG (same line-art style as the .md agents — viewBox 0 0 24 24,
            // stroke currentColor) so it reads consistently with the rest of the catalog instead of
            // falling back to the generic bot glyph. A name-tag/label fits the thread-naming role.
            Icon = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><path d=\"M3.5 3.5h7l9.4 9.4a2 2 0 0 1 0 2.83l-4.17 4.17a2 2 0 0 1-2.83 0L3.5 10.5z\"/><circle cx=\"7.6\" cy=\"7.6\" r=\"1.3\" fill=\"currentColor\"/></svg>",
            Content = config
        };
    }

    private static IEnumerable<MeshNode> LoadEmbeddedNodes()
    {
        var assembly = typeof(BuiltInAgentProvider).Assembly;
        var prefix = $"{assembly.GetName().Name}.Data.";
        // Data/Skill/*.md are nodeType:Skill nodes served by BuiltInSkillProvider — not agents/docs.
        var skillPrefix = $"{prefix}{SkillNodeType.RootNamespace}.";

        // Prefer the on-disk AI content section (content/ai/Agent) — editable in the mesh and
        // syncable back to the repo. The parse (frontmatter → node) is identical to the embedded path,
        // so the node set is unchanged; only the SOURCE of the bytes moves from the DLL to disk.
        var root = AiContentLocator.SectionRoot();
        var agentDir = root is null ? null : System.IO.Path.Combine(root, "Agent");
        if (agentDir is not null && System.IO.Directory.Exists(agentDir))
        {
            foreach (var file in System.IO.Directory
                         .EnumerateFiles(agentDir, "*.md", System.IO.SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var content = System.IO.File.ReadAllText(file);
                var relativePath = "Agent/" +
                    System.IO.Path.GetRelativePath(agentDir, file).Replace('\\', '/');
                var node = ParseEmbeddedNode(content, relativePath);
                if (node != null)
                    yield return node;
            }
            yield break;
        }

        // Fallback: EMBEDDED resources — the offline default (MAUI / monolith) never loses its agents
        // even if the on-disk section isn't shipped/found.
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(prefix) && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                                 && !n.StartsWith(skillPrefix, StringComparison.Ordinal))
                     .Order())
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;

            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            var relativePath = ResourceNameToPath(resourceName, prefix);

            var node = ParseEmbeddedNode(content, relativePath);
            if (node != null)
                yield return node;
        }
    }

    private static MeshNode? ParseEmbeddedNode(string content, string relativePath)
    {
        var document = Markdig.Markdown.Parse(content, Pipeline);
        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        if (yamlBlock == null) return null;

        var yamlContent = yamlBlock.Lines.ToString();
        var markdownBody = content[(yamlBlock.Span.End + 1)..].TrimStart('\r', '\n');

        // Check if this is an agent file (nodeType: Agent in frontmatter)
        if (IsAgentFrontmatter(yamlContent))
            return ParseAgentNode(yamlContent, markdownBody, relativePath);

        return ParseMarkdownNode(yamlContent, markdownBody, relativePath);
    }

    private static bool IsAgentFrontmatter(string yamlContent)
    {
        try
        {
            var fm = AgentYamlDeserializer.Deserialize<NodeTypePeek>(yamlContent);
            return string.Equals(fm?.NodeType, "Agent", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static MeshNode ParseAgentNode(string yamlContent, string markdownBody, string relativePath)
    {
        var (id, ns) = DeriveIdAndNamespace(relativePath);
        var frontMatter = AgentYamlDeserializer.Deserialize<AgentFrontMatter>(yamlContent);

        var agentConfig = new AgentConfiguration
        {
            Id = id,
            Description = frontMatter.Description,
            Instructions = string.IsNullOrWhiteSpace(markdownBody) ? null : markdownBody.Trim(),
            CustomIconSvg = frontMatter.CustomIconSvg,
            IsDefault = frontMatter.IsDefault,
            ExposedInNavigator = frontMatter.ExposedInNavigator,
            Delegations = frontMatter.Delegations?.Select(d => new AgentDelegation
            {
                AgentPath = d.AgentPath ?? "",
                Instructions = d.Instructions
            }).ToList(),
            Handoffs = frontMatter.Handoffs?.Select(h => new AgentHandoff
            {
                AgentPath = h.AgentPath ?? "",
                Instructions = h.Instructions
            }).ToList(),
            Plugins = frontMatter.Plugins?.Select(AgentPluginReference.Parse).ToList(),
            ContextMatchPattern = frontMatter.ContextMatchPattern,
            ModelTier = frontMatter.ModelTier
        };

        // Node-level metadata (name, description, icon, group, order) lives on the
        // MeshNode — NOT duplicated on the AgentConfiguration content. The picker groups
        // by Category, so the harness group (frontmatter groupName, default MeshWeaver)
        // maps onto the node's Category.
        return new MeshNode(id, ns)
        {
            NodeType = "Agent",
            Name = frontMatter.Name ?? frontMatter.DisplayName ?? id,
            Description = frontMatter.Description,
            Category = frontMatter.GroupName ?? frontMatter.Category ?? Harnesses.MeshWeaver,
            Icon = frontMatter.Icon ?? "Bot",
            Order = frontMatter.Order,
            Content = agentConfig
        };
    }

    private static MeshNode ParseMarkdownNode(string yamlContent, string markdownBody, string relativePath)
    {
        var (id, ns) = DeriveIdAndNamespace(relativePath);
        var frontMatter = MarkdownYamlDeserializer.Deserialize<MarkdownFrontMatter>(yamlContent);

        var markdownDocument = MarkdownContent.Parse(markdownBody, ns);

        return new MeshNode(id, ns)
        {
            NodeType = frontMatter?.NodeType ?? "Markdown",
            Name = frontMatter?.Name ?? id,
            Category = frontMatter?.Category,
            Icon = frontMatter?.Icon ?? "Document",
            Content = markdownDocument,
            PreRenderedHtml = markdownDocument.PrerenderedHtml
        };
    }

    private static string ResourceNameToPath(string resourceName, string prefix)
    {
        var withoutPrefix = resourceName[prefix.Length..];
        var lastDot = withoutPrefix.LastIndexOf('.');
        if (lastDot > 0)
        {
            var nameWithoutExt = withoutPrefix[..lastDot].Replace('.', '/');
            var ext = withoutPrefix[lastDot..];
            return nameWithoutExt + ext;
        }
        return withoutPrefix.Replace('.', '/');
    }

    private static (string Id, string? Namespace) DeriveIdAndNamespace(string relativePath)
    {
        var pathWithoutExt = relativePath;
        if (pathWithoutExt.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            pathWithoutExt = pathWithoutExt[..^3];

        pathWithoutExt = pathWithoutExt.Trim('/').Replace('\\', '/');

        var lastSlash = pathWithoutExt.LastIndexOf('/');
        if (lastSlash < 0)
            return (pathWithoutExt, null);

        var ns = pathWithoutExt[..lastSlash];
        var id = pathWithoutExt[(lastSlash + 1)..];
        return (id, ns);
    }

    // Peek class to check nodeType without full deserialization
    private class NodeTypePeek
    {
        public string? NodeType { get; set; }
    }

    private class AgentFrontMatter
    {
        public string? NodeType { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? DisplayName { get; set; }
        public string? GroupName { get; set; }
        public bool IsDefault { get; set; }
        public bool ExposedInNavigator { get; set; }
        public string? ContextMatchPattern { get; set; }
        public int Order { get; set; }
        public string? CustomIconSvg { get; set; }
        public string? ModelTier { get; set; }
        public List<DelegationEntry>? Delegations { get; set; }
        public List<HandoffEntry>? Handoffs { get; set; }
        public List<string>? Plugins { get; set; }
    }

    private class DelegationEntry
    {
        public string? AgentPath { get; set; }
        public string? Instructions { get; set; }
    }

    private class HandoffEntry
    {
        public string? AgentPath { get; set; }
        public string? Instructions { get; set; }
    }

    private class MarkdownFrontMatter
    {
        public string? NodeType { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? Icon { get; set; }
    }
}
