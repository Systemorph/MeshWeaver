using System.ComponentModel.DataAnnotations;
using MeshWeaver.Kernel;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// A parsed markdown article: its raw content, pre-rendered HTML, metadata, and any executable
/// code blocks discovered during parsing. Keyed by <see cref="Url"/> within a collection.
/// </summary>
public record MarkdownElement
{
    /// <summary>The article's name (the file name without extension).</summary>
    public required string Name { get; init; }
    /// <summary>The name of the collection this article belongs to.</summary>
    public required string Collection { get; init; }
    /// <summary>The article rendered to HTML, or <c>null</c> if not pre-rendered.</summary>
    public string? PrerenderedHtml { get; init; }
    /// <summary>The source file's last-modified timestamp.</summary>
    public DateTime LastUpdated { get; set; }
    /// <summary>The markdown body with any YAML front matter removed.</summary>
    public required string Content { get; init; }
    /// <summary>The content URL that uniquely identifies this article (the record's key).</summary>
    [property: Key] public required string Url { get; init; }

    /// <summary>The article's file path within the collection.</summary>
    public required string Path { get; init; }
    /// <summary>Executable code blocks extracted from the markdown; empty when there are none.</summary>
    public IReadOnlyList<SubmitCodeRequest>? CodeSubmissions { get; set; } = [];
}
