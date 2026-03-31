// <meshweaver>
// Id: BusinessUnit
// DisplayName: Business Unit
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;

/// <summary>
/// Business unit within the FutuRe insurance group.
/// Each business unit operates in a specific region and reports
/// in a designated currency.
/// </summary>
public record BusinessUnit
{
    /// <summary>
    /// Business unit identifier.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Display name of the business unit.
    /// </summary>
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Description of the business unit's operations and scope.
    /// </summary>
    [Markdown]
    public string? Description { get; init; }

    /// <summary>
    /// Reporting currency code (ISO 4217), e.g. USD, EUR.
    /// </summary>
    [Required]
    public string Currency { get; init; } = "USD";

    /// <summary>
    /// Primary region of operations.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Icon identifier for display.
    /// </summary>
    [MeshNodeProperty(nameof(MeshNode.Icon))]
    public string Icon { get; init; } = "Building";
}
