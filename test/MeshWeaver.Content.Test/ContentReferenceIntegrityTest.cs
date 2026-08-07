using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Content.Test;

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

    // Mirror of the embedded NodeTypeIcons collection (src/MeshWeaver.Graph/Icons/**.svg),
    // served at /static/NodeTypeIcons/. Keep in sync when adding an icon file there — a
    // reference to a name not present here (or not on disk) renders as a broken <img>.
    private static readonly HashSet<string> KnownNodeTypeIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        "bell.svg", "bot.svg", "box.svg", "building.svg", "chart.svg", "chat.svg",
        "checkmark.svg", "code.svg", "comment.svg", "database.svg", "document.svg",
        "folder.svg", "key.svg", "mail.svg", "meshweaver-logo.svg", "message.svg",
        "organization.svg", "people.svg", "person.svg", "rocket.svg", "satellite.svg",
        "settings.svg", "shield.svg", "shopping-bag.svg", "sparkle.svg", "task-list.svg",
        "truck.svg"
    };

    /// <summary>The ACCESS-CONTROLLED route every mesh-content URL must use (issue #587).</summary>
    private const string ContentRoutePrefix = "/api/content/";

    /// <summary>
    /// The ONLY <c>/static</c> prefixes that may survive: build assets shipped inside a MeshWeaver
    /// assembly (icon SVGs, the documentation package's images). Everything else on that route is
    /// mesh content and must move — <c>/static</c> performs no permission check at all.
    /// </summary>
    private static readonly string[] BuildAssetPrefixes =
        ["/static/NodeTypeIcons/", "/static/DocContent/"];

    /// <summary>
    /// What makes a <c>/static/…</c> occurrence a REFERENCE rather than prose: it is the value of a
    /// JSON property, a markdown link/image target, or an HTML attribute.
    /// </summary>
    private static readonly string[] UrlOpeners = ["\"", "](", "'", "=\""];

    /// <summary>
    /// Maps a content URL back to its path under <c>samples/Graph/content</c>, or <c>null</c> when
    /// it is not a content URL at all. Only the access-controlled route is accepted.
    /// </summary>
    private static string? TryMapToDisk(string url) =>
        url.StartsWith(ContentRoutePrefix, StringComparison.OrdinalIgnoreCase)
            ? url[ContentRoutePrefix.Length..]
            : null;

    #region The /static contract (issue #587)

    /// <summary>
    /// 🚨 THE DURABLE GUARD. <c>/static</c> serves application BUILD OUTPUT and nothing else: it
    /// resolves no identity and evaluates no permission, so anything reachable there is public to
    /// the entire internet. Mesh content — a Space's images, a partition's uploads, a node's
    /// thumbnail — must therefore be addressed through <c>/api/content/…</c>, where the owning
    /// node's hub gates the read.
    ///
    /// <para>This scans the sample data the product ships. It fails the moment a
    /// <c>/static/storage/content/…</c> (or any other non-build-asset <c>/static</c>) URL reappears
    /// in an icon, thumbnail or image field — which is exactly how the hole was introduced: the URL
    /// scheme was predictable, the route was unauthenticated, and nothing objected.</para>
    /// </summary>
    [Fact(Timeout = 20000)]
    public void MeshContentUrls_AreNeverAddressedThroughTheStaticRoute()
    {
        var offenders = new List<string>();
        foreach (var root in new[] { TestPaths.SamplesGraphData, TestPaths.SamplesGraphContent })
        {
            foreach (var filePath in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (Path.GetExtension(filePath) is not (".json" or ".md"))
                    continue;
                var relativePath = Path.GetRelativePath(root, filePath).Replace('\\', '/');
                // Generated satellites (compile releases, activity logs) are runtime artifacts.
                if (relativePath.Contains("/Release/", StringComparison.Ordinal)
                    || relativePath.StartsWith("Release/", StringComparison.Ordinal))
                    continue;

                foreach (var line in File.ReadLines(filePath))
                {
                    // URL-VALUED occurrences only: a JSON string value, a markdown link/image
                    // target, or an html src/href. Prose that merely mentions the route (a doc
                    // explaining this very rule) is not a reference and must not fail the build.
                    foreach (var opener in UrlOpeners)
                    {
                        var at = line.IndexOf(opener + "/static/", StringComparison.OrdinalIgnoreCase);
                        if (at < 0)
                            continue;
                        var url = line[(at + opener.Length)..];
                        if (BuildAssetPrefixes.Any(p => url.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        offenders.Add($"{relativePath}: {line.Trim()}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "/static applies NO access control, so mesh content addressed through it is world-readable "
            + "(issue #587). Use /api/content/{node}/{file} — the same bytes, gated on Read of the "
            + "owning node. Only build assets shipped in an assembly may stay on /static:\n"
            + string.Join("\n", offenders));
    }

    #endregion

    #region Markdown Thumbnails

    [Fact(Timeout = 20000)]
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
            var node = _parser.Parse(filePath, fileContent, relativePath);

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

    [Fact(Timeout = 20000)]
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
            var node = _parser.Parse(filePath, fileContent, relativePath);

            var thumbnail = (node?.Content as MarkdownContent)?.Thumbnail;
            if (string.IsNullOrEmpty(thumbnail))
                continue;

            // Skip absolute URLs — nothing to check on disk
            if (thumbnail.StartsWith("/") || thumbnail.StartsWith("http"))
                continue;

            // Simulate the runtime resolution:
            // /api/content/{namespace}/{thumbnail}  (MeshNodeThumbnailControl)
            // Map back to disk: content/{namespace}/{thumbnail}
            var ns = node!.Namespace;
            if (string.IsNullOrEmpty(ns))
                continue;

            var diskPath = Path.GetFullPath(Path.Combine(contentDir, ns, thumbnail));
            if (!File.Exists(diskPath))
            {
                broken.Add($"{relativePath} → Thumbnail: \"{thumbnail}\"\n  Runtime URL: /api/content/{ns}/{thumbnail}\n  (expected on disk at: {Path.GetRelativePath(contentDir, diskPath)})");
            }
        }

        // Assert
        broken.Should().BeEmpty(
            "all markdown thumbnail URLs (as resolved by MeshNodeThumbnailControl) should map to existing files:\n" +
            string.Join("\n", broken));
    }

    #endregion

    #region Markdown Node Icons

    [Fact(Timeout = 20000)]
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
            var node = _parser.Parse(filePath, fileContent, relativePath);

            var icon = node?.Icon;
            if (string.IsNullOrEmpty(icon))
                continue;

            // Skip Fluent UI icon names (no path separator)
            if (!icon.Contains('/'))
                continue;

            // Skip inline SVG icons (valid icon format, not a file path)
            if (icon.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
                continue;

            // Relative path — resolve using node namespace (same as GetImageUrlForNode at runtime)
            if (!icon.StartsWith(ContentRoutePrefix, StringComparison.OrdinalIgnoreCase)
                && !icon.StartsWith("/static/", StringComparison.OrdinalIgnoreCase))
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
                    // Top-level nodes (no namespace) — resolve icon directly against content dir
                    var diskPath = Path.GetFullPath(Path.Combine(contentDir, icon));
                    if (!File.Exists(diskPath))
                    {
                        broken.Add($"{relativePath} → Icon: \"{icon}\"\n  (relative path with no namespace cannot be resolved)");
                    }
                }
                continue;
            }

            // 🚨 Mesh content must be addressed through the ACCESS-CONTROLLED route, never
            // /static (issue #587) — see Icons_AreNeverAddressedThroughTheStaticRoute.
            var subPath = TryMapToDisk(icon);
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

    [Fact(Timeout = 20000)]
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

                if (TryMapToDisk(url) is { } contentSubPath)
                {
                    resolvedPath = Path.GetFullPath(Path.Combine(contentDir, contentSubPath));
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

    [Fact(Timeout = 20000)]
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

            // Skip generated satellite artifacts (NodeType compile releases,
            // activity logs, threads, …). These are produced at runtime — they
            // are not hand-authored sample content, and their image refs are
            // whatever the generator emitted, so validating them here is wrong
            // (and flaky: a compile test running alongside this one drops fresh
            // `Release/*.json` files into the sample tree).
            if (relativePath.Contains("/Release/", StringComparison.Ordinal)
                || relativePath.StartsWith("Release/", StringComparison.Ordinal))
                continue;

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
                if (!image.StartsWith("/static/", StringComparison.OrdinalIgnoreCase)
                    && !image.StartsWith(ContentRoutePrefix, StringComparison.OrdinalIgnoreCase))
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

                var subPath = TryMapToDisk(image);
                if (subPath == null)
                {
                    broken.Add($"{relativePath} → {property}: \"{image}\"\n  (unrecognized content path)");
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
