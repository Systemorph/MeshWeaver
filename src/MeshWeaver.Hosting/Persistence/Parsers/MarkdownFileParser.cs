using System.Text;
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
        string? rawYaml = null;
        if (yamlBlock != null)
        {
            rawYaml = yamlBlock.Lines.ToString();
            try
            {
                frontMatter = YamlDeserializer.Deserialize<MarkdownFrontMatter>(rawYaml);
                frontMatter?.NormalizeAliases();
            }
            catch
            {
                // If YAML parsing fails, fall through to the regex-based extractor
                // below — defensive against environment-specific YamlDotNet quirks
                // (line endings, encoding, type-coercion edge cases) that would
                // otherwise silently downgrade NodeType to "Markdown".
            }
        }

        // Defensive frontmatter extractor: if structured YAML deserialization
        // didn't fill in standard fields (Markdig didn't recognize the block,
        // parser threw, returned null, or the property simply wasn't bound),
        // pull each field out with a flat regex. Strictly additive — only
        // fills fields that are null/empty, never overrides a successful
        // parse. Catches the CI-Linux case where Markdig's
        // YamlFrontMatterBlock detection silently fails on a file that
        // starts with a valid `---\nName: …\n---` block (repro:
        // MarkdownNodeIntegrationTest.CollaborativeEditing_NodeExists_InMeshWeaverNamespace
        // — the frontmatter sets `Name: Collaborative Editing` but CI sees
        // Name fall back to the id "CollaborativeEditing").
        //
        // Search the raw YAML if Markdig found one, else search the leading
        // frontmatter section of the file (between the first two `---` markers).
        var defensiveSearchScope = rawYaml ?? ExtractLeadingFrontmatter(content);
        if (!string.IsNullOrEmpty(defensiveSearchScope))
        {
            string? RegexField(string fieldName)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    defensiveSearchScope,
                    $@"^\s*{System.Text.RegularExpressions.Regex.Escape(fieldName)}\s*:\s*(?<value>[^\r\n#]+?)\s*$",
                    System.Text.RegularExpressions.RegexOptions.Multiline
                    | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return match.Success
                    ? match.Groups["value"].Value.Trim().Trim('"', '\'')
                    : null;
            }

            if (string.IsNullOrEmpty(frontMatter?.NodeType))
            {
                var v = RegexField("NodeType");
                if (!string.IsNullOrEmpty(v))
                {
                    frontMatter ??= new MarkdownFrontMatter();
                    frontMatter.NodeType = v;
                }
            }
            if (string.IsNullOrEmpty(frontMatter?.Name))
            {
                var v = RegexField("Name") ?? RegexField("Title");
                if (!string.IsNullOrEmpty(v))
                {
                    frontMatter ??= new MarkdownFrontMatter();
                    frontMatter.Name = v;
                }
            }
            if (string.IsNullOrEmpty(frontMatter?.Category))
            {
                var v = RegexField("Category");
                if (!string.IsNullOrEmpty(v))
                {
                    frontMatter ??= new MarkdownFrontMatter();
                    frontMatter.Category = v;
                }
            }
            if (string.IsNullOrEmpty(frontMatter?.Icon))
            {
                var v = RegexField("Icon") ?? RegexField("Thumbnail");
                if (!string.IsNullOrEmpty(v))
                {
                    frontMatter ??= new MarkdownFrontMatter();
                    frontMatter.Icon = v;
                }
            }
            if (string.IsNullOrEmpty(frontMatter?.State))
            {
                var v = RegexField("State");
                if (!string.IsNullOrEmpty(v))
                {
                    frontMatter ??= new MarkdownFrontMatter();
                    frontMatter.State = v;
                }
            }
        }

        // Extract markdown content (without YAML block)
        var markdownContent = yamlBlock != null
            ? content.Substring(yamlBlock.Span.End + 1).TrimStart('\r', '\n')
            : content;

        // Get file last modified time (graceful fallback if path is inaccessible)
        DateTimeOffset lastModified;
        try
        {
            var fileInfo = new FileInfo(filePath);
            lastModified = fileInfo.Exists
                ? new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero)
                : DateTimeOffset.UtcNow;
        }
        catch
        {
            lastModified = DateTimeOffset.UtcNow;
        }

        // Parse markdown and create MarkdownContent with pre-rendered HTML and code submissions
        var fullNodePath = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";
        var markdownDocument = MarkdownContent.Parse(markdownContent, relativePath, fullNodePath) with
        {
            Authors = frontMatter?.Authors,
            Tags = frontMatter?.Tags,
            Thumbnail = frontMatter?.Thumbnail,
            Abstract = frontMatter?.Abstract
        };

        var node = new MeshNode(id, ns)
        {
            NodeType = frontMatter?.NodeType ?? "Markdown",
            Name = frontMatter?.Name ?? id,
            Category = frontMatter?.Category,
            // Icon: prefer Icon, then Thumbnail (fallback), resolve relative paths
            Icon = ResolveIcon(frontMatter?.Icon ?? frontMatter?.Thumbnail, ns),
            State = ParseState(frontMatter?.State),
            LastModified = lastModified,
            Content = markdownDocument,
            PreRenderedHtml = markdownDocument.PrerenderedHtml
        };

        return Task.FromResult<MeshNode?>(node);
    }

    public Task<string> SerializeAsync(MeshNode node, CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        // Extract MarkdownContent if available
        var mdContent = node.Content as MarkdownContent;

        // Build YAML front matter from node properties and MarkdownContent metadata
        var frontMatter = new MarkdownFrontMatter
        {
            NodeType = node.NodeType != "Markdown" ? node.NodeType : null,
            Name = node.Name != node.Id ? node.Name : null,
            Category = node.Category,
            Icon = node.Icon != null && node.Icon != DefaultMarkdownIcon && !node.Icon.StartsWith("/static/storage/content/") ? node.Icon : null,
            State = node.State != MeshNodeState.Active ? node.State.ToString() : null,
            Authors = mdContent?.Authors?.ToList(),
            Tags = mdContent?.Tags?.ToList(),
            Thumbnail = mdContent?.Thumbnail,
            Abstract = mdContent?.Abstract
        };

        // Only write YAML block if there's meaningful content
        var hasYamlContent = frontMatter.NodeType != null ||
                            frontMatter.Name != null ||
                            frontMatter.Category != null ||
                            frontMatter.Icon != null ||
                            frontMatter.State != null ||
                            frontMatter.Authors?.Count > 0 ||
                            frontMatter.Tags?.Count > 0 ||
                            frontMatter.Thumbnail != null ||
                            frontMatter.Abstract != null;

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

    /// <summary>
    /// Resolves an icon value to a display-ready path: absolute URLs pass through,
    /// relative paths are expanded to content URLs, and missing values fall back to the default icon.
    /// </summary>
    private static string ResolveIcon(string? iconValue, string? ns)
    {
        if (string.IsNullOrEmpty(iconValue))
            return DefaultMarkdownIcon;
        // Already absolute or inline SVG — use as-is
        if (iconValue.StartsWith("/") || iconValue.StartsWith("http") || iconValue.StartsWith("data:") || iconValue.StartsWith("<svg"))
            return iconValue;
        // Relative file path (contains /) — resolve to content URL
        if (iconValue.Contains('/') && !string.IsNullOrEmpty(ns))
            return $"/static/storage/content/{ns}/{iconValue}";
        // Fluent icon name or no namespace — use as-is
        return iconValue;
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

        // index.md represents the parent directory node, not a child called "index"
        // e.g. "FutuRe/index" → id="FutuRe", ns=null
        // e.g. "ACME/Products/index" → id="Products", ns="ACME"
        if (id.Equals("index", StringComparison.OrdinalIgnoreCase))
        {
            var parentSlash = ns.LastIndexOf('/');
            if (parentSlash < 0)
                return (ns, null);
            return (ns[(parentSlash + 1)..], ns[..parentSlash]);
        }

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
    /// Extracts the YAML frontmatter section from the start of a markdown file.
    /// Returns the content between the first two `---` markers, or null if no
    /// frontmatter block is found. Used as a Markdig-independent fallback when
    /// the structured frontmatter detection silently fails (CI-Linux YAML
    /// extension quirks vs. the structurally identical file on Windows).
    /// </summary>
    private static string? ExtractLeadingFrontmatter(string content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        // Skip an optional UTF-8 BOM, then leading whitespace.
        var i = 0;
        if (content.Length > 0 && content[0] == '﻿') i = 1;
        while (i < content.Length && (content[i] == '\n' || content[i] == '\r' || content[i] == ' '))
            i++;
        // Must start with a `---` line.
        if (i + 3 > content.Length || content[i] != '-' || content[i + 1] != '-' || content[i + 2] != '-')
            return null;
        // Advance past the `---` and any chars until the end of that line.
        i += 3;
        while (i < content.Length && content[i] != '\n') i++;
        if (i >= content.Length) return null;
        i++; // past the newline
        var startOfYaml = i;
        // Scan for a closing `---` on its own line.
        while (i < content.Length)
        {
            // Find the start of a line.
            var lineStart = i;
            // Skip any leading spaces on the line.
            while (i < content.Length && content[i] == ' ') i++;
            if (i + 3 <= content.Length
                && content[i] == '-' && content[i + 1] == '-' && content[i + 2] == '-')
            {
                // Confirm it's a closing marker (followed by EOL or whitespace+EOL).
                var afterDashes = i + 3;
                while (afterDashes < content.Length
                    && (content[afterDashes] == ' ' || content[afterDashes] == '\t'))
                    afterDashes++;
                if (afterDashes >= content.Length
                    || content[afterDashes] == '\n' || content[afterDashes] == '\r')
                {
                    return content.Substring(startOfYaml, lineStart - startOfYaml);
                }
            }
            // Advance to next line.
            while (i < content.Length && content[i] != '\n') i++;
            if (i < content.Length) i++;
        }
        return null;
    }

    /// <summary>
    /// YAML front matter model for markdown files. Mirrors the canonical MeshNode
    /// properties (Name, Category, Icon, State, NodeType) plus the markdown-specific
    /// metadata captured on <see cref="MarkdownContent"/> (Authors, Tags, Thumbnail,
    /// Abstract). Accepts the legacy aliases <c>Title</c> (→ Name) and
    /// <c>Description</c> (→ Abstract) for files written before the field rename.
    /// </summary>
    private class MarkdownFrontMatter
    {
        // MeshNode standard properties
        public string? NodeType { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? Icon { get; set; }
        public string? State { get; set; }

        // MarkdownContent metadata
        public List<string>? Authors { get; set; }
        public List<string>? Tags { get; set; }
        public string? Thumbnail { get; set; }
        public string? Abstract { get; set; }

        // Legacy aliases. YamlDotNet doesn't follow C# property hierarchy, so we
        // expose them as plain settable properties and fold them in below
        // (NormalizeAliases) once deserialization completes. Pre-rename files
        // (Title / Description) keep working without a one-shot migration script.
        public string? Title { get; set; }
        public string? Description { get; set; }

        public void NormalizeAliases()
        {
            if (string.IsNullOrEmpty(Name) && !string.IsNullOrEmpty(Title))
                Name = Title;
            if (string.IsNullOrEmpty(Abstract) && !string.IsNullOrEmpty(Description))
                Abstract = Description;
        }
    }
}
