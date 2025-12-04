using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.CreativeCloud.Domain;

/// <summary>
/// Represents a story arch - a major theme or narrative that stories are grouped under.
/// </summary>
[Display(GroupName = "Content")]
public record StoryArch : INamed, IHasDependencies
{
    /// <summary>
    /// Unique identifier for the story arch.
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Name of the story arch.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of the story arch and its scope.
    /// </summary>
    [UiControl<TextAreaControl>]
    public string? Description { get; init; }

    /// <summary>
    /// The overarching theme of this story arch.
    /// </summary>
    public string? Theme { get; init; }

    string INamed.DisplayName => Name;

    /// <summary>
    /// Dependencies for this story arch (none).
    /// </summary>
    public IReadOnlyCollection<string> Dependencies => Array.Empty<string>();
}
