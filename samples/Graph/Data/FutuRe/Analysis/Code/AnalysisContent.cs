// <meshweaver>
// Id: AnalysisContent
// DisplayName: Analysis Content
// </meshweaver>

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Content type for FutuRe analysis hub instances.
/// </summary>
public record AnalysisContent
{
    /// <summary>
    /// Unique identifier for the analysis instance.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Display name for the analysis hub.
    /// </summary>
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Brief description of this analysis scope.
    /// </summary>
    public string? Description { get; init; }
}
