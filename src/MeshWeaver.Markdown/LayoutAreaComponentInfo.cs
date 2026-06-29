using Markdig.Parsers;
using Markdig.Syntax;
using MeshWeaver.Data;

namespace MeshWeaver.Markdown;

/// <summary>
/// A Markdig block representing an embedded or linked layout-area reference parsed from a unified
/// content reference (<c>@</c>/<c>@@</c> syntax or a <c>layout</c> code block). Carries either a raw
/// path resolved at render time, or a pre-parsed address/area/id triple.
/// </summary>
public class LayoutAreaComponentInfo : ContainerBlock
{
    /// <summary>
    /// Constructor for raw path references. The path will be resolved at render time
    /// using IMeshCatalog.ResolvePathAsync for proper address matching.
    /// </summary>
    /// <param name="rawPath">The raw path (e.g., "Systemorph/Marketing/BeyondPoC")</param>
    /// <param name="blockParser">The block parser</param>
    /// <param name="isInline">True for @@ (inline), false for @ (hyperlink)</param>
    public LayoutAreaComponentInfo(string rawPath, BlockParser blockParser, bool isInline = false) : base(blockParser)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            throw new ArgumentException("Path cannot be null or empty", nameof(rawPath));

        RawPath = rawPath;
        IsInline = isInline;

        // For raw paths, we don't pre-parse into address/area/id
        // The rendering layer will use IMeshCatalog.ResolvePathAsync
        Address = rawPath;
        Area = null;
        Id = null;
    }

    /// <summary>
    /// Constructor for pre-parsed references (used by parser for keyword-based paths like data:, content:, area:).
    /// </summary>
    /// <param name="originalPath">The original path as written in markdown (e.g., "MeshWeaver/UCR/content:logo.svg")</param>
    /// <param name="address">The resolved address part</param>
    /// <param name="area">The resolved area name (e.g., "$Content")</param>
    /// <param name="id">The area ID or path after the prefix</param>
    /// <param name="blockParser">The block parser</param>
    /// <param name="isInline">True for @@ (inline), false for @ (hyperlink)</param>
    public LayoutAreaComponentInfo(string originalPath, string address, string? area, string? id, BlockParser blockParser, bool isInline = false) : base(blockParser)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("Address cannot be null or empty", nameof(address));

        RawPath = originalPath;
        Address = address;
        Area = area;
        Id = id;
        IsInline = isInline;
    }

    /// <summary>
    /// The original raw path as written in markdown (e.g., "Systemorph/Marketing/BeyondPoC").
    /// Used for proper address resolution at render time.
    /// </summary>
    public string RawPath { get; }

    /// <summary>
    /// The area name (may be null for raw path references).
    /// For keyword references: the area name (e.g., "$Data", "$Content", or custom area)
    /// </summary>
    public string? Area { get; }

    /// <summary>
    /// The resolved address (for raw paths, this equals RawPath until resolved at render time).
    /// </summary>
    public object Address { get; }

    /// <summary>
    /// The area ID or additional path after the area name.
    /// </summary>
    public object? Id { get; }

    /// <summary>
    /// When true (@@), render inline content. When false (@), render as hyperlink.
    /// </summary>
    public bool IsInline { get; }

    /// <summary>
    /// The layout-area reference (<see cref="Area"/> + <see cref="Id"/>) used to subscribe to / link the area.
    /// </summary>
    public LayoutAreaReference Reference =>
        new(Area) { Id = Id };
}

/// <summary>
/// A parsed source reference describing a typed pointer to mesh content.
/// </summary>
/// <param name="Type">The reference type (e.g. data, content, area).</param>
/// <param name="Reference">The reference value (path or id within the type).</param>
/// <param name="Address">The resolved node address the reference targets.</param>
public record SourceInfo(string Type, string Reference, string Address);

