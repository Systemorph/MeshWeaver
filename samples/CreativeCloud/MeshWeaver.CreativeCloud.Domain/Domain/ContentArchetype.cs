using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.CreativeCloud.Domain;

/// <summary>
/// Represents a content archetype that defines a person's content strategy, voice, and pillars.
/// </summary>
[Display(GroupName = "Reference Data")]
public record ContentArchetype : INamed
{
    /// <summary>
    /// Unique identifier for the content archetype.
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Name of the content archetype (typically the person's name).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The core purpose statement defining the person's content mission.
    /// </summary>
    [UiControl<TextAreaControl>]
    public required string PurposeStatement { get; init; }

    /// <summary>
    /// Description of the Tactical content pillar - actionable, how-to content.
    /// </summary>
    [UiControl<TextAreaControl>]
    public string? TacticalDescription { get; init; }

    /// <summary>
    /// Description of the Aspirational content pillar - success stories and case studies.
    /// </summary>
    [UiControl<TextAreaControl>]
    public string? AspirationalDescription { get; init; }

    /// <summary>
    /// Description of the Insightful content pillar - thought leadership and analysis.
    /// </summary>
    [UiControl<TextAreaControl>]
    public string? InsightfulDescription { get; init; }

    /// <summary>
    /// Description of the Personal content pillar - authentic stories and reflections.
    /// </summary>
    [UiControl<TextAreaControl>]
    public string? PersonalDescription { get; init; }

    string INamed.DisplayName => Name;
}
