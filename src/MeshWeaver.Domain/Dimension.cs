using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Domain;

public record Dimension : INamed
{
    [Key]
    public string SystemName { get; init; }
    [Sort(IsDefaultSort = true)]
    public string DisplayName { get; init; }
}
