using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests to ensure ALL use case content and layout references are valid.
/// Dynamically discovers test cases by scanning the data directory.
/// </summary>
public partial class ContentIntegrityTest
{
    // Matches @@("path") with either escaped quotes (\") or raw quotes (")
    [GeneratedRegex(@"@@\(\\?""([^""\\]+)\\?""\)")]
    private static partial Regex LayoutAreaLinkRegex();

    #region Dynamic Test Case Discovery

    /// <summary>
    /// Discovers all ContentCatalog instances by scanning JSON files.
    /// Returns node paths derived from file system location (matching how FileSystemStorageAdapter works).
    /// For example, a file at "ACME/Northwind/Reports.json" yields path "ACME/Northwind/Reports".
    /// Returns a placeholder if no ContentCatalog nodes exist to prevent xUnit "No data found" error.
    /// </summary>
    public static IEnumerable<object[]> GetContentCatalogNodes()
    {
        var dataDir = TestPaths.SamplesGraphData;
        var jsonFiles = Directory.GetFiles(dataDir, "*.json", SearchOption.AllDirectories);
        var results = new List<object[]>();

        foreach (var file in jsonFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.StartsWith("_") || fileName == "package") continue;

            try
            {
                var content = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(content, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                // Skip arrays (data files like PropertyRisks.json)
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    continue;

                if (doc.RootElement.TryGetProperty("nodeType", out var nodeType))
                {
                    var nodeTypeValue = nodeType.GetString();
                    if (nodeTypeValue?.Contains("ContentCatalog", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        // Derive path from file system location (matching FileSystemStorageAdapter behavior)
                        var relativePath = Path.GetRelativePath(dataDir, file).Replace('\\', '/');
                        var nodePath = relativePath[..^5]; // Remove ".json" extension
                        results.Add(new object[] { nodePath });
                    }
                }
            }
            catch (JsonException)
            {
                // Skip malformed JSON files
            }
        }

        // Return placeholder to avoid xUnit "No data found" error when no ContentCatalog nodes exist
        if (results.Count == 0)
            results.Add(new object[] { "__SKIP__" });

        return results;
    }

    /// <summary>
    /// Discovers all JSON files containing @@ layout area references.
    /// Returns relative paths like "Northwind.json".
    /// Returns a placeholder if no files exist to prevent xUnit "No data found" error.
    /// </summary>
    public static IEnumerable<object[]> GetFilesWithLayoutAreaReferences()
    {
        var dataDir = TestPaths.SamplesGraphData;
        var jsonFiles = Directory.GetFiles(dataDir, "*.json", SearchOption.AllDirectories);
        var regex = LayoutAreaLinkRegex();
        var results = new List<object[]>();

        foreach (var file in jsonFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.StartsWith("_") || fileName == "package") continue;

            var content = File.ReadAllText(file);
            if (regex.IsMatch(content))
            {
                var relativePath = Path.GetRelativePath(dataDir, file).Replace('\\', '/');
                results.Add(new object[] { relativePath });
            }
        }

        // Return placeholder to avoid xUnit "No data found" error when no matching files exist
        if (results.Count == 0)
            results.Add(new object[] { "__SKIP__" });

        return results;
    }

    #endregion

    #region Layout Area Reference Tests

    [Theory(Timeout = 10000)]
    [MemberData(nameof(GetFilesWithLayoutAreaReferences))]
    public void Validate_LayoutAreaReferences_TargetExistingNodes(string jsonFilePath)
    {
        // Skip if no files with layout area references exist
        if (jsonFilePath == "__SKIP__")
            return;

        // Arrange
        var dataDir = TestPaths.SamplesGraphData;
        var fullPath = Path.Combine(dataDir, jsonFilePath.Replace('/', Path.DirectorySeparatorChar));
        var allNodePaths = GetAllNodePaths(dataDir);
        var brokenReferences = new List<string>();

        // Act
        var content = File.ReadAllText(fullPath);
        var regex = LayoutAreaLinkRegex();
        foreach (Match match in regex.Matches(content))
        {
            var path = match.Groups[1].Value;
            var segments = path.Split('/');
            if (segments.Length < 2) continue;

            // Try to find the matching node path by progressively checking prefixes
            // e.g., for "ACME/Northwind/Analytics/SalesReport", check:
            //   "ACME/Northwind/Analytics/SalesReport", "ACME/Northwind/Analytics", "ACME/Northwind", "ACME"
            var nodeFound = false;
            for (int i = segments.Length; i >= 2; i--)
            {
                var candidatePath = string.Join("/", segments.Take(i));
                if (allNodePaths.Contains(candidatePath))
                {
                    nodeFound = true;
                    break;
                }
            }

            if (!nodeFound)
            {
                brokenReferences.Add($"@@(\"{path}\") - no matching node found");
            }
        }

        // Assert
        brokenReferences.Should().BeEmpty(
            $"all @@ layout area references in {jsonFilePath} should target existing nodes:\n" +
            string.Join("\n", brokenReferences));
    }

    #endregion

    #region ContentCatalog Tests

    [Theory(Timeout = 10000)]
    [MemberData(nameof(GetContentCatalogNodes))]
    public void Validate_ContentCatalogNode_HasContentDirectory(string nodePath)
    {
        // Skip if no ContentCatalog nodes exist
        if (nodePath == "__SKIP__")
            return;

        // Arrange
        var contentDir = TestPaths.SamplesGraphContent;
        var expectedContentPath = Path.Combine(contentDir, nodePath.Replace('/', Path.DirectorySeparatorChar));

        // Act & Assert
        Directory.Exists(expectedContentPath).Should().BeTrue(
            $"Content directory should exist at: {expectedContentPath}");
    }

    [Theory(Timeout = 10000)]
    [MemberData(nameof(GetContentCatalogNodes))]
    public void Validate_ContentCatalogNode_HasContent(string nodePath)
    {
        // Skip if no ContentCatalog nodes exist
        if (nodePath == "__SKIP__")
            return;

        // Arrange
        var contentDir = TestPaths.SamplesGraphContent;
        var expectedContentPath = Path.Combine(contentDir, nodePath.Replace('/', Path.DirectorySeparatorChar));

        // Skip if directory doesn't exist (caught by separate test)
        if (!Directory.Exists(expectedContentPath))
            return;

        // Act - Search recursively for markdown files or any content files
        var mdFiles = Directory.GetFiles(expectedContentPath, "*.md", SearchOption.AllDirectories);
        var allFiles = Directory.GetFiles(expectedContentPath, "*.*", SearchOption.AllDirectories);

        // Assert - Either markdown files OR other content (images, svg, csv, etc.) should exist
        var hasContent = mdFiles.Length > 0 || allFiles.Length > 0;
        hasContent.Should().BeTrue(
            $"Content directory {expectedContentPath} should contain content files (markdown, images, etc.)");
    }

    #endregion

    #region Helpers

    private static HashSet<string> GetAllNodePaths(string dataDir)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jsonFiles = Directory.GetFiles(dataDir, "*.json", SearchOption.AllDirectories);

        foreach (var file in jsonFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.StartsWith("_") || fileName == "package") continue;

            var relativePath = Path.GetRelativePath(dataDir, file).Replace('\\', '/');
            var nodePath = relativePath[..^5]; // Remove ".json"
            paths.Add(nodePath);
        }

        return paths;
    }

    #endregion
}
