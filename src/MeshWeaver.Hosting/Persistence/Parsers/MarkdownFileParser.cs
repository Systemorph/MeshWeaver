using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using MeshWeaver.Markdown;
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
        var document = Markdig.Markdown.Parse(content, Pipeline);
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

        // Use Published date if available, otherwise file last modified
        if (frontMatter?.Published != null && DateTimeOffset.TryParse(frontMatter.Published, out var publishedDate))
        {
            lastModified = publishedDate;
        }

        // Parse markdown and create MarkdownContent with pre-rendered HTML and code submissions
        // Include article metadata in the MarkdownContent
        var markdownDocument = MarkdownContent.Parse(markdownContent, relativePath) with
        {
            Authors = frontMatter?.Authors,
            Tags = frontMatter?.Tags,
            Thumbnail = frontMatter?.Thumbnail,
            VideoUrl = frontMatter?.VideoUrl,
            VideoDuration = ParseTimeSpan(frontMatter?.VideoDuration),
            VideoTitle = frontMatter?.VideoTitle,
            VideoDescription = frontMatter?.VideoDescription,
            VideoTagLine = frontMatter?.VideoTagLine,
            VideoTranscript = frontMatter?.VideoTranscript
        };

        var node = new MeshNode(id, ns)
        {
            NodeType = frontMatter?.NodeType ?? "Markdown",
            // Name: prefer Name, then Title (legacy), then id
            Name = frontMatter?.Name ?? frontMatter?.Title ?? id,
            Category = frontMatter?.Category,
            // Description: prefer Description, then Abstract (legacy)
            Description = frontMatter?.Description ?? frontMatter?.Abstract,
            // Icon: prefer Icon, then Thumbnail (legacy)
            Icon = frontMatter?.Icon ?? frontMatter?.Thumbnail ?? DefaultMarkdownIcon,
            State = ParseState(frontMatter?.State),
            IsPersistent = true,
            LastModified = lastModified,
            Content = markdownDocument
        };

        return Task.FromResult<MeshNode?>(node);
    }

    private static TimeSpan? ParseTimeSpan(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        return TimeSpan.TryParse(value, out var result) ? result : null;
    }

    public Task<string> SerializeAsync(MeshNode node, CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        // Extract MarkdownContent if available to preserve article metadata
        MarkdownContent? mdContent = node.Content switch
        {
            MarkdownContent doc => doc,
            System.Text.Json.JsonElement jsonElement => ExtractMarkdownContentFromJsonElement(jsonElement),
            _ => null
        };

        // Build YAML front matter from node properties and MarkdownContent metadata
        var frontMatter = new MarkdownFrontMatter
        {
            // Node-level properties
            NodeType = node.NodeType != "Markdown" ? node.NodeType : null,
            Name = node.Name != node.Id ? node.Name : null,
            Category = node.Category,
            Description = node.Description,
            Icon = node.Icon != DefaultMarkdownIcon ? node.Icon : null,
            State = node.State != MeshNodeState.Active ? node.State.ToString() : null,

            // Article metadata from MarkdownContent
            Authors = mdContent?.Authors?.ToList(),
            Tags = mdContent?.Tags?.ToList(),
            Thumbnail = mdContent?.Thumbnail,
            VideoUrl = mdContent?.VideoUrl,
            VideoDuration = mdContent?.VideoDuration?.ToString(),
            VideoTitle = mdContent?.VideoTitle,
            VideoDescription = mdContent?.VideoDescription,
            VideoTagLine = mdContent?.VideoTagLine,
            VideoTranscript = mdContent?.VideoTranscript
        };

        // Only write YAML block if there's meaningful content
        var hasYamlContent = frontMatter.NodeType != null ||
                            frontMatter.Name != null ||
                            frontMatter.Category != null ||
                            frontMatter.Description != null ||
                            frontMatter.Icon != null ||
                            frontMatter.State != null ||
                            frontMatter.Authors?.Count > 0 ||
                            frontMatter.Tags?.Count > 0 ||
                            frontMatter.Thumbnail != null ||
                            frontMatter.VideoUrl != null;

        if (hasYamlContent)
        {
            sb.AppendLine("---");
            var yaml = YamlSerializer.Serialize(frontMatter).TrimEnd();
            sb.AppendLine(yaml);
            sb.AppendLine("---");
            sb.AppendLine();
        }

        // Append markdown content - extract from MarkdownContent if needed
        // Handle JsonElement for cases where Content type was lost during JSON round-trip
        var markdownText = node.Content switch
        {
            MarkdownContent doc => doc.Content,
            string str => str,
            System.Text.Json.JsonElement jsonElement => ExtractContentFromJsonElement(jsonElement),
            _ => null
        };

        if (markdownText != null)
        {
            sb.Append(markdownText);
        }

        return Task.FromResult(sb.ToString());
    }

    /// <summary>
    /// Extracts MarkdownContent object from a JsonElement.
    /// </summary>
    private static MarkdownContent? ExtractMarkdownContentFromJsonElement(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        try
        {
            var content = element.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == System.Text.Json.JsonValueKind.String
                ? contentProp.GetString()
                : null;

            if (content == null)
                return null;

            return new MarkdownContent
            {
                Content = content,
                Authors = ExtractStringList(element, "authors"),
                Tags = ExtractStringList(element, "tags"),
                Thumbnail = ExtractString(element, "thumbnail"),
                VideoUrl = ExtractString(element, "videoUrl"),
                VideoDuration = ExtractTimeSpan(element, "videoDuration"),
                VideoTitle = ExtractString(element, "videoTitle"),
                VideoDescription = ExtractString(element, "videoDescription"),
                VideoTagLine = ExtractString(element, "videoTagLine"),
                VideoTranscript = ExtractString(element, "videoTranscript")
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractString(System.Text.Json.JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static List<string>? ExtractStringList(System.Text.Json.JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != System.Text.Json.JsonValueKind.Array)
            return null;

        var list = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                list.Add(item.GetString()!);
        }
        return list.Count > 0 ? list : null;
    }

    private static TimeSpan? ExtractTimeSpan(System.Text.Json.JsonElement element, string propertyName)
    {
        var str = ExtractString(element, propertyName);
        return str != null && TimeSpan.TryParse(str, out var result) ? result : null;
    }

    public bool CanSerialize(MeshNode node)
    {
        // Handle nodes with NodeType "Markdown", MarkdownContent content, string content,
        // or JsonElement content (from JSON round-trip where type info was lost)
        return node.NodeType == "Markdown"
            || node.Content is MarkdownContent
            || node.Content is string
            || (node.Content is System.Text.Json.JsonElement je && HasMarkdownContent(je));
    }

    /// <summary>
    /// Extracts the Content string from a JsonElement that represents a serialized MarkdownContent.
    /// </summary>
    private static string? ExtractContentFromJsonElement(System.Text.Json.JsonElement element)
    {
        // Try to get the "content" property (MarkdownContent.Content)
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object &&
            element.TryGetProperty("content", out var contentProp))
        {
            if (contentProp.ValueKind == System.Text.Json.JsonValueKind.String)
                return contentProp.GetString();
        }

        // If it's just a string, return it directly
        if (element.ValueKind == System.Text.Json.JsonValueKind.String)
            return element.GetString();

        return null;
    }

    /// <summary>
    /// Checks if a JsonElement looks like it contains markdown content.
    /// </summary>
    private static bool HasMarkdownContent(System.Text.Json.JsonElement element)
    {
        // Check for object with "content" property (MarkdownContent structure)
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object &&
            element.TryGetProperty("content", out var contentProp) &&
            contentProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return true;
        }

        // Check for plain string content
        if (element.ValueKind == System.Text.Json.JsonValueKind.String)
            return true;

        return false;
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
    /// Supports both new MeshNode properties and legacy Article properties for backwards compatibility.
    /// </summary>
    private class MarkdownFrontMatter
    {
        // MeshNode standard properties
        public string? NodeType { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? State { get; set; }

        // Legacy Article properties (for backwards compatibility)
        public string? Title { get; set; }          // Maps to Name
        public string? Abstract { get; set; }       // Maps to Description
        public string? Thumbnail { get; set; }      // Maps to Icon, also stored in MarkdownContent
        public string? Published { get; set; }      // Maps to LastModified
        public List<string>? Authors { get; set; }  // Stored in MarkdownContent
        public List<string>? Tags { get; set; }     // Stored in MarkdownContent

        // Video-related properties (stored in MarkdownContent)
        public string? VideoUrl { get; set; }
        public string? VideoDuration { get; set; }
        public string? VideoTitle { get; set; }
        public string? VideoDescription { get; set; }
        public string? VideoTagLine { get; set; }
        public string? VideoTranscript { get; set; }
    }
}
