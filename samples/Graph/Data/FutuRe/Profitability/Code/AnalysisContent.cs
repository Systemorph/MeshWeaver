// <meshweaver>
// Id: AnalysisContent
// DisplayName: Analysis Content
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Mesh.Contract;

/// <summary>
/// Content type for FutuRe analysis hub instances.
/// </summary>
public record AnalysisContent
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }
}
