using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Domain;

public record Dimension : INamed
{
    [Key]
    public required string SystemName { get; init; }
    [Sort(IsDefaultSort = true)]
    public required string DisplayName { get; init; }
}
