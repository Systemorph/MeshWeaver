using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Utility for migrating JSON files to native formats (.md and .cs).
/// </summary>
public partial class MigrationUtility
{
    private readonly string _dataDirectory;
    private readonly string _contentDirectory;
    private readonly IconExtractor _iconExtractor;
    private readonly MarkdownFileParser _markdownParser = new();
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new MigrationUtility.
    /// </summary>
    /// <param name="dataDirectory">Directory containing JSON files (e.g., samples/Graph/Data).</param>
    /// <param name="contentDirectory">Directory for extracted icons (e.g., samples/Graph/content).</param>
    /// <param name="jsonOptions">JSON serializer options from the hub.</param>
    public MigrationUtility(string dataDirectory, string contentDirectory, JsonSerializerOptions jsonOptions)
    {
        _dataDirectory = dataDirectory;
        _contentDirectory = contentDirectory;
        _iconExtractor = new IconExtractor(contentDirectory);
        _jsonOptions = jsonOptions;
    }

    // Regex to extract class/record name from C# code
    [GeneratedRegex(@"(?:public|internal|private|protected)?\s*(?:partial\s+)?(?:static\s+)?(?:abstract\s+)?(?:sealed\s+)?(?:class|record|struct|interface)\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex TypeNameRegex();

    /// <summary>
    /// Migrates all JSON files in the data directory to native formats.
    /// </summary>
    /// <param name="dryRun">If true, only reports what would be done without making changes.</param>
    /// <returns>Migration report.</returns>
    public async Task<MigrationReport> MigrateAllAsync(bool dryRun = false)
    {
        var report = new MigrationReport();

        // Find all JSON files
        var jsonFiles = Directory.GetFiles(_dataDirectory, "*.json", SearchOption.AllDirectories);

        foreach (var jsonFile in jsonFiles)
        {
            try
            {
                var result = await MigrateFileAsync(jsonFile, dryRun);
                report.Results.Add(result);
            }
            catch (Exception ex)
            {
                report.Results.Add(new MigrationResult
                {
                    SourceFile = jsonFile,
                    Status = MigrationStatus.Failed,
                    Error = ex.Message
                });
            }
        }

        return report;
    }

    /// <summary>
    /// Migrates a single JSON file.
    /// </summary>
    public async Task<MigrationResult> MigrateFileAsync(string jsonFile, bool dryRun = false)
    {
        var result = new MigrationResult { SourceFile = jsonFile };

        // Read and parse JSON
        var json = await File.ReadAllTextAsync(jsonFile);

        // Check if it's a CodeConfiguration (in Code directory)
        var relativePath = Path.GetRelativePath(_dataDirectory, jsonFile);
        var isCodeFile = relativePath.Contains($"{Path.DirectorySeparatorChar}Code{Path.DirectorySeparatorChar}") ||
                        relativePath.Contains("/Code/");

        if (isCodeFile && json.Contains("\"$type\"") && json.Contains("CodeConfiguration"))
        {
            return await MigrateCodeFileAsync(jsonFile, json, dryRun);
        }

        // Try to parse as MeshNode
        MeshNode? node;
        try
        {
            node = JsonSerializer.Deserialize<MeshNode>(json, _jsonOptions);
        }
        catch
        {
            result.Status = MigrationStatus.Skipped;
            result.Reason = "Not a valid MeshNode JSON";
            return result;
        }

        if (node == null)
        {
            result.Status = MigrationStatus.Skipped;
            result.Reason = "Null node";
            return result;
        }

        // Check if it's a Markdown node
        if (node.NodeType == "Markdown" || IsMarkdownContent(node))
        {
            return await MigrateMarkdownNodeAsync(jsonFile, node, json, dryRun);
        }

        // Not a file type we migrate
        result.Status = MigrationStatus.Skipped;
        result.Reason = $"NodeType '{node.NodeType}' is not migrated";
        return result;
    }

    private async Task<MigrationResult> MigrateCodeFileAsync(string jsonFile, string json, bool dryRun)
    {
        var result = new MigrationResult { SourceFile = jsonFile };

        // Parse the CodeConfiguration
        CodeConfiguration? config;
        try
        {
            // The JSON has $type discriminator, so we need to handle it
            var doc = JsonDocument.Parse(json);
            var code = doc.RootElement.GetProperty("code").GetString();
            var displayName = doc.RootElement.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;

            if (string.IsNullOrEmpty(code))
            {
                result.Status = MigrationStatus.Skipped;
                result.Reason = "Empty code content";
                return result;
            }

            // Extract the primary type name for the filename
            var typeName = ExtractPrimaryTypeName(code);
            if (string.IsNullOrEmpty(typeName))
            {
                // Fall back to original filename without extension
                typeName = Path.GetFileNameWithoutExtension(jsonFile);
            }

            config = new CodeConfiguration
            {
                Id = typeName,
                Code = code,
                DisplayName = displayName,
                Language = "csharp"
            };
        }
        catch (Exception ex)
        {
            result.Status = MigrationStatus.Failed;
            result.Error = $"Failed to parse CodeConfiguration: {ex.Message}";
            return result;
        }

        // Determine output file name
        var directory = Path.GetDirectoryName(jsonFile)!;
        var outputFileName = config.Id + ".cs";
        var outputPath = Path.Combine(directory, outputFileName);

        result.TargetFile = outputPath;
        result.MigrationType = "CodeConfiguration -> .cs";

        if (dryRun)
        {
            result.Status = MigrationStatus.WouldMigrate;
            return result;
        }

        // Write the .cs file
        var csContent = CSharpFileParser.SerializeCodeConfiguration(config);
        await File.WriteAllTextAsync(outputPath, csContent);

        // Backup and delete original JSON
        var backupPath = jsonFile + ".bak";
        File.Move(jsonFile, backupPath, overwrite: true);

        result.Status = MigrationStatus.Migrated;
        result.BackupFile = backupPath;
        return result;
    }

    private async Task<MigrationResult> MigrateMarkdownNodeAsync(string jsonFile, MeshNode node, string json, bool dryRun)
    {
        var result = new MigrationResult { SourceFile = jsonFile };

        // Extract markdown content
        var markdownContent = ExtractMarkdownContent(node, json);
        if (string.IsNullOrEmpty(markdownContent))
        {
            result.Status = MigrationStatus.Skipped;
            result.Reason = "No markdown content found";
            return result;
        }

        // Extract icon if it's a data URI
        var icon = node.Icon ?? ExtractIconFromContent(json);
        string? extractedIconPath = null;
        if (!dryRun && IconExtractor.IsDataUri(icon))
        {
            var nodePath = Path.GetRelativePath(_dataDirectory, Path.GetDirectoryName(jsonFile)!)
                .Replace(Path.DirectorySeparatorChar, '/');
            var nodeId = node.Id ?? Path.GetFileNameWithoutExtension(jsonFile);
            var fullNodePath = string.IsNullOrEmpty(nodePath) || nodePath == "."
                ? nodeId
                : $"{nodePath}/{nodeId}";

            extractedIconPath = await _iconExtractor.ExtractAndSaveIconAsync(icon, fullNodePath);
        }

        // Build the new node for serialization
        var migratedNode = node with
        {
            Icon = extractedIconPath ?? (IconExtractor.IsDataUri(icon) ? null : icon),
            Content = markdownContent
        };

        // Determine output path
        var outputPath = Path.ChangeExtension(jsonFile, ".md");
        result.TargetFile = outputPath;
        result.MigrationType = "Markdown JSON -> .md";

        if (dryRun)
        {
            result.Status = MigrationStatus.WouldMigrate;
            return result;
        }

        // Serialize to markdown format
        var mdContent = await _markdownParser.SerializeAsync(migratedNode);
        await File.WriteAllTextAsync(outputPath, mdContent);

        // Backup and delete original JSON
        var backupPath = jsonFile + ".bak";
        File.Move(jsonFile, backupPath, overwrite: true);

        result.Status = MigrationStatus.Migrated;
        result.BackupFile = backupPath;
        if (extractedIconPath != null)
        {
            result.ExtractedIcon = extractedIconPath;
        }

        return result;
    }

    private static bool IsMarkdownContent(MeshNode node)
    {
        // Check if content looks like MarkdownDocument
        if (node.Content is JsonElement element)
        {
            if (element.TryGetProperty("$type", out var typeElem))
            {
                var typeName = typeElem.GetString();
                return typeName?.Contains("Markdown", StringComparison.OrdinalIgnoreCase) == true;
            }
            if (element.TryGetProperty("content", out _))
            {
                // Has a content property, might be markdown
                return true;
            }
        }
        return false;
    }

    private static string? ExtractMarkdownContent(MeshNode node, string json)
    {
        // Try to extract from Content property
        if (node.Content is JsonElement element)
        {
            if (element.TryGetProperty("content", out var contentElem))
            {
                return contentElem.GetString();
            }
        }

        // Try parsing the full JSON for nested content
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("content", out var contentObj))
            {
                if (contentObj.ValueKind == JsonValueKind.Object &&
                    contentObj.TryGetProperty("content", out var nestedContent))
                {
                    return nestedContent.GetString();
                }
                if (contentObj.ValueKind == JsonValueKind.String)
                {
                    return contentObj.GetString();
                }
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }

    private static string? ExtractIconFromContent(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);

            // Check root level icon
            if (doc.RootElement.TryGetProperty("icon", out var iconElem))
            {
                return iconElem.GetString();
            }

            // Check content.logo or content.iconName
            if (doc.RootElement.TryGetProperty("content", out var contentObj) &&
                contentObj.ValueKind == JsonValueKind.Object)
            {
                if (contentObj.TryGetProperty("logo", out var logoElem))
                {
                    return logoElem.GetString();
                }
                if (contentObj.TryGetProperty("iconName", out var iconNameElem))
                {
                    return iconNameElem.GetString();
                }
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }

    private static string? ExtractPrimaryTypeName(string code)
    {
        var match = TypeNameRegex().Match(code);
        return match.Success ? match.Groups[1].Value : null;
    }
}

/// <summary>
/// Report from migration process.
/// </summary>
public class MigrationReport
{
    public List<MigrationResult> Results { get; } = new();

    public int TotalFiles => Results.Count;
    public int MigratedCount => Results.Count(r => r.Status == MigrationStatus.Migrated);
    public int SkippedCount => Results.Count(r => r.Status == MigrationStatus.Skipped);
    public int FailedCount => Results.Count(r => r.Status == MigrationStatus.Failed);
    public int WouldMigrateCount => Results.Count(r => r.Status == MigrationStatus.WouldMigrate);

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Migration Report");
        sb.AppendLine("================");
        sb.AppendLine($"Total files: {TotalFiles}");
        sb.AppendLine($"Migrated: {MigratedCount}");
        sb.AppendLine($"Skipped: {SkippedCount}");
        sb.AppendLine($"Failed: {FailedCount}");
        if (WouldMigrateCount > 0)
            sb.AppendLine($"Would migrate (dry run): {WouldMigrateCount}");

        if (FailedCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failures:");
            foreach (var r in Results.Where(r => r.Status == MigrationStatus.Failed))
            {
                sb.AppendLine($"  {r.SourceFile}: {r.Error}");
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Result of migrating a single file.
/// </summary>
public class MigrationResult
{
    public required string SourceFile { get; init; }
    public string? TargetFile { get; set; }
    public string? BackupFile { get; set; }
    public string? ExtractedIcon { get; set; }
    public MigrationStatus Status { get; set; }
    public string? MigrationType { get; set; }
    public string? Reason { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Status of a migration operation.
/// </summary>
public enum MigrationStatus
{
    /// <summary>File was successfully migrated.</summary>
    Migrated,
    /// <summary>File was skipped (not applicable for migration).</summary>
    Skipped,
    /// <summary>Migration failed with an error.</summary>
    Failed,
    /// <summary>Dry run - file would be migrated.</summary>
    WouldMigrate
}
