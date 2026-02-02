using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting.Persistence.Parsers;

/// <summary>
/// Parses .cs files with meshweaver metadata comment blocks into CodeConfiguration objects.
/// The metadata is stored in a comment block to keep the file as valid C#:
///
/// // &lt;meshweaver&gt;
/// // Id: Person
/// // DisplayName: Person Data Model
/// // &lt;/meshweaver&gt;
/// </summary>
public partial class CSharpFileParser : IFileFormatParser
{
    public IReadOnlyList<string> SupportedExtensions => [".cs"];

    // Regex to extract meshweaver metadata block
    [GeneratedRegex(@"^//\s*<meshweaver>\s*\r?\n((?://.*\r?\n)*?)//\s*</meshweaver>", RegexOptions.Multiline)]
    private static partial Regex MetadataBlockRegex();

    // Regex to extract a single property from the metadata block
    [GeneratedRegex(@"^//\s*(\w+):\s*(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex PropertyRegex();

    // Regex to extract the primary class/record/struct/interface name
    [GeneratedRegex(@"(?:public|internal|private|protected)?\s*(?:partial\s+)?(?:static\s+)?(?:abstract\s+)?(?:sealed\s+)?(?:class|record|struct|interface)\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex TypeNameRegex();

    public Task<CodeConfiguration?> ParseCodeConfigurationAsync(string filePath, string content, CancellationToken ct = default)
    {
        var metadata = ExtractMetadata(content);
        var codeWithoutMetadata = RemoveMetadataBlock(content);

        // Extract primary type name if Id not specified
        var id = metadata.GetValueOrDefault("Id") ?? ExtractPrimaryTypeName(content) ?? Path.GetFileNameWithoutExtension(filePath);

        var config = new CodeConfiguration
        {
            Id = id,
            Code = codeWithoutMetadata.Trim(),
            Language = "csharp",
            DisplayName = metadata.GetValueOrDefault("DisplayName")
        };

        return Task.FromResult<CodeConfiguration?>(config);
    }

    public Task<MeshNode?> ParseAsync(string filePath, string content, string relativePath, CancellationToken ct = default)
    {
        // C# files are typically loaded as partition objects (CodeConfiguration), not as MeshNodes
        // This method is here for completeness but may not be commonly used
        var (id, ns) = DeriveIdAndNamespace(relativePath, filePath);

        var metadata = ExtractMetadata(content);
        var codeWithoutMetadata = RemoveMetadataBlock(content);

        var codeConfig = new CodeConfiguration
        {
            Id = metadata.GetValueOrDefault("Id") ?? ExtractPrimaryTypeName(content) ?? id,
            Code = codeWithoutMetadata.Trim(),
            Language = "csharp",
            DisplayName = metadata.GetValueOrDefault("DisplayName")
        };

        var fileInfo = new FileInfo(filePath);
        var lastModified = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);

        var node = new MeshNode(id, ns)
        {
            NodeType = "Code",
            Name = codeConfig.DisplayName ?? codeConfig.Id,
            LastModified = lastModified,
            Content = codeConfig
        };

        return Task.FromResult<MeshNode?>(node);
    }

    public Task<string> SerializeAsync(MeshNode node, CancellationToken ct = default)
    {
        if (node.Content is not CodeConfiguration codeConfig)
            throw new InvalidOperationException("Cannot serialize node without CodeConfiguration content");

        return Task.FromResult(SerializeCodeConfiguration(codeConfig));
    }

    /// <summary>
    /// Serializes a CodeConfiguration to a .cs file content string.
    /// </summary>
    public static string SerializeCodeConfiguration(CodeConfiguration config)
    {
        var sb = new StringBuilder();

        // Write metadata block if there's meaningful metadata
        var hasMetadata = !string.IsNullOrEmpty(config.DisplayName);

        if (hasMetadata)
        {
            sb.AppendLine("// <meshweaver>");
            if (!string.IsNullOrEmpty(config.Id))
                sb.AppendLine($"// Id: {config.Id}");
            if (!string.IsNullOrEmpty(config.DisplayName))
                sb.AppendLine($"// DisplayName: {config.DisplayName}");
            sb.AppendLine("// </meshweaver>");
            sb.AppendLine();
        }

        // Append the code
        if (!string.IsNullOrEmpty(config.Code))
        {
            sb.Append(config.Code);
        }

        return sb.ToString();
    }

    public bool CanSerialize(MeshNode node)
    {
        return node.Content is CodeConfiguration;
    }

    private static Dictionary<string, string> ExtractMetadata(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var blockMatch = MetadataBlockRegex().Match(content);
        if (!blockMatch.Success)
            return result;

        var blockContent = blockMatch.Groups[1].Value;
        foreach (Match propMatch in PropertyRegex().Matches(blockContent))
        {
            var key = propMatch.Groups[1].Value;
            var value = propMatch.Groups[2].Value;
            result[key] = value;
        }

        return result;
    }

    private static string RemoveMetadataBlock(string content)
    {
        // Remove the meshweaver block and any trailing newlines after it
        var result = MetadataBlockRegex().Replace(content, "");
        return result.TrimStart('\r', '\n');
    }

    private static string? ExtractPrimaryTypeName(string content)
    {
        var match = TypeNameRegex().Match(content);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static (string Id, string? Namespace) DeriveIdAndNamespace(string relativePath, string filePath)
    {
        var pathWithoutExt = relativePath;
        if (pathWithoutExt.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            pathWithoutExt = pathWithoutExt[..^3];

        pathWithoutExt = pathWithoutExt.Trim('/').Replace('\\', '/');

        var lastSlash = pathWithoutExt.LastIndexOf('/');
        if (lastSlash < 0)
            return (pathWithoutExt, null);

        var ns = pathWithoutExt[..lastSlash];
        var id = pathWithoutExt[(lastSlash + 1)..];
        return (id, ns);
    }
}
