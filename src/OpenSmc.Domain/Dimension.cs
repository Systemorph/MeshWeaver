using System.ComponentModel.DataAnnotations;

namespace OpenSmc.Domain;

public record Dimension : INamed
{
    [Key]
    public string SystemName { get; init; }
    public string DisplayName { get; init; }
}
