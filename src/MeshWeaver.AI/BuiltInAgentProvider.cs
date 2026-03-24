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

    public IEnumerable<MeshNode> GetStaticNodes()
    {
        // Read-only policy for the Agent namespace — built-in agents are unmodifiable
        yield return new MeshNode("_Policy", RootNamespace)
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Access Policy",
            Content = new PartitionAccessPolicy
            {
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
        var nodes = new List<MeshNode> { CreateThreadNamerNode() };
        nodes.AddRange(LoadEmbeddedNodes());
        return nodes.ToArray();
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
            Order = 999,
        };

        return new MeshNode(ThreadNamerId, "Agent")
        {
            Name = "Thread Namer",
            NodeType = "Agent",
            Content = config
        };
    }

    private static IEnumerable<MeshNode> LoadEmbeddedNodes()
    {
        var assembly = typeof(BuiltInAgentProvider).Assembly;
        var prefix = $"{assembly.GetName().Name}.Data.";

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(prefix) && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
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
            DisplayName = frontMatter.Name ?? frontMatter.DisplayName ?? id,
            Description = frontMatter.Description,
            Instructions = string.IsNullOrWhiteSpace(markdownBody) ? null : markdownBody.Trim(),
            Icon = frontMatter.Icon,
            CustomIconSvg = frontMatter.CustomIconSvg,
            GroupName = frontMatter.GroupName,
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
            Plugins = frontMatter.Plugins?.Select(ParsePluginReference).ToList(),
            PreferredModel = frontMatter.PreferredModel,
            ContextMatchPattern = frontMatter.ContextMatchPattern,
            Order = frontMatter.Order
        };

        return new MeshNode(id, ns)
        {
            NodeType = "Agent",
            Name = frontMatter.Name ?? frontMatter.DisplayName ?? id,
            Category = frontMatter.Category ?? "Agents",
            Icon = frontMatter.Icon ?? "Bot",
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

    /// <summary>
    /// Parses "PluginName" or "PluginName:Method1,Method2" into AgentPluginReference.
    /// </summary>
    private static AgentPluginReference ParsePluginReference(string s)
    {
        var colonIndex = s.IndexOf(':');
        if (colonIndex < 0)
            return new AgentPluginReference { Name = s.Trim() };

        return new AgentPluginReference
        {
            Name = s[..colonIndex].Trim(),
            Methods = s[(colonIndex + 1)..].Split(',').Select(m => m.Trim()).ToList()
        };
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
        public string? PreferredModel { get; set; }
        public int Order { get; set; }
        public string? CustomIconSvg { get; set; }
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
