using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using MeshWeaver.Mesh;
using YamlDotNet.Serialization;

namespace MeshWeaver.Hosting.Persistence.Parsers;

/// <summary>
/// Parses .md files with YAML front matter into MeshNode objects.
/// </summary>
public partial class MarkdownFileParser : IFileFormatParser
{
    private const string DefaultMarkdownIcon = "Document";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .Build();

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults)
        .Build();

    public IReadOnlyList<string> SupportedExtensions => [".md"];

    public Task<MeshNode?> ParseAsync(string filePath, string content, string relativePath, CancellationToken ct = default)
    {
        // Derive id and namespace from path
        var (id, ns) = DeriveIdAndNamespace(relativePath, filePath);

        // Parse markdown to extract YAML front matter
        var document = Markdown.Parse(content, Pipeline);
        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();

        MarkdownFrontMatter? frontMatter = null;
        if (yamlBlock != null)
        {
            try
            {
                var yamlContent = yamlBlock.Lines.ToString();
                frontMatter = YamlDeserializer.Deserialize<MarkdownFrontMatter>(yamlContent);
            }
            catch
            {
                // If YAML parsing fails, use defaults
            }
        }

        // Extract markdown content (without YAML block)
        var markdownContent = yamlBlock != null
            ? content.Substring(yamlBlock.Span.End + 1).TrimStart('\r', '\n')
            : content;

        // Get file last modified time
        var fileInfo = new FileInfo(filePath);
        var lastModified = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);

        var node = new MeshNode(id, ns)
        {
            NodeType = frontMatter?.NodeType ?? "Markdown",
            Name = frontMatter?.Name ?? id,
            Category = frontMatter?.Category,
            Description = frontMatter?.Description,
            Icon = frontMatter?.Icon ?? DefaultMarkdownIcon,
            State = ParseState(frontMatter?.State),
            IsPersistent = true,
            LastModified = lastModified,
            Content = markdownContent
        };

        return Task.FromResult<MeshNode?>(node);
    }

    public Task<string> SerializeAsync(MeshNode node, CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        // Build YAML front matter
        var frontMatter = new MarkdownFrontMatter
        {
            NodeType = node.NodeType != "Markdown" ? node.NodeType : null,
            Name = node.Name != node.Id ? node.Name : null,
            Category = node.Category,
            Description = node.Description,
            Icon = node.Icon != DefaultMarkdownIcon ? node.Icon : null,
            State = node.State != MeshNodeState.Active ? node.State.ToString() : null
        };

        // Only write YAML block if there's meaningful content
        var hasYamlContent = frontMatter.NodeType != null ||
                            frontMatter.Name != null ||
                            frontMatter.Category != null ||
                            frontMatter.Description != null ||
                            frontMatter.Icon != null ||
                            frontMatter.State != null;

        if (hasYamlContent)
        {
            sb.AppendLine("---");
            var yaml = YamlSerializer.Serialize(frontMatter).TrimEnd();
            sb.AppendLine(yaml);
            sb.AppendLine("---");
            sb.AppendLine();
        }

        // Append markdown content
        if (node.Content is string markdownContent)
        {
            sb.Append(markdownContent);
        }

        return Task.FromResult(sb.ToString());
    }

    public bool CanSerialize(MeshNode node)
    {
        // Handle nodes with NodeType "Markdown" or with string content
        return node.NodeType == "Markdown" || node.Content is string;
    }

    private static (string Id, string? Namespace) DeriveIdAndNamespace(string relativePath, string filePath)
    {
        // Remove extension and normalize
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

    private static MeshNodeState ParseState(string? state)
    {
        if (string.IsNullOrEmpty(state))
            return MeshNodeState.Active;

        return Enum.TryParse<MeshNodeState>(state, true, out var result)
            ? result
            : MeshNodeState.Active;
    }

    /// <summary>
    /// YAML front matter model for markdown files.
    /// </summary>
    private class MarkdownFrontMatter
    {
        public string? NodeType { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? State { get; set; }
    }
}
