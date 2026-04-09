// <meshweaver>
// Id: AnalysisContent
// DisplayName: Analysis Content
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;

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

    /// <summary>
    /// Embedded datacube rows for this analysis instance.
    /// Each BU's Analysis node stores its own datacube data here,
    /// enabling deployment-mode-independent data loading (no CSV/filesystem dependency).
    /// </summary>
    public DataCubeRow[]? Datacube { get; init; }
}

/// <summary>
/// Raw datacube row as stored in MeshNode content.
/// Contains only the base data fields; runtime-derived fields
/// (Id, BusinessUnit, Currency, display names) are added during loading.
/// </summary>
public record DataCubeRow
{
    public string Month { get; init; } = string.Empty;
    public string Quarter { get; init; } = string.Empty;
    public int Year { get; init; }
    public string LineOfBusiness { get; init; } = string.Empty;
    public string AmountType { get; init; } = string.Empty;
    public double Estimate { get; init; }
    public double? Actual { get; init; }
}
