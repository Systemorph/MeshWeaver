using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using YamlDotNet.Serialization;

namespace MeshWeaver.Documentation;

/// <summary>
/// Provides MeshWeaver platform documentation as static MeshNodes
/// loaded from embedded markdown resources.
/// </summary>
public class DocumentationNodeProvider : IStaticNodeProvider
{
    public const string RootNamespace = "Doc";

    private static readonly Lazy<MeshNode[]> LazyNodes = new(LoadNodes);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .Build();

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    public IEnumerable<MeshNode> GetStaticNodes()
    {
        // Read-only policy for the Doc namespace — all documentation is unmodifiable
        yield return new MeshNode("_Policy", RootNamespace)
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Access Policy",
            Content = new PartitionAccessPolicy
            {
                Create = false,
                Update = false,
                Delete = false,
                Comment = true,
                Thread = true
            }
        };

        // Grant all authenticated users read access to documentation
        yield return new MeshNode($"{WellKnownUsers.Public}_Access", RootNamespace)
        {
            NodeType = "AccessAssignment",
            Name = $"{WellKnownUsers.Public} Access",
            Content = new AccessAssignment
            {
                AccessObject = WellKnownUsers.Public,
                DisplayName = "All authenticated users",
                Roles = [new RoleAssignment { Role = "Viewer" }]
            }
        };

        foreach (var node in LazyNodes.Value)
            yield return node;
    }

    private static MeshNode[] LoadNodes()
    {
        var assembly = typeof(DocumentationNodeProvider).Assembly;
        var prefix = $"{assembly.GetName().Name}.Data.";

        var nodes = new List<MeshNode>();

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(prefix))
                     .Order())
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;

            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            var relativePath = ResourceNameToPath(resourceName, prefix);

            MeshNode? node;
            if (resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                node = ParseJsonNode(content, relativePath);
            else if (resourceName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                node = ParseCodeNode(content, relativePath);
            else
                node = ParseMarkdownNode(content, relativePath);

            if (node != null)
                nodes.Add(node);
        }

        return nodes.ToArray();
    }

    private static MeshNode? ParseMarkdownNode(string content, string relativePath)
    {
        var (id, ns) = DeriveIdAndNamespace(relativePath);

        var document = Markdig.Markdown.Parse(content, Pipeline);
        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();

        FrontMatter? frontMatter = null;
        if (yamlBlock != null)
        {
            try
            {
                var yamlContent = yamlBlock.Lines.ToString();
                frontMatter = YamlDeserializer.Deserialize<FrontMatter>(yamlContent);
            }
            catch
            {
                // Use defaults if YAML parsing fails
            }
        }

        var markdownBody = yamlBlock != null
            ? content[(yamlBlock.Span.End + 1)..].TrimStart('\r', '\n')
            : content;

        // Full node path for resolving relative links in markdown
        var fullPath = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";

        var markdownDocument = MarkdownContent.Parse(markdownBody, ns, fullPath) with
        {
            Authors = frontMatter?.Authors,
            Tags = frontMatter?.Tags,
            Thumbnail = frontMatter?.Thumbnail,
            Abstract = frontMatter?.Abstract ?? frontMatter?.Description
        };

        return new MeshNode(id, ns)
        {
            NodeType = frontMatter?.NodeType ?? "Markdown",
            Name = frontMatter?.Name ?? frontMatter?.Title ?? id,
            Category = frontMatter?.Category,
            Icon = ResolveIcon(frontMatter?.Icon ?? frontMatter?.Thumbnail, ns),
            Content = markdownDocument,
            PreRenderedHtml = markdownDocument.PrerenderedHtml
        };
    }

    private static MeshNode? ParseJsonNode(string content, string relativePath)
    {
        try
        {
            var node = JsonSerializer.Deserialize<MeshNode>(content);
            if (node == null) return null;

            // Ensure namespace is under Doc/ root
            var (id, ns) = DeriveIdAndNamespace(relativePath);
            if (string.IsNullOrEmpty(node.Namespace))
                node = node with { Namespace = ns };

            return node;
        }
        catch
        {
            return null;
        }
    }

    private static MeshNode? ParseCodeNode(string content, string relativePath)
    {
        var (id, ns) = DeriveIdAndNamespace(relativePath);

        // Extract display name from meshweaver header comment if present
        string? displayName = null;
        var headerMatch = Regex.Match(content, @"//\s*DisplayName:\s*(.+)", RegexOptions.IgnoreCase);
        if (headerMatch.Success)
            displayName = headerMatch.Groups[1].Value.Trim();

        return new MeshNode(id, ns)
        {
            Name = displayName ?? id,
            NodeType = "Code",
            Content = new CodeConfiguration { Code = content }
        };
    }

    /// <summary>
    /// Converts embedded resource name back to file path relative to Data/.
    /// E.g. "MeshWeaver.Documentation.Data.AI.AgenticAI.md" → "AI/AgenticAI.md"
    /// </summary>
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
        // Strip any file extension (.md, .json, .cs)
        var pathWithoutExt = Path.GetFileNameWithoutExtension(relativePath);
        var dir = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
        pathWithoutExt = string.IsNullOrEmpty(dir) ? pathWithoutExt : $"{dir}/{pathWithoutExt}";

        pathWithoutExt = pathWithoutExt.Trim('/').Replace('\\', '/');

        var lastSlash = pathWithoutExt.LastIndexOf('/');

        // index.md files represent their parent namespace root
        if (lastSlash < 0)
        {
            if (pathWithoutExt.Equals("index", StringComparison.OrdinalIgnoreCase))
                return (RootNamespace, null); // Data/index.md → path "Doc"

            return (pathWithoutExt, RootNamespace);
        }

        var id = pathWithoutExt[(lastSlash + 1)..];
        if (id.Equals("index", StringComparison.OrdinalIgnoreCase))
        {
            // e.g. AI/index.md → path "Doc/AI" (collapse index into parent)
            var parentDir = pathWithoutExt[..lastSlash];
            var parentSlash = parentDir.LastIndexOf('/');
            if (parentSlash < 0)
                return (parentDir, RootNamespace);
            return (parentDir[(parentSlash + 1)..], $"{RootNamespace}/{parentDir[..parentSlash]}");
        }

        var ns = $"{RootNamespace}/{pathWithoutExt[..lastSlash]}";
        return (id, ns);
    }

    private static string ResolveIcon(string? iconValue, string? ns)
    {
        if (string.IsNullOrEmpty(iconValue))
            return "Document";
        if (iconValue.StartsWith("/") || iconValue.StartsWith("http") || iconValue.StartsWith("data:"))
            return iconValue;
        if (!string.IsNullOrEmpty(ns))
        {
            // Strip the "Doc/" prefix from namespace since DocContent serves relative to Content/ folder
            var contentPath = ns.StartsWith($"{RootNamespace}/", StringComparison.OrdinalIgnoreCase)
                ? ns[(RootNamespace.Length + 1)..]
                : ns;
            return $"/static/DocContent/{contentPath}/{iconValue}";
        }
        return iconValue;
    }

    private class FrontMatter
    {
        public string? NodeType { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? Title { get; set; }
        public string? Abstract { get; set; }
        public string? Published { get; set; }
        public List<string>? Authors { get; set; }
        public List<string>? Tags { get; set; }
        public string? Thumbnail { get; set; }
    }
}
