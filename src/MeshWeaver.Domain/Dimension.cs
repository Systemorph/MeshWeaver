using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Domain;

/// <summary>
/// A named dimension member identified by a stable system name and shown via a display name.
/// </summary>
public record Dimension : INamed
{
    /// <summary>
    /// The stable, machine-readable key that uniquely identifies the dimension member.
    /// </summary>
    [Key]
    public required string SystemName { get; init; }
    /// <summary>
    /// The human-readable label shown for the dimension member; the default sort field.
    /// </summary>
    [Sort(IsDefaultSort = true)]
    public required string DisplayName { get; init; }
}
