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
    /// Returns node paths like "Northwind/Articles", "Northwind/Reports".
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

                if (doc.RootElement.TryGetProperty("nodeType", out var nodeType))
                {
                    var nodeTypeValue = nodeType.GetString();
                    if (nodeTypeValue?.Contains("ContentCatalog", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        var ns = doc.RootElement.TryGetProperty("namespace", out var nsElem)
                            ? nsElem.GetString() : null;
                        var id = doc.RootElement.TryGetProperty("id", out var idElem)
                            ? idElem.GetString() : null;

                        if (!string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(id))
                            results.Add(new object[] { $"{ns}/{id}" });
                    }
                }
            }
            catch (JsonException)
            {
                // Skip malformed JSON files
            }
        }

        return results;
    }

    /// <summary>
    /// Discovers all JSON files containing @@ layout area references.
    /// Returns relative paths like "Northwind.json".
    /// </summary>
    public static IEnumerable<object[]> GetFilesWithLayoutAreaReferences()
    {
        var dataDir = TestPaths.SamplesGraphData;
        var jsonFiles = Directory.GetFiles(dataDir, "*.json", SearchOption.AllDirectories);
        var regex = LayoutAreaLinkRegex();

        foreach (var file in jsonFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.StartsWith("_") || fileName == "package") continue;

            var content = File.ReadAllText(file);
            if (regex.IsMatch(content))
            {
                var relativePath = Path.GetRelativePath(dataDir, file).Replace('\\', '/');
                yield return new object[] { relativePath };
            }
        }
    }

    #endregion

    #region Layout Area Reference Tests

    [Theory(Timeout = 10000)]
    [MemberData(nameof(GetFilesWithLayoutAreaReferences))]
    public void Validate_LayoutAreaReferences_TargetExistingNodes(string jsonFilePath)
    {
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

            // Node path is first two segments (e.g., "Northwind/Analytics")
            var nodePath = string.Join("/", segments.Take(2));
            if (!allNodePaths.Contains(nodePath))
            {
                brokenReferences.Add($"@@(\"{path}\") - node not found: {nodePath}");
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
        // Arrange
        var contentDir = TestPaths.SamplesGraphContent;
        var expectedContentPath = Path.Combine(contentDir, nodePath.Replace('/', Path.DirectorySeparatorChar));

        // Act & Assert
        Directory.Exists(expectedContentPath).Should().BeTrue(
            $"Content directory should exist at: {expectedContentPath}");
    }

    [Theory(Timeout = 10000)]
    [MemberData(nameof(GetContentCatalogNodes))]
    public void Validate_ContentCatalogNode_HasMarkdownFiles(string nodePath)
    {
        // Arrange
        var contentDir = TestPaths.SamplesGraphContent;
        var expectedContentPath = Path.Combine(contentDir, nodePath.Replace('/', Path.DirectorySeparatorChar));

        // Skip if directory doesn't exist (caught by separate test)
        if (!Directory.Exists(expectedContentPath))
            return;

        // Act
        var mdFiles = Directory.GetFiles(expectedContentPath, "*.md");

        // Assert
        mdFiles.Should().NotBeEmpty(
            $"Content directory {expectedContentPath} should contain markdown files");
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
