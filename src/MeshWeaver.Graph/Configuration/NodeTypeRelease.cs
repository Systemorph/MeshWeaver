using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Markdown;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Represents a compiled release of a NodeType. Lives on disk as a MeshNode
/// of type <c>Release</c> at <c>{nodeTypePath}/Release/{version}</c>;
/// immutable once committed.
///
/// <para>Releases are first-class durable artefacts â€” the GUI's Create-Release
/// button triggers a compile activity, and on success the watcher writes a
/// new Release MeshNode with the compiled <see cref="AssemblyPath"/> + the
/// markdown <see cref="Notes"/> the user authored. Old releases stay on disk
/// (one DLL per version) so older instances of the NodeType can keep running
/// on their loaded ALC while new instances bind to the latest succeeded
/// release. See <c>Doc/Architecture/Postmortems/NodeTypeReleaseRedesign.md</c>.</para>
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
    /// User-facing version label (e.g. "1.2.0", "feature-x"). When the release
    /// was auto-stamped, defaults to <c>{yyyyMMddHHmmss}-{8charHash}</c> for
    /// chronological sortability. When the user supplied an explicit version
    /// at create-release time, that value wins.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Author-written release notes â€” free-form markdown body shown at the top
    /// of the Release detail view and in the release-history list. Sourced from
    /// <c>NodeTypeDefinition.ReleaseNotes</c> at compile time and copied here
    /// so the release is a self-contained snapshot (later edits to the
    /// NodeType's <c>ReleaseNotes</c> field don't rewrite history).
    /// </summary>
    public MarkdownContent? Notes { get; init; }

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
    /// Filesystem path of the compiled DLL for this release (set when the
    /// compile activity terminated <c>Succeeded</c>; null on failure).
    /// Stable per <c>(NodeTypePath, Version)</c> â€” overwriting in place is
    /// safe; deleting other versions' DLLs is forbidden because their ALCs
    /// may still be holding the file handles for live instances.
    /// <para>
    /// ðŸš¨ Local-process hint only â€” not cross-silo durable. For cross-silo
    /// activation, use <see cref="AssemblyCollection"/> + <see cref="AssemblyContentPath"/>:
    /// every silo fetches the bytes from the same content-collection blob, no
    /// shared filesystem required.
    /// </para>
    /// </summary>
    public string? AssemblyPath { get; init; }

    /// <summary>
    /// Content-collection name where this release's compiled assembly bytes
    /// live (e.g. <c>"nodetype-cache"</c>). Pair with <see cref="AssemblyContentPath"/>
    /// to fetch via <c>IContentCollection</c>. Set by the compile watcher
    /// after uploading the assembly to the blob container.
    /// <para>
    /// This is the cross-silo durable assembly reference: every silo (Aspire
    /// replica, Orleans grain host, monolith) sees the same blob and can
    /// hydrate its local ALC on demand. <see cref="AssemblyPath"/> is the
    /// process-local cache path the producing silo wrote; remote silos
    /// download into their own cache via this content-collection reference.
    /// </para>
    /// </summary>
    public string? AssemblyCollection { get; init; }

    /// <summary>
    /// Path inside <see cref="AssemblyCollection"/> where this release's
    /// compiled assembly bytes live (e.g. <c>"TestData/PinType/v2-abc123.dll"</c>).
    /// </summary>
    public string? AssemblyContentPath { get; init; }

    /// <summary>
    /// Path to the corresponding <c>.pdb</c> file, when emitted alongside the
    /// DLL. Same naming convention as <see cref="AssemblyPath"/>.
    /// </summary>
    public string? PdbPath { get; init; }

    /// <summary>
    /// Mirror of the compile activity's terminal status. <c>Succeeded</c> means
    /// the release is loadable and is a candidate for "active release"
    /// resolution; <c>Failed</c> means the previous succeeded release stays
    /// active and this release is kept only as history.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Path to the <c>NodeTypeCompilation</c> activity that produced this
    /// release â€” link to the live message log + diagnostics. Set even on
    /// failed releases so triagers can drill into the Roslyn output.
    /// </summary>
    public string? CompilationActivityPath { get; init; }

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
