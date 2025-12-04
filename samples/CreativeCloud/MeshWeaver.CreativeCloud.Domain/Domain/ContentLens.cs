using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.CreativeCloud.Domain;

/// <summary>
/// Represents a content lens - a specific theme or angle within a content pillar.
/// </summary>
[Display(GroupName = "Reference Data")]
public record ContentLens : INamed
{
    /// <summary>
    /// Unique identifier for the content lens.
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Reference to the content archetype this lens belongs to.
    /// </summary>
    [Dimension<ContentArchetype>]
    public required string ContentArchetypeId { get; init; }

    /// <summary>
    /// The content pillar this lens belongs to (Tactical, Aspirational, Insightful, Personal).
    /// </summary>
    public required string Pillar { get; init; }

    /// <summary>
    /// Name of the content lens.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Detailed description of the content lens and its focus areas.
    /// </summary>
    [UiControl<TextAreaControl>]
    public string? Description { get; init; }

    /// <summary>
    /// Example posts or content ideas for this lens.
    /// </summary>
    [UiControl<TextAreaControl>]
    public string? ExamplePosts { get; init; }

    /// <summary>
    /// Display order within the pillar.
    /// </summary>
    public int DisplayOrder { get; init; }

    string INamed.DisplayName => Name;
}
