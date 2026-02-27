using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Validates that all image references in the Graph sample's content and data files
/// resolve to existing files on disk.
/// </summary>
public class ContentReferenceIntegrityTest
{
    private readonly MarkdownFileParser _parser = new();

    private static readonly MarkdownPipeline MarkdigPipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .Build();

    private static readonly HashSet<string> KnownNodeTypeIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        "bot.svg", "chat.svg", "code.svg", "comment.svg", "document.svg", "message.svg"
    };

    #region Markdown Thumbnails

    [Fact(Timeout = 10000)]
    public async Task Validate_MarkdownThumbnails_AllResolveToExistingFiles()
    {
        // Arrange
        var contentDir = TestPaths.SamplesGraphContent;
        var mdFiles = Directory.GetFiles(contentDir, "*.md", SearchOption.AllDirectories);
        var broken = new List<string>();

        // Act
        foreach (var filePath in mdFiles)
        {
            var relativePath = Path.GetRelativePath(contentDir, filePath).Replace('\\', '/');
            var fileContent = await File.ReadAllTextAsync(filePath);
            var node = await _parser.ParseAsync(filePath, fileContent, relativePath);

            var thumbnail = (node?.Content as MarkdownContent)?.Thumbnail;
            if (string.IsNullOrEmpty(thumbnail))
                continue;

            // Resolve relative to the .md file's directory
            var mdDir = Path.GetDirectoryName(filePath)!;
            var resolvedPath = Path.GetFullPath(Path.Combine(mdDir, thumbnail));

            if (!File.Exists(resolvedPath))
            {
                broken.Add($"{relativePath} → Thumbnail: \"{thumbnail}\"\n  (expected at: {Path.GetRelativePath(contentDir, resolvedPath)})");
            }
        }

        // Assert
        broken.Should().BeEmpty(
            "all markdown thumbnail references should resolve to existing files:\n" +
            string.Join("\n", broken));
    }

    [Fact(Timeout = 10000)]
    public async Task Validate_MarkdownThumbnailUrls_ResolveToExistingFiles()
    {
        // Arrange — verify that thumbnail paths, when resolved the same way
        // MeshNodeThumbnailControl.GetImageUrl() does it, point to real files.
        var contentDir = TestPaths.SamplesGraphContent;
        var mdFiles = Directory.GetFiles(contentDir, "*.md", SearchOption.AllDirectories);
        var broken = new List<string>();

        // Act
        foreach (var filePath in mdFiles)
        {
            var relativePath = Path.GetRelativePath(contentDir, filePath).Replace('\\', '/');
            var fileContent = await File.ReadAllTextAsync(filePath);
            var node = await _parser.ParseAsync(filePath, fileContent, relativePath);

            var thumbnail = (node?.Content as MarkdownContent)?.Thumbnail;
            if (string.IsNullOrEmpty(thumbnail))
                continue;

            // Skip absolute URLs — nothing to check on disk
            if (thumbnail.StartsWith("/") || thumbnail.StartsWith("http"))
                continue;

            // Simulate the runtime resolution:
            // /static/storage/content/{namespace}/{thumbnail}
            // Map back to disk: content/{namespace}/{thumbnail}
            var ns = node!.Namespace;
            if (string.IsNullOrEmpty(ns))
                continue;

            var diskPath = Path.GetFullPath(Path.Combine(contentDir, ns, thumbnail));
            if (!File.Exists(diskPath))
            {
                broken.Add($"{relativePath} → Thumbnail: \"{thumbnail}\"\n  Runtime URL: /static/storage/content/{ns}/{thumbnail}\n  (expected on disk at: {Path.GetRelativePath(contentDir, diskPath)})");
            }
        }

        // Assert
        broken.Should().BeEmpty(
            "all markdown thumbnail URLs (as resolved by MeshNodeThumbnailControl) should map to existing files:\n" +
            string.Join("\n", broken));
    }

    #endregion

    #region Markdown Node Icons

    [Fact(Timeout = 10000)]
    public async Task Validate_MarkdownNodeIcons_AreValidReferences()
    {
        // Arrange
        var contentDir = TestPaths.SamplesGraphContent;
        var dataDir = TestPaths.SamplesGraphData;
        var mdFiles = Directory.GetFiles(contentDir, "*.md", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(dataDir, "*.md", SearchOption.AllDirectories))
            .ToArray();
        var broken = new List<string>();

        // Act
        foreach (var filePath in mdFiles)
        {
            var baseDir = filePath.StartsWith(contentDir) ? contentDir : dataDir;
            var relativePath = Path.GetRelativePath(baseDir, filePath).Replace('\\', '/');
            var fileContent = await File.ReadAllTextAsync(filePath);
            var node = await _parser.ParseAsync(filePath, fileContent, relativePath);

            var icon = node?.Icon;
            if (string.IsNullOrEmpty(icon))
                continue;

            // Skip Fluent UI icon names (no path separator)
            if (!icon.Contains('/'))
                continue;

            // Relative path — resolve using node namespace (same as GetImageUrlForNode at runtime)
            if (!icon.StartsWith("/static/", StringComparison.OrdinalIgnoreCase))
            {
                var ns = node!.Namespace;
                if (!string.IsNullOrEmpty(ns))
                {
                    var diskPath = Path.GetFullPath(Path.Combine(contentDir, ns, icon));
                    if (!File.Exists(diskPath))
                    {
                        broken.Add($"{relativePath} → Icon: \"{icon}\"\n  (resolved to content/{ns}/{icon} but file not found)");
                    }
                }
                else
                {
                    broken.Add($"{relativePath} → Icon: \"{icon}\"\n  (relative path with no namespace cannot be resolved)");
                }
                continue;
            }

            // Resolve /static/storage/content/X or /static/content/X → content/X
            string? subPath = null;
            if (icon.StartsWith("/static/storage/content/", StringComparison.OrdinalIgnoreCase))
                subPath = icon["/static/storage/content/".Length..];
            else if (icon.StartsWith("/static/content/", StringComparison.OrdinalIgnoreCase))
                subPath = icon["/static/content/".Length..];

            if (subPath != null)
            {
                var resolvedPath = Path.GetFullPath(Path.Combine(contentDir, subPath));
                if (!File.Exists(resolvedPath))
                {
                    broken.Add($"{relativePath} → Icon: \"{icon}\"\n  (expected at: content/{subPath})");
                }
            }
        }

        // Assert
        broken.Should().BeEmpty(
            "all markdown node icons that are paths should resolve to existing files:\n" +
            string.Join("\n", broken));
    }

    #endregion

    #region Inline Image References

    [Fact(Timeout = 10000)]
    public async Task Validate_InlineImageReferences_AllResolveToExistingFiles()
    {
        // Arrange
        var contentDir = TestPaths.SamplesGraphContent;
        var dataDir = TestPaths.SamplesGraphData;

        var mdFiles = Directory.GetFiles(contentDir, "*.md", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(dataDir, "*.md", SearchOption.AllDirectories))
            .ToArray();

        var broken = new List<string>();

        // Act
        foreach (var filePath in mdFiles)
        {
            var fileContent = await File.ReadAllTextAsync(filePath);
            var document = Markdig.Markdown.Parse(fileContent, MarkdigPipeline);

            var baseDir = filePath.StartsWith(contentDir) ? contentDir : dataDir;
            var relativePath = Path.GetRelativePath(baseDir, filePath).Replace('\\', '/');

            foreach (var link in document.Descendants<LinkInline>())
            {
                if (!link.IsImage || string.IsNullOrEmpty(link.Url))
                    continue;

                var url = link.Url;

                // Skip external URLs
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;

                string resolvedPath;

                if (url.StartsWith("/static/storage/content/", StringComparison.OrdinalIgnoreCase))
                {
                    // Map /static/storage/content/X → content/X
                    var subPath = url["/static/storage/content/".Length..];
                    resolvedPath = Path.GetFullPath(Path.Combine(contentDir, subPath));
                }
                else if (url.StartsWith("/static/content/", StringComparison.OrdinalIgnoreCase))
                {
                    // Map /static/content/X → content/X
                    var subPath = url["/static/content/".Length..];
                    resolvedPath = Path.GetFullPath(Path.Combine(contentDir, subPath));
                }
                else
                {
                    // Relative path → resolve from .md file's directory
                    var mdDir = Path.GetDirectoryName(filePath)!;
                    resolvedPath = Path.GetFullPath(Path.Combine(mdDir, url));
                }

                if (!File.Exists(resolvedPath))
                {
                    broken.Add($"{relativePath} → image: \"{url}\"\n  (expected at: {resolvedPath})");
                }
            }
        }

        // Assert
        broken.Should().BeEmpty(
            "all inline image references should resolve to existing files:\n" +
            string.Join("\n", broken));
    }

    #endregion

    #region JSON Node Icons

    [Fact(Timeout = 10000)]
    public void Validate_JsonNodeImages_AllResolveToExistingFiles()
    {
        // Arrange
        var dataDir = TestPaths.SamplesGraphData;
        var contentDir = TestPaths.SamplesGraphContent;
        var jsonFiles = Directory.GetFiles(dataDir, "*.json", SearchOption.AllDirectories);
        var broken = new List<string>();

        // Act
        foreach (var filePath in jsonFiles)
        {
            var relativePath = Path.GetRelativePath(dataDir, filePath).Replace('\\', '/');
            var fileContent = File.ReadAllText(filePath);

            using var doc = JsonDocument.Parse(fileContent, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var images = new List<(string Property, string Value)>();
            ExtractImageValues(doc.RootElement, images);

            foreach (var (property, image) in images)
            {
                // Skip non-path values (Fluent UI icon names like "Organization")
                if (!image.StartsWith("/static/", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip embedded NodeTypeIcons — validate against known set
                if (image.StartsWith("/static/NodeTypeIcons/", StringComparison.OrdinalIgnoreCase))
                {
                    var iconFileName = image["/static/NodeTypeIcons/".Length..];
                    if (!KnownNodeTypeIcons.Contains(iconFileName))
                    {
                        broken.Add($"{relativePath} → {property}: \"{image}\"\n  (unknown embedded NodeTypeIcon: {iconFileName})");
                    }
                    continue;
                }

                // Map /static/storage/content/X or /static/content/X → content/X
                string? subPath = null;
                if (image.StartsWith("/static/storage/content/", StringComparison.OrdinalIgnoreCase))
                    subPath = image["/static/storage/content/".Length..];
                else if (image.StartsWith("/static/content/", StringComparison.OrdinalIgnoreCase))
                    subPath = image["/static/content/".Length..];

                if (subPath == null)
                {
                    // Unknown /static/ prefix
                    broken.Add($"{relativePath} → {property}: \"{image}\"\n  (unrecognized /static/ path)");
                    continue;
                }

                var resolvedPath = Path.GetFullPath(Path.Combine(contentDir, subPath));
                if (!File.Exists(resolvedPath))
                {
                    broken.Add($"{relativePath} → {property}: \"{image}\"\n  (expected at: content/{subPath})");
                }
            }
        }

        // Assert
        broken.Should().BeEmpty(
            "all JSON image references should resolve to existing files:\n" +
            string.Join("\n", broken));
    }

    private static readonly HashSet<string> ImagePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "icon", "logo", "avatar", "thumbnail"
    };

    private static void ExtractImageValues(JsonElement element, List<(string Property, string Value)> images)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ImagePropertyNames.Contains(property.Name) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrEmpty(value))
                            images.Add((property.Name, value));
                    }
                    else
                    {
                        ExtractImageValues(property.Value, images);
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ExtractImageValues(item, images);
                }
                break;
        }
    }

    #endregion
}
