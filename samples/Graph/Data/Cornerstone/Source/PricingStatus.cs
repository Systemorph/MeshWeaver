// <meshweaver>
// Id: PricingStatus
// DisplayName: Pricing Status Reference Data
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Represents a pricing status with display metadata.
/// </summary>
public record PricingStatus : INamed
{
    /// <summary>
    /// Status identifier.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Display name for the status.
    /// </summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Description of the status.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Emoji icon for visual representation.
    /// </summary>
    public string Emoji { get; init; } = string.Empty;

    /// <summary>
    /// Display order in lists.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Whether groups with this status should be expanded by default in catalog views.
    /// </summary>
    public bool IsExpandedByDefault { get; init; } = true;

    string INamed.DisplayName => Name;

    public static readonly PricingStatus Draft = new()
    {
        Id = "Draft",
        Name = "Draft",
        Emoji = "\ud83d\udcdd",
        Description = "Initial pricing draft, not yet submitted for quote",
        Order = 0,
        IsExpandedByDefault = true
    };

    public static readonly PricingStatus Quoted = new()
    {
        Id = "Quoted",
        Name = "Quoted",
        Emoji = "\ud83d\udcb0",
        Description = "Quote has been issued to the client",
        Order = 1,
        IsExpandedByDefault = true
    };

    public static readonly PricingStatus Bound = new()
    {
        Id = "Bound",
        Name = "Bound",
        Emoji = "\u2705",
        Description = "Policy has been bound and coverage is in effect",
        Order = 2,
        IsExpandedByDefault = true
    };

    public static readonly PricingStatus Declined = new()
    {
        Id = "Declined",
        Name = "Declined",
        Emoji = "\u274c",
        Description = "Pricing was declined by client or underwriter",
        Order = 3,
        IsExpandedByDefault = false
    };

    public static readonly PricingStatus Expired = new()
    {
        Id = "Expired",
        Name = "Expired",
        Emoji = "\u23f0",
        Description = "Quote or policy has expired",
        Order = 4,
        IsExpandedByDefault = false
    };

    public static readonly PricingStatus[] All = [Draft, Quoted, Bound, Declined, Expired];

    public static PricingStatus GetById(string? id) =>
        All.FirstOrDefault(s => s.Id == id) ?? Draft;
}
