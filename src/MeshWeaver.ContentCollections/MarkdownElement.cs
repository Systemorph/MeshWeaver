using System.ComponentModel.DataAnnotations;
using MeshWeaver.Kernel;

namespace MeshWeaver.ContentCollections;

public record MarkdownElement
{
    public required string Name { get; init; }
    public required string Collection { get; init; }
    public string? PrerenderedHtml { get; init; }
    public DateTime LastUpdated { get; set; }
    public required string Content { get; init; }
    [property: Key] public required string Url { get; init; }

    public required string Path { get; init; }
    public IReadOnlyList<SubmitCodeRequest>? CodeSubmissions { get; set; } = [];
}
