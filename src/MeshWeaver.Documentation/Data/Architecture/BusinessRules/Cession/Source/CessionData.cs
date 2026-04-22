// <meshweaver>
// Id: CessionData
// DisplayName: Cession Data Model
// </meshweaver>

using MeshWeaver.Domain;

/// <summary>
/// Content type for a Cession node — holds the layer configuration.
/// </summary>
public record CessionData
{
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public double AttachmentPoint { get; init; }

    public double Limit { get; init; }
}

/// <summary>
/// A single claim cashflow.
/// </summary>
public record Cashflow
{
    [Key] public string ClaimId { get; init; } = string.Empty;
    public string LineOfBusiness { get; init; } = string.Empty;
    public double GrossAmount { get; init; }
}

/// <summary>
/// Excess-of-Loss layer definition.
/// </summary>
public record ExcessOfLossLayer
{
    [Key] public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public double AttachmentPoint { get; init; }
    public double Limit { get; init; }
}

/// <summary>
/// Result of applying a layer to a cashflow.
/// </summary>
public record CededCashflow
{
    [Key] public string ClaimId { get; init; } = string.Empty;
    public string LayerId { get; init; } = string.Empty;
    public double GrossAmount { get; init; }
    public double CededAmount { get; init; }
    public double RetainedAmount { get; init; }
}
