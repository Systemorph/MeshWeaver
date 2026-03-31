using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Represents a compiled release of a NodeType.
/// Contains all compilation inputs and metadata for the release.
/// Immutable once created - the Path uniquely identifies the compiled output.
/// </summary>
public record NodeTypeRelease
{
    /// <summary>
    /// The unique path for this release in format "{nodeTypePath}@{releaseHash}".
    /// Example: "Type/Organization@abc123def456"
    /// </summary>
    [Key]
    public required string Path { get; init; }

    /// <summary>
    /// The node type path that was compiled (e.g., "Type/Organization").
    /// </summary>
    public required string NodeTypePath { get; init; }

    /// <summary>
    /// The release hash portion of the Path.
    /// Computed from all compilation inputs.
    /// </summary>
    public required string Release { get; init; }

    /// <summary>
    /// The C# source code from CodeConfiguration.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// The HubConfiguration source code from NodeTypeDefinition.Configuration.
    /// </summary>
    public string? HubConfiguration { get; init; }

    /// <summary>
    /// Content collections configuration from NodeTypeDefinition.
    /// </summary>
    public IReadOnlyList<ContentCollectionConfig>? ContentCollections { get; init; }

    /// <summary>
    /// Version of the MeshWeaver.Graph framework used for compilation.
    /// </summary>
    public required string FrameworkVersion { get; init; }

    /// <summary>
    /// When this release was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Gets a sanitized version of the Path suitable for file system naming.
    /// Replaces invalid characters with underscores.
    /// </summary>
    public string GetSanitizedPath()
    {
        // Replace path separators and @ with underscores
        var sanitized = Regex.Replace(Path, @"[/\\@]", "_");
        // Remove any other invalid file system characters
        sanitized = Regex.Replace(sanitized, @"[^a-zA-Z0-9_\-]", "_");
        // Remove consecutive underscores
        while (sanitized.Contains("__"))
            sanitized = sanitized.Replace("__", "_");
        return sanitized.Trim('_');
    }

    /// <summary>
    /// Computes a deterministic release hash from compilation inputs.
    /// Same inputs will always produce the same hash.
    /// </summary>
    /// <param name="code">Source code from CodeConfiguration.</param>
    /// <param name="hubConfiguration">HubConfiguration lambda source.</param>
    /// <param name="contentCollections">Content collections configuration.</param>
    /// <param name="frameworkTimestamp">Framework assembly timestamp for rebuild detection.</param>
    /// <returns>A URL-safe 16-character hash string.</returns>
    public static string ComputeRelease(
        string? code,
        string? hubConfiguration,
        IReadOnlyList<ContentCollectionConfig>? contentCollections,
        DateTimeOffset frameworkTimestamp)
    {
        using var sha256 = SHA256.Create();
        var sb = new StringBuilder();

        // Include code
        sb.Append(code ?? "");
        sb.Append('\x00');

        // Include hub configuration
        sb.Append(hubConfiguration ?? "");
        sb.Append('\x00');

        // Include content collections (sorted for determinism)
        if (contentCollections != null)
        {
            foreach (var cc in contentCollections.OrderBy(c => c.Name))
            {
                sb.Append(cc.Name ?? "");
                sb.Append(cc.SourceType ?? "");
                sb.Append(cc.BasePath ?? "");
            }
        }
        sb.Append('\x00');

        // Include framework timestamp for rebuild detection
        sb.Append(frameworkTimestamp.ToUnixTimeMilliseconds());

        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));

        // Convert to URL-safe base64 and truncate to 16 chars
        return Convert.ToBase64String(hash)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=')[..16];
    }

    /// <summary>
    /// Creates a NodeTypeRelease from compilation inputs.
    /// </summary>
    public static NodeTypeRelease Create(
        string nodeTypePath,
        string? code,
        string? hubConfiguration,
        IReadOnlyList<ContentCollectionConfig>? contentCollections,
        DateTimeOffset frameworkTimestamp,
        string frameworkVersion)
    {
        var release = ComputeRelease(code, hubConfiguration, contentCollections, frameworkTimestamp);
        return new NodeTypeRelease
        {
            Path = $"{nodeTypePath}@{release}",
            NodeTypePath = nodeTypePath,
            Release = release,
            Code = code,
            HubConfiguration = hubConfiguration,
            ContentCollections = contentCollections,
            FrameworkVersion = frameworkVersion,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// Request to create a new NodeType release.
/// Captures current code and hub configuration, compiles, and saves to persistence.
/// </summary>
/// <param name="NodeTypePath">The node type path to create a release for (e.g., "Type/Organization").</param>
/// <param name="Version">Optional version label for the release.</param>
public record CreateNodeTypeReleaseRequest(string NodeTypePath, string? Version = null)
    : IRequest<CreateNodeTypeReleaseResponse>;

/// <summary>
/// Response from creating a NodeType release.
/// </summary>
/// <param name="Release">The created release, or null if creation failed.</param>
/// <param name="AssemblyPath">Path to the compiled assembly.</param>
/// <param name="Error">Error message if creation failed.</param>
public record CreateNodeTypeReleaseResponse(
    NodeTypeRelease? Release,
    string? AssemblyPath = null,
    string? Error = null);
