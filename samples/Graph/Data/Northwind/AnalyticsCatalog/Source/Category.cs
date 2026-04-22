// <meshweaver>
// Id: Category
// DisplayName: Product Category
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Product category reference data.
/// </summary>
public record Category : INamed
{
    [Key]
    public int CategoryId { get; init; }

    [Required]
    public string CategoryName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    string INamed.DisplayName => CategoryName;
}
