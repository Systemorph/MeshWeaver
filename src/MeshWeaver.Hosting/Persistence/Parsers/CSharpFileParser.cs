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
    /// <inheritdoc />
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

    /// <summary>
    /// Parses .cs file content into a <c>CodeConfiguration</c>, stripping any leading
    /// <c>&lt;meshweaver&gt;</c> metadata comment block so only the C# code remains.
    /// </summary>
    /// <param name="filePath">Full path to the source file (used for context only).</param>
    /// <param name="content">Raw .cs file content, optionally prefixed with a metadata block.</param>
    /// <returns>A <c>CodeConfiguration</c> with the cleaned code, or null if it cannot be parsed.</returns>
    public CodeConfiguration? ParseCodeConfiguration(string filePath, string content)
    {
        var codeWithoutMetadata = RemoveMetadataBlock(content);

        return new CodeConfiguration
        {
            Code = codeWithoutMetadata.Trim(),
            Language = "csharp"
        };
    }

    /// <inheritdoc />
    public MeshNode? Parse(string filePath, string content, string relativePath)
    {
        // C# files are typically loaded as partition objects (CodeConfiguration), not as MeshNodes
        // This method is here for completeness but may not be commonly used
        var (id, ns) = DeriveIdAndNamespace(relativePath, filePath);

        var metadata = ExtractMetadata(content);
        var codeWithoutMetadata = RemoveMetadataBlock(content);

        var codeConfig = new CodeConfiguration
        {
            Code = codeWithoutMetadata.Trim(),
            Language = "csharp"
        };

        var displayName = metadata.GetValueOrDefault("DisplayName");
        var nodeId = metadata.GetValueOrDefault("Id") ?? ExtractPrimaryTypeName(content) ?? id;
        // A `.cs` source defaults to a "Code" node, but the `<meshweaver>` header may declare a
        // different type — e.g. `// NodeType: Scope` makes it a first-class BusinessRules Scope
        // node (still CodeConfiguration content, still folded into the parent compile).
        var nodeType = metadata.GetValueOrDefault("NodeType") ?? "Code";

        var fileInfo = new FileInfo(filePath);
        var lastModified = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);

        var node = new MeshNode(nodeId, ns)
        {
            NodeType = nodeType,
            Name = displayName ?? nodeId,
            LastModified = lastModified,
            Content = codeConfig
        };

        return node;
    }

    /// <inheritdoc />
    public string Serialize(MeshNode node)
    {
        if (node.Content is not CodeConfiguration codeConfig)
            throw new InvalidOperationException("Cannot serialize node without CodeConfiguration content");

        return SerializeCodeConfiguration(codeConfig);
    }

    /// <summary>
    /// Serializes a CodeConfiguration to a .cs file content string.
    /// </summary>
    public static string SerializeCodeConfiguration(CodeConfiguration config)
    {
        var sb = new StringBuilder();

        // Write metadata block if there's meaningful metadata
        // No metadata block needed — Id and DisplayName come from the MeshNode wrapper

        // Append the code
        if (!string.IsNullOrEmpty(config.Code))
        {
            sb.Append(config.Code);
        }

        return sb.ToString();
    }

    /// <inheritdoc />
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

    internal static string? ExtractPrimaryTypeName(string content)
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

        // index.cs represents the parent directory node, not a child called "index"
        if (id.Equals("index", StringComparison.OrdinalIgnoreCase))
        {
            var parentSlash = ns.LastIndexOf('/');
            if (parentSlash < 0)
                return (ns, null);
            return (ns[(parentSlash + 1)..], ns[..parentSlash]);
        }

        return (id, ns);
    }
}
